import * as vscode from "vscode";
import * as path from "node:path";
import { CopitDb } from "./db";
import { AgentManager, patternForDenial } from "./agents/manager";
import { locateCli } from "./agents/cli";
import { AutomationEngine, ACTIONS, TRIGGERS } from "./automation";
import { Agent, Automation, Budget, COLUMNS, PERMISSION_OPS, PermissionLevel, newId } from "./model";
import {
  PermissionRule, defaultCopitRules, describeRule, globalSettingsPath, matrixLevel, projectSettingsPath, readClaudeRules,
  ruleId, samePath, setMatrixLevel, writeClaudeRule,
} from "./permissions";
import { evaluate, isoWeek, newThresholds, overview, periodRange } from "./budgets";
import { GitLab, Jira } from "./integrations";
import { AgentsTree, AutomationsTree } from "./trees";
import { Panel } from "./panels";
import * as G from "./git";
import { downloadAsset, ghToken, isNewer, latestRelease } from "./updater";
import { seedDemo } from "./demo";

let db: CopitDb;
let manager: AgentManager;
let engine: AutomationEngine;
let repos: string[] = [];
const tests: Record<string, string> = {};

const cfg = () => vscode.workspace.getConfiguration("copit");

export async function activate(ctx: vscode.ExtensionContext) {
  const version: string = ctx.extension.packageJSON.version;
  db = await CopitDb.open(path.join(ctx.globalStorageUri.fsPath, "copit.db"), path.join(ctx.extensionPath, "dist"));
  ctx.subscriptions.push({ dispose: () => db.flush() });

  if (!db.get("copitRulesSeeded", false)) { db.set("copitRules", defaultCopitRules()); db.set("copitRulesSeeded", true); }

  const secrets = ctx.secrets;
  let jiraToken = await secrets.get("copit.jira");
  let gitlabToken = await secrets.get("copit.gitlab");
  ctx.subscriptions.push(secrets.onDidChange(async () => {
    jiraToken = await secrets.get("copit.jira"); gitlabToken = await secrets.get("copit.gitlab");
  }));
  const jira = () => {
    const url = cfg().get<string>("jira.url");
    if (!url || !jiraToken) return undefined;
    return new Jira(url, jiraToken, { basicUser: cfg().get("jira.authMode") === "basic" ? cfg().get<string>("jira.user") : undefined, ignoreCerts: !!cfg().get("ignoreCertificateErrors") });
  };
  const gitlab = () => {
    const url = cfg().get<string>("gitlab.url");
    return url && gitlabToken ? new GitLab(url, gitlabToken, !!cfg().get("ignoreCertificateErrors")) : undefined;
  };

  // ---------------------------------------------------------- Repos
  const loadRepos = async () => {
    const roots = await Promise.all((vscode.workspace.workspaceFolders ?? []).map((f) => G.repoRoot(f.uri.fsPath).then((r) => r ?? f.uri.fsPath)));
    const known: string[] = db.get("knownRepos", []);
    repos = [...new Set([...roots, ...known])];
    db.set("knownRepos", repos.slice(0, 30));
    Panel.refreshAll();
  };
  await loadRepos();
  ctx.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(loadRepos));
  const repoName = (p: string) => path.basename(p);

  // ---------------------------------------------------------- Regeln
  const allRules = (): PermissionRule[] => {
    const copit: PermissionRule[] = db.get("copitRules", []);
    const paused: PermissionRule[] = db.get("pausedRules", []);
    const claude = [
      ...readClaudeRules(globalSettingsPath(), "global"),
      ...repos.flatMap((r) => readClaudeRules(projectSettingsPath(r), "project", r)),
    ];
    return [...claude, ...paused.map((p) => ({ ...p, enabled: false })), ...copit];
  };
  const budgets = (): Budget[] => db.get("budgets", []);

  // ---------------------------------------------------------- Kern
  manager = new AgentManager(db, () => ({
    claudePath: cfg().get("claudePath") || undefined, copilotPath: cfg().get("copilotPath") || undefined,
    claudeModel: cfg().get("claudeModel") || undefined, instructions: cfg().get("globalInstructions") ?? "", useWorktrees: cfg().get("useWorktrees") ?? true,
  }), allRules, budgets);

  const notify = (msg: string, level: "info" | "warn" | "error" = "info") => {
    const fn = level === "error" ? vscode.window.showErrorMessage : level === "warn" ? vscode.window.showWarningMessage : vscode.window.showInformationMessage;
    void fn(`Copit: ${msg}`);
  };
  engine = new AutomationEngine({ db, agents: manager, jira, gitlab, notify, repos: () => repos, pollSeconds: () => cfg().get("pollSeconds") ?? 60 });
  engine.seedDefaults();
  engine.start();
  ctx.subscriptions.push({ dispose: () => engine.dispose() });

  // ---------------------------------------------------------- Views
  const agentsTree = new AgentsTree(() => manager.list(), (id) => manager.isRunning(id));
  const autoTree = new AutomationsTree(() => db.automations());
  ctx.subscriptions.push(vscode.window.registerTreeDataProvider("copit.agents", agentsTree), vscode.window.registerTreeDataProvider("copit.automations", autoTree));

  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.command = "copit.openBoard";
  status.show();
  ctx.subscriptions.push(status);
  const updateStatus = () => {
    const list = manager.list();
    const running = list.filter((a) => manager.isRunning(a.id)).length;
    const need = list.filter((a) => a.status === "need_input").length;
    const week = overview(db.runs(periodRange("week").from, Date.now() + 1), new Map(list.map((a) => [a.id, a]))).week;
    status.text = `${running ? `$(sync~spin) ${running} aktiv` : "$(layers) Copit"}${need ? `  $(question) ${need}` : ""}  $${week.cost.toFixed(2)} KW`;
    status.tooltip = `${list.length} Agents · ${need} brauchen Input · ${week.hours.toFixed(1)} h Agent-Zeit diese Woche`;
  };
  updateStatus();

  manager.on("changed", (a: Agent) => { agentsTree.refresh(); Panel.refreshAll("board"); Panel.all.get("agent:" + a.id)?.refresh(); updateStatus(); });
  manager.on("event", (e) => Panel.all.get("agent:" + e.agentId)?.refresh(200));
  manager.on("delta", (d) => Panel.all.get("agent:" + d.agentId)?.refresh(250));
  manager.on("removed", () => { agentsTree.refresh(); Panel.refreshAll("board"); });
  manager.on("runFinished", (a: Agent) => {
    filesCache.delete(a.id);
    Panel.refreshAll("calendar"); Panel.refreshAll("budgets"); autoTree.refresh(); updateStatus();
    checkBudgets();
  });

  // ---------------------------------------------------------- Budgets
  const checkBudgets = () => {
    const list = budgets();
    const runs = db.runs(0, Date.now() + 1);
    const agents = new Map(manager.list().map((a) => [a.id, a]));
    let changed = false;
    for (const b of list) {
      const s = evaluate(b, runs, agents);
      for (const pct of newThresholds(s)) {
        b.notified = [...(b.notified ?? []), `${s.periodKey}:${pct}`];
        changed = true;
        notify(`Budget "${b.name}" hat ${pct} % erreicht (${b.type === "cost" ? "$" + s.used.toFixed(2) : s.used.toFixed(1) + " h"} von ${b.amount}${b.type === "cost" ? " USD" : " h"}, ${s.periodLabel}).`, pct >= 100 ? "error" : "warn");
      }
    }
    if (changed) db.set("budgets", list);
  };

  // ---------------------------------------------------------- Dateien / Diff
  const filesCache = new Map<string, { path: string; added: number; removed: number; status: string }[]>();
  const files = async (a: Agent) => {
    if (!filesCache.has(a.id)) {
      try { filesCache.set(a.id, (await G.diffStat(a.cwd, a.baseSha)).files); } catch { filesCache.set(a.id, []); }
    }
    return filesCache.get(a.id)!;
  };
  ctx.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider("copit-base", {
    async provideTextDocumentContent(uri) {
      const q = new URLSearchParams(uri.query);
      const a = manager.get(q.get("agent") ?? "");
      return a?.baseSha ? G.showFile(a.cwd, a.baseSha, q.get("path") ?? "") : "";
    },
  }));
  const diffUris = (a: Agent, p: string) => {
    const right = vscode.Uri.file(path.join(a.cwd, p));
    const left = vscode.Uri.from({ scheme: "copit-base", path: "/" + p.replace(/\\/g, "/"), query: `agent=${a.id}&path=${encodeURIComponent(p)}` });
    return { left, right };
  };

  // ---------------------------------------------------------- Panels
  const openAgent = (id: string) => {
    const a0 = manager.get(id);
    if (!a0) return;
    Panel.show(ctx, "agent:" + id, `${a0.ticket ? a0.ticket + " · " : ""}${a0.title}`.slice(0, 40), "agent", () => ({
      state: async () => {
        const a = manager.get(id)!;
        const running = manager.isRunning(id);
        const startedAt = running ? db.events(id).filter((e) => e.kind === "user").pop()?.ts : undefined;
        return {
          agent: { ...a, running, startedAt, pendingPermissions: (a.pendingPermissions ?? []).map((p) => ({ ...p, pattern: patternForDenial(p.tool, p.input) })) },
          events: db.events(id).slice(-1500), files: await files(a), columns: COLUMNS,
        };
      },
      onAct: async (m) => {
        const a = manager.get(id)!;
        switch (m.act) {
          case "reply": void manager.reply(id, m.value!); break;
          case "run": void manager.start(id, a.turns ? "Fahre mit der Aufgabe fort." : undefined); break;
          case "stop": manager.stop(id); break;
          case "status": manager.setStatus(id, m.value as any, true); break;
          case "answer": void manager.reply(id, m.value!); break;
          case "approve": void manager.approve(id, [m.arg!]); break;
          case "always": {
            const rules: PermissionRule[] = db.get("copitRules", []);
            rules.push({ id: ruleId("copit", m.arg!, a.repo), pattern: m.arg!, level: "allow", source: "copit", repo: a.repo, enabled: true });
            db.set("copitRules", rules);
            void manager.approve(id, [m.arg!]);
            Panel.refreshAll("customize");
            break;
          }
          case "terminal": vscode.window.createTerminal({ name: a.title.slice(0, 30), cwd: a.cwd }).show(); break;
          case "diff": { const { left, right } = diffUris(a, m.arg!); await vscode.commands.executeCommand("vscode.diff", left, right, `${m.arg} (Agent)`); break; }
          case "review": {
            const list = await files(a);
            try {
              await vscode.commands.executeCommand("vscode.changes", `Review: ${a.title}`, list.map((f) => { const { left, right } = diffUris(a, f.path); return [right, left, right]; }));
            } catch {
              for (const f of list.slice(0, 8)) { const { left, right } = diffUris(a, f.path); await vscode.commands.executeCommand("vscode.diff", left, right, f.path, { preview: false }); }
            }
            break;
          }
        }
      },
    }));
  };

  const openBoard = () => Panel.show(ctx, "board", "Copit Board", "board", () => ({
    state: () => {
      const list = manager.list();
      const runs = db.runs(0, Date.now() + 1);
      const amap = new Map(list.map((a) => [a.id, a]));
      return {
        columns: COLUMNS,
        agents: list.map((a) => ({ ...a, running: manager.isRunning(a.id) })),
        projects: [...new Set(list.map((a) => a.project))],
        budget: budgets().slice(0, 3).map((b) => { const s = evaluate(b, runs, amap); return { label: b.name, fraction: s.fraction, text: `${s.used.toFixed(2)} / ${b.amount} (${s.periodLabel})` }; }),
      };
    },
    onAct: async (m) => {
      if (m.act === "open") openAgent(m.arg!);
      else if (m.act === "move") manager.setStatus(m.arg!, m.value as any, true);
      else if (m.act === "new") openNewAgent();
      else if (m.act === "calendar") openCalendar();
      else if (m.act === "menu") {
        const a = manager.get(m.arg!);
        if (!a) return;
        const pick = await vscode.window.showQuickPick([
          ...(manager.isRunning(a.id) ? ["Stoppen"] : ["Starten / Fortsetzen"]), "Öffnen", "Terminal im Arbeitsordner", "Status ändern…", "Löschen",
        ], { title: a.title });
        if (pick === "Stoppen") manager.stop(a.id);
        else if (pick === "Starten / Fortsetzen") void manager.start(a.id, a.turns ? "Fahre mit der Aufgabe fort." : undefined);
        else if (pick === "Öffnen") openAgent(a.id);
        else if (pick === "Terminal im Arbeitsordner") vscode.window.createTerminal({ cwd: a.cwd, name: a.title.slice(0, 30) }).show();
        else if (pick === "Status ändern…") {
          const s = await vscode.window.showQuickPick(COLUMNS.map((c) => ({ label: c.title, id: c.id })), { title: "Neuer Status" });
          if (s) manager.setStatus(a.id, s.id, true);
        } else if (pick === "Löschen") await deleteAgent(a.id);
      }
    },
  }));

  const openNewAgent = (prefill?: { title?: string; prompt?: string; ticket?: string }) => {
    let error: string | undefined;
    let fill = prefill;
    const p = Panel.show(ctx, "newagent", "Neuer Agent", "newagent", () => ({
      state: () => {
        const s = { repos: repos.map((r) => ({ path: r, name: repoName(r) })), provider: cfg().get("defaultProvider") ?? "claude", model: cfg().get("claudeModel") ?? "", prefill: fill, jira: !!jira(), error };
        fill = undefined;
        return s;
      },
      onAct: async (m) => {
        if (m.act === "jira") {
          const i = await jira()?.issue(m.value!);
          if (!i) { error = `Ticket ${m.value} nicht gefunden`; return; }
          error = undefined;
          fill = { title: i.summary, ticket: i.key, prompt: `Arbeite an Ticket ${i.key}: ${i.summary}\n\n${i.description}`.trim() };
        } else if (m.act === "create") {
          const f = JSON.parse(m.value!);
          if (!f.repo) { error = "Kein Projekt – öffne einen Ordner in VSCodium"; return; }
          const a = await manager.create({ title: f.title, prompt: f.prompt, repo: f.repo, ticket: f.ticket, branch: f.branch, provider: f.provider, mode: f.mode, model: f.model, start: false });
          Panel.all.get("newagent")?.panel.dispose();
          openAgent(a.id);
          if (f.start) void manager.start(a.id);
        }
      },
    }));
    if (prefill) { fill = prefill; p.refresh(); }
  };

  let calOffset = 0;
  const openCalendar = () => Panel.show(ctx, "calendar", "Agent-Kalender", "calendar", () => ({
    state: () => {
      const w = periodRange("week", new Date(Date.now() + calOffset * 7 * 86400000));
      const agents = new Map(manager.list().map((a) => [a.id, a]));
      const blocks = db.runs(w.from, w.to).map((r) => {
        const a = agents.get(r.agentId);
        return { agentId: r.agentId, title: a?.title ?? "(gelöscht)", ticket: a?.ticket, project: a?.project ?? "?", start: r.startedAt, end: r.endedAt ?? Date.now(),
          aiMs: r.aiMs, cost: r.costUsd, ok: r.ok, running: !r.endedAt && manager.isRunning(r.agentId) };
      });
      return { weekStart: w.from, blocks, kw: isoWeek(new Date(w.from)) };
    },
    onAct: (m) => {
      if (m.act === "nav") calOffset = m.value === "0" ? 0 : calOffset + Number(m.value);
      else if (m.act === "open") openAgent(m.arg!);
    },
  }));

  let picked: string | undefined;
  const openBudgets = () => Panel.show(ctx, "budgets", "Budgets", "budgets", () => ({
    state: () => {
      const runs = db.runs(0, Date.now() + 1);
      const agents = new Map(manager.list().map((a) => [a.id, a]));
      const list = budgets();
      const s = {
        budgets: list, statuses: list.map((b) => { const st = evaluate(b, runs, agents); return { id: b.id, used: st.used, fraction: st.fraction, periodLabel: st.periodLabel }; }),
        overview: overview(runs, agents), repos: repos.map((r) => ({ path: r, name: repoName(r) })), picked,
      };
      picked = undefined;
      return s;
    },
    onAct: async (m) => {
      const list = budgets();
      if (m.act === "save") {
        const b: Budget = JSON.parse(m.value!);
        const i = list.findIndex((x) => x.id === b.id);
        if (i >= 0) list[i] = { ...list[i], ...b }; else list.push(b);
        db.set("budgets", list);
        checkBudgets();
        updateStatus(); Panel.refreshAll("board");
      } else if (m.act === "delete") db.set("budgets", list.filter((x) => x.id !== m.arg));
      else if (m.act === "browse") {
        const f = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: "Ordner übernehmen" });
        if (f?.[0]) picked = (await G.repoRoot(f[0].fsPath)) ?? f[0].fsPath;
      }
    },
  }));

  const openAutomations = (select?: string) => {
    const p = Panel.show(ctx, "automations", "Automations", "automations", () => ({
      state: () => ({
        automations: db.automations(), triggers: TRIGGERS, actions: ACTIONS, log: db.automationLog(100),
        vars: ["title", "ticket", "branch", "project", "status", "summary", "lines", "files", "cost", "agentMinutes", "aiMinutes", "sha", "message", "pipelineUrl", "issueSummary", "from", "to"],
      }),
      onAct: async (m) => {
        if (m.act === "toggle") { const a = db.automations().find((x) => x.id === m.arg); if (a) { a.enabled = !a.enabled; db.saveAutomation(a); } }
        else if (m.act === "save") { const a: Automation = JSON.parse(m.value!); db.saveAutomation(a); }
        else if (m.act === "delete") db.deleteAutomation(m.arg!);
        else if (m.act === "test") {
          const a: Automation = JSON.parse(m.value!);
          if (a.id.startsWith("new-")) { notify("Bitte zuerst speichern."); return; }
          const agent = manager.list()[0];
          await engine.test(a, agent);
          notify(`Testlauf "${a.name}" ausgeführt${agent ? ` mit Agent "${agent.title}"` : ""}.`);
        }
        autoTree.refresh();
      },
    }));
    if (select) p.post({ type: "select", id: select });
  };

  const settingKeys = ["claudePath", "copilotPath", "defaultProvider", "claudeModel", "globalInstructions", "useWorktrees", "jira.url", "jira.authMode", "jira.user", "gitlab.url", "ignoreCertificateErrors", "pollSeconds", "update.repo", "update.auto"];
  const openCustomize = () => Panel.show(ctx, "customize", "Customize", "permissions", () => ({
    state: async () => {
      const rules = allRules().map((r) => ({ ...r, repoName: r.repo ? repoName(r.repo) : undefined, ...describeRule(r.pattern) }));
      const matrix: Record<string, Record<string, PermissionLevel>> = {};
      const copit: PermissionRule[] = db.get("copitRules", []);
      for (const op of PERMISSION_OPS) {
        matrix[op.id] = { "": matrixLevel(copit, op.id) };
        for (const r of repos) matrix[op.id][r] = matrixLevel(copit, op.id, r);
      }
      const settings: Record<string, unknown> = {};
      for (const k of settingKeys) settings[k] = cfg().get(k);
      return {
        rules, repos: repos.map((r) => ({ path: r, name: repoName(r) })), ops: PERMISSION_OPS, matrix, settings,
        secrets: { jira: !!jiraToken, gitlab: !!gitlabToken, github: !!(await secrets.get("copit.github")) }, tests, version,
      };
    },
    onAct: async (m) => {
      const copit: PermissionRule[] = db.get("copitRules", []);
      const paused: PermissionRule[] = db.get("pausedRules", []);
      const find = () => allRules().find((r) => r.id === m.arg);
      const fileOf = (r: PermissionRule) => r.source === "global" ? globalSettingsPath() : projectSettingsPath(r.repo!);
      switch (m.act) {
        case "rule.level": {
          const r = find(); if (!r) return;
          const level = m.value as PermissionLevel;
          if (r.source === "copit") { const x = copit.find((c) => c.id === r.id); if (x) x.level = level; db.set("copitRules", copit); }
          else if (!r.enabled) { const x = paused.find((c) => c.id === r.id); if (x) x.level = level; db.set("pausedRules", paused); }
          else writeClaudeRule(fileOf(r), r.pattern, level);
          break;
        }
        case "rule.toggle": {
          const r = find(); if (!r) return;
          if (r.source === "copit") { const x = copit.find((c) => c.id === r.id); if (x) x.enabled = !x.enabled; db.set("copitRules", copit); }
          else if (r.enabled) { writeClaudeRule(fileOf(r), r.pattern, null); db.set("pausedRules", [...paused, { ...r, enabled: false }]); }
          else { writeClaudeRule(fileOf(r), r.pattern, r.level); db.set("pausedRules", paused.filter((p) => p.id !== r.id)); }
          break;
        }
        case "rule.delete": {
          const r = find(); if (!r) return;
          const ok = await vscode.window.showWarningMessage(`Regel ${r.pattern} löschen?`, { modal: true }, "Löschen");
          if (ok !== "Löschen") return;
          if (r.source === "copit") db.set("copitRules", copit.filter((c) => c.id !== r.id));
          else if (!r.enabled) db.set("pausedRules", paused.filter((p) => p.id !== r.id));
          else writeClaudeRule(fileOf(r), r.pattern, null);
          break;
        }
        case "rule.add": {
          const n = JSON.parse(m.value!);
          if (n.source === "copit") db.set("copitRules", [...copit.filter((c) => c.id !== ruleId("copit", n.pattern, n.repo)), { id: ruleId("copit", n.pattern, n.repo), pattern: n.pattern, level: n.level, source: "copit", repo: n.repo, enabled: true }]);
          else writeClaudeRule(n.source === "global" ? globalSettingsPath() : projectSettingsPath(n.repo), n.pattern, n.level);
          break;
        }
        case "matrix.set": {
          const [op, repo] = m.arg!.split("|");
          db.set("copitRules", setMatrixLevel(copit, op, m.value as PermissionLevel, repo || undefined));
          break;
        }
        case "save": {
          const v = JSON.parse(m.value!);
          for (const k of settingKeys) if (k in v && v[k] !== cfg().get(k)) await cfg().update(k, v[k], vscode.ConfigurationTarget.Global);
          notify("Einstellungen gespeichert.");
          break;
        }
        case "secret": {
          const label = m.arg === "jira" ? "Jira Personal Access Token" : m.arg === "gitlab" ? "GitLab Private Access Token" : "GitHub Token (repo-Lesezugriff)";
          const t = await vscode.window.showInputBox({ prompt: label, password: true, ignoreFocusOut: true });
          if (t !== undefined) await secrets.store("copit." + m.arg, t.trim());
          break;
        }
        case "test": {
          const k = m.arg!;
          tests[k] = "teste…"; Panel.refreshAll("customize");
          try {
            if (k === "jira") { const me = await jira()?.myself(); tests[k] = me ? `✓ ${me.displayName}` : "nicht konfiguriert"; }
            else if (k === "gitlab") { const gl = gitlab(); tests[k] = gl ? (await gl.defaultBranch("").then(() => "✓").catch((e) => /404/.test(e.message) ? "✓ verbunden" : "✗ " + e.message)) : "nicht konfiguriert"; }
            else { const c = locateCli(k, cfg().get(k + "Path") || undefined); tests[k] = c ? `✓ ${c.display}` : "✗ nicht gefunden"; }
          } catch (e: any) { tests[k] = "✗ " + e.message; }
          break;
        }
        case "update": await checkUpdate(true); break;
      }
    },
  }));

  // ---------------------------------------------------------- Updater
  const checkUpdate = async (manual: boolean) => {
    const repo = cfg().get<string>("update.repo");
    if (!repo) { if (manual) tests.update = "kein Repository hinterlegt"; return; }
    try {
      const token = (await secrets.get("copit.github")) || (await ghToken());
      const rel = await latestRelease(repo, token);
      if (!rel) { tests.update = "keine Releases"; return; }
      if (!isNewer(rel.version, version)) { tests.update = `aktuell (neuestes: ${rel.tag})`; return; }
      tests.update = `Update ${rel.tag} verfügbar`;
      const choice = manual ? "Installieren" : await vscode.window.showInformationMessage(`Copit ${rel.tag} ist verfügbar.`, "Installieren", "Später");
      if (choice !== "Installieren") return;
      const file = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Copit ${rel.tag} wird geladen…` }, () => downloadAsset(rel, token));
      await vscode.commands.executeCommand("workbench.extensions.installExtension", vscode.Uri.file(file));
      tests.update = `${rel.tag} installiert`;
      const r = await vscode.window.showInformationMessage(`Copit ${rel.tag} installiert. Jetzt neu laden?`, "Neu laden");
      if (r) await vscode.commands.executeCommand("workbench.action.reloadWindow");
    } catch (e: any) {
      tests.update = "✗ " + e.message;
      if (manual) notify("Update-Prüfung: " + e.message, "warn");
    } finally { Panel.refreshAll("customize"); }
  };
  if (cfg().get("update.auto")) setTimeout(() => void checkUpdate(false), 15000);
  const updTimer = setInterval(() => { if (cfg().get("update.auto")) void checkUpdate(false); }, 3600000);
  ctx.subscriptions.push({ dispose: () => clearInterval(updTimer) });

  // ---------------------------------------------------------- Commands
  const deleteAgent = async (id: string) => {
    const a = manager.get(id);
    if (!a) return;
    const ok = await vscode.window.showWarningMessage(`Agent "${a.title}" löschen?${a.worktree ? " Der Worktree wird entfernt (Branch bleibt)." : ""}`, { modal: true }, "Löschen");
    if (ok === "Löschen") { Panel.all.get("agent:" + id)?.panel.dispose(); await manager.remove(id); }
  };
  const reg = (id: string, fn: (...a: any[]) => unknown) => ctx.subscriptions.push(vscode.commands.registerCommand(id, fn));
  reg("copit.newAgent", () => openNewAgent());
  reg("copit.openBoard", openBoard);
  reg("copit.openCalendar", openCalendar);
  reg("copit.openPermissions", openCustomize);
  reg("copit.openBudgets", openBudgets);
  reg("copit.openAutomations", (id?: string) => openAutomations(typeof id === "string" ? id : undefined));
  reg("copit.openAgent", (id: string) => openAgent(id));
  reg("copit.stopAgent", (n: any) => manager.stop(n?.agent?.id ?? n));
  reg("copit.deleteAgent", (n: any) => deleteAgent(n?.agent?.id ?? n));
  reg("copit.setJiraToken", async () => { const t = await vscode.window.showInputBox({ prompt: "Jira Personal Access Token", password: true }); if (t) await secrets.store("copit.jira", t); });
  reg("copit.setGitLabToken", async () => { const t = await vscode.window.showInputBox({ prompt: "GitLab Private Access Token", password: true }); if (t) await secrets.store("copit.gitlab", t); });
  reg("copit.checkUpdate", () => checkUpdate(true));
  reg("copit.newAgentFromTicket", async () => {
    const key = await vscode.window.showInputBox({ prompt: "Jira-Ticket", placeHolder: "HAW-178" });
    if (!key) return;
    const i = await jira()?.issue(key.trim().toUpperCase());
    openNewAgent(i ? { title: i.summary, ticket: i.key, prompt: `Arbeite an Ticket ${i.key}: ${i.summary}\n\n${i.description}`.trim() } : { ticket: key });
  });

  // Demo-Daten & Start-Ansicht (für Screenshots / Präsentationen)
  if (process.env.COPIT_DEMO === "1" && repos[0]) { seedDemo(db, repos[0]); agentsTree.refresh(); }
  const startView = process.env.COPIT_OPEN;
  const views: Record<string, () => void> = {
    board: openBoard, calendar: openCalendar, budgets: openBudgets, automations: () => openAutomations(), customize: openCustomize, newagent: () => openNewAgent(),
    agent: () => { const a = manager.list().find((x) => x.status === "review") ?? manager.list()[0]; if (a) openAgent(a.id); },
  };
  if (startView && views[startView]) setTimeout(views[startView], 600);
  else if (!db.get("welcomed", false)) { db.set("welcomed", true); setTimeout(openBoard, 800); }
}

export function deactivate() {
  db?.flush();
}
