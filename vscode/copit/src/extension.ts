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
import { earliestNeeded, evaluate, isoWeek, newThresholds, overview, periodRange, UsageRecord } from "./budgets";
import { buildActivity, ClaudeTranscripts, usageRecords } from "./activity";
import { DEFAULT_PRICES, Price } from "./pricing";
import { GitLab, Jira, JiraWorklog, ymd } from "./integrations";
import * as TB from "./timebooking";
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

  // ---------------------------------------------------------- Aktivität (Claude-Transkripte + Copit-Läufe)
  const transcripts = new ClaudeTranscripts();
  const prices = (): Record<string, Price> => ({ ...DEFAULT_PRICES, ...(cfg().get<Record<string, Price>>("prices") ?? {}) });
  const GENERIC = new Set(["git", "src", "repos", "source", "projects", "code", "dev", "users", "documents"]);
  const projectName = (p: string) => {
    if (!p) return "?";
    const parts = path.resolve(p).split(/[\\/]/).filter(Boolean);
    const last = parts[parts.length - 1] ?? p, prev = parts[parts.length - 2] ?? "";
    return !prev || GENERIC.has(prev.toLowerCase()) || /:$/.test(prev) ? last : `${prev}/${last}`;
  };
  const activity = async (from: number, to: number) => buildActivity({
    from, to, transcripts: await transcripts.sessions(from, to), agents: manager.list(), runs: db.runs(from, to), prices: prices(), projectName,
  });
  let usageCache: { at: number; records: UsageRecord[] } | undefined;
  const usage = async (force = false): Promise<UsageRecord[]> => {
    if (!force && usageCache && Date.now() - usageCache.at < 30000) return usageCache.records;
    const records = usageRecords(await activity(earliestNeeded(budgets()), Date.now() + 1));
    usageCache = { at: Date.now(), records };
    return records;
  };

  // ---------------------------------------------------------- Kern
  manager = new AgentManager(db, () => ({
    claudePath: cfg().get("claudePath") || undefined, copilotPath: cfg().get("copilotPath") || undefined,
    claudeModel: cfg().get("claudeModel") || undefined, instructions: cfg().get("globalInstructions") ?? "", useWorktrees: cfg().get("useWorktrees") ?? true,
  }), allRules, budgets, () => usage(true));

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
  const updateStatus = async () => {
    // erst die (langsame) Nutzung laden, dann den aktuellen Zustand lesen – sonst überschreibt ein älterer Aufruf einen neueren
    const week = overview(await usage().catch(() => [])).week;
    const list = manager.list();
    const running = list.filter((a) => manager.isRunning(a.id)).length;
    const need = list.filter((a) => a.status === "need_input").length;
    status.text = `${running ? `$(sync~spin) ${running} aktiv` : "$(layers) Copit"}${need ? `  $(question) ${need}` : ""}  $${week.cost.toFixed(2)} KW`;
    status.tooltip = `${list.length} Agents · ${need} brauchen Input · ${week.hours.toFixed(1)} h Agent-Zeit diese Woche`;
  };
  void updateStatus();
  const statusTimer = setInterval(() => void updateStatus(), 120000);
  ctx.subscriptions.push({ dispose: () => clearInterval(statusTimer) });

  manager.on("changed", (a: Agent) => { agentsTree.refresh(); Panel.refreshAll("board"); Panel.all.get("agent:" + a.id)?.refresh(); void updateStatus(); });
  manager.on("event", (e) => Panel.all.get("agent:" + e.agentId)?.refresh(200));
  manager.on("delta", (d) => Panel.all.get("agent:" + d.agentId)?.refresh(250));
  manager.on("removed", () => { agentsTree.refresh(); Panel.refreshAll("board"); });
  manager.on("runFinished", (a: Agent) => {
    filesCache.delete(a.id);
    usageCache = undefined;
    Panel.refreshAll("activity"); Panel.refreshAll("budgets"); autoTree.refresh(); void updateStatus();
    void checkBudgets();
  });

  // ---------------------------------------------------------- Budgets
  const checkBudgets = async () => {
    const list = budgets();
    if (!list.length) return;
    const records = await usage(true);
    let changed = false;
    for (const b of list) {
      const s = evaluate(b, records);
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
          agent: { ...a, running, startedAt, pendingPermissions: [...new Map((a.pendingPermissions ?? []).map((p) => { const pattern = patternForDenial(p.tool, p.input); return [pattern, { ...p, pattern }] as const; })).values()] },
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
          case "approve": void manager.approve(id, m.arg!.split(", ")); break;
          case "always": {
            const pats = m.arg!.split(", ");
            const rules: PermissionRule[] = db.get("copitRules", []);
            for (const p of pats) if (!rules.some((r) => r.id === ruleId("copit", p, a.repo))) rules.push({ id: ruleId("copit", p, a.repo), pattern: p, level: "allow", source: "copit", repo: a.repo, enabled: true });
            db.set("copitRules", rules);
            void manager.approve(id, pats);
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
    state: async () => {
      const list = manager.list();
      const records = await usage().catch(() => []);
      return {
        columns: COLUMNS,
        agents: list.map((a) => ({ ...a, running: manager.isRunning(a.id) })),
        projects: [...new Set(list.map((a) => a.project))],
        budget: budgets().slice(0, 3).map((b) => { const s = evaluate(b, records); return { label: b.name, fraction: s.fraction, text: `${s.used.toFixed(2)} / ${b.amount} (${s.periodLabel})` }; }),
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

  // ---------------------------------------------------------- Zeitbuchung
  type TimeState = { month: number; selected: number; working: boolean; booking: boolean; steps: string[]; error?: string; loading: boolean;
    accountDialog?: { index: number; issueKey: string; summary: string; options: string[] } };
  const tb: TimeState = { month: 0, selected: 0, working: false, booking: false, steps: [], loading: false };
  const tbSettings = () => ({ minHours: 8, workDays: [1, 2, 3, 4, 5], dayStart: "08:00", rounding: 15, ...db.get<any>("time.settings", {}) });
  const tbRules = (): TB.TimeRule[] => db.get("time.rules", TB.DEFAULT_RULES);
  const tbDays = (): Record<string, TB.DayEntry> => db.get("time.days", {});
  const tbEntry = (d: number): TB.DayEntry => tbDays()[ymd(new Date(d))] ?? { note: "", suggestions: [], summary: "" };
  const tbSave = (d: number, e: TB.DayEntry) => { const all = tbDays(); all[ymd(new Date(d))] = e; db.set("time.days", all); };
  const tbMem = (): TB.AccountMemory => db.get("time.accounts", { issues: {}, projects: {}, known: [] });
  const tbTopics = (): TB.TopicMapping[] => db.get("time.topics", []);
  const tbSources = (): TB.Sources => ({ ...TB.ALL_SOURCES, ...db.get<Partial<TB.Sources>>("time.sources", {}) });
  const accountField = () => cfg().get<string>("jira.accountField") || undefined;
  const accountRequired = () => !!cfg().get("jira.useTempo") || !!accountField();
  const targetFor = (d: number) => {
    const e = tbDays()[ymd(new Date(d))];
    if (e?.target != null) return e.target;
    const s = tbSettings();
    return s.workDays.includes(new Date(d).getDay()) ? s.minHours : 0;
  };
  const worklogCache = new Map<string, { at: number; logs: JiraWorklog[] }>();
  const monthLogs = async (force = false): Promise<JiraWorklog[]> => {
    const j = jira();
    if (!j) return [];
    const first = new Date(tb.month), start = new Date(first.getFullYear(), first.getMonth(), 1 - ((first.getDay() + 6) % 7));
    const key = ymd(start);
    const c = worklogCache.get(key);
    if (!force && c && Date.now() - c.at < 5 * 60000) return c.logs;
    tb.loading = true;
    try { const logs = await j.myWorklogs(start, new Date(start.getTime() + 41 * 86400000)); worklogCache.set(key, { at: Date.now(), logs }); return logs; }
    catch (e: any) { tb.error = "Jira: " + e.message; return c?.logs ?? []; }
    finally { tb.loading = false; }
  };
  let pendingAccount: ((v: { account: string; project: boolean } | undefined) => void) | undefined;
  let stopAi: { stop?: () => void } = {};

  const openTime = (day?: number) => {
    const today = new Date(); today.setHours(0, 0, 0, 0);
    if (!tb.selected || day) { tb.selected = day ?? today.getTime(); }
    const sel = new Date(tb.selected); tb.month = new Date(sel.getFullYear(), sel.getMonth(), 1).getTime();
    return Panel.show(ctx, "time", "Zeitbuchung", "timebooking", () => ({
      state: async () => {
        const logs = await monthLogs();
        const first = new Date(tb.month), start = new Date(first.getFullYear(), first.getMonth(), 1 - ((first.getDay() + 6) % 7));
        const days = tbDays();
        const s = tbSettings();
        const now = new Date(); now.setHours(0, 0, 0, 0);
        const cells = [...Array(42)].map((_, i) => {
          const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
          const k = ymd(d);
          return {
            date: d.getTime(), inMonth: d.getMonth() === first.getMonth(), isToday: d.getTime() === now.getTime(), workDay: s.workDays.includes(d.getDay()),
            booked: logs.filter((l) => ymd(new Date(l.started)) === k).reduce((x, l) => x + l.seconds, 0) / 3600, target: targetFor(d.getTime()),
            hasNote: !!days[k]?.note?.trim(), hasDraft: !!days[k]?.suggestions?.some((x) => !x.booked),
          };
        });
        const inMonth = cells.filter((c) => c.inMonth);
        const past = inMonth.filter((c) => c.date < now.getTime() && c.target - c.booked > 0.01);
        return {
          month: tb.month, days: cells, selected: tb.selected, loading: tb.loading, jira: !!jira(), accountRequired: accountRequired(),
          entry: { ...tbEntry(tb.selected), target: targetFor(tb.selected) }, worklogs: logs.filter((l) => ymd(new Date(l.started)) === ymd(new Date(tb.selected))),
          working: tb.working, booking: tb.booking, steps: tb.steps, error: tb.error, accountDialog: tb.accountDialog,
          rules: tbRules(), settings: { ...s, provider: cfg().get("defaultProvider") ?? "claude" }, sources: tbSources(), topics: tbTopics(),
          stats: { booked: inMonth.reduce((x, c) => x + c.booked, 0), target: inMonth.reduce((x, c) => x + c.target, 0), missingDays: past.length, missingHours: past.reduce((x, c) => x + c.target - c.booked, 0), fraction: 0 },
        };
      },
      onAct: async (m) => {
        const p = Panel.all.get("time");
        const parseH = (v?: string) => { const n = parseFloat(String(v ?? "").replace(",", ".").replace(/h$/i, "")); return isNaN(n) ? undefined : n; };
        const entry = tbEntry(tb.selected);
        switch (m.act) {
          case "month": {
            if (m.value === "0") { const d = new Date(); d.setHours(0, 0, 0, 0); tb.selected = d.getTime(); tb.month = new Date(d.getFullYear(), d.getMonth(), 1).getTime(); }
            else { const d = new Date(tb.month); tb.month = new Date(d.getFullYear(), d.getMonth() + Number(m.value), 1).getTime(); }
            tb.error = undefined;
            break;
          }
          case "select": if (!tb.working) { tb.selected = Number(m.value); tb.error = undefined; tb.steps = []; } break;
          case "note": entry.note = m.value ?? ""; tbSave(tb.selected, entry); break;
          case "target": {
            const h = parseH(m.value);
            const def = tbSettings().workDays.includes(new Date(tb.selected).getDay()) ? tbSettings().minHours : 0;
            entry.target = h == null || Math.abs(h - def) < 0.001 ? undefined : h;
            tbSave(tb.selected, entry);
            break;
          }
          case "edit": {
            const i = Number(m.arg); const v = JSON.parse(m.value!);
            const sg = entry.suggestions[i]; if (!sg) break;
            if (v.field === "hours") { sg.hours = parseH(v.value) ?? sg.hours; tbSave(tb.selected, entry); return; }
            if (v.field === "comment") { sg.comment = v.value; tbSave(tb.selected, entry); return; }
            if (v.field === "issueKey") {
              const key = String(v.value).trim().toUpperCase();
              if (!key || key === sg.issueKey) return;
              sg.issueKey = key; sg.account = undefined; sg.accountFromMemory = false;
              const i = await jira()?.issueFull(key, accountField());
              sg.issueSummary = i?.summary ?? "";
              if (accountRequired()) TB.resolveAccounts([sg], i ? [i] : [], tbMem());
              // Korrektur merken: dieses Thema/Repo gehört auf dieses Ticket
              if (sg.topic || sg.repo) { db.set("time.topics", TB.learnTopic(tbTopics(), sg, 3)); notify(`Gemerkt: „${sg.topic ?? sg.repo}“ → ${key}`); }
              tbSave(tb.selected, entry);
            }
            break;
          }
          case "remove": entry.suggestions.splice(Number(m.arg), 1); tbSave(tb.selected, entry); break;
          case "add": {
            const key = (await vscode.window.showInputBox({ prompt: "Ticket-Key", placeHolder: "ABC-123" }))?.trim().toUpperCase();
            if (!key) return;
            const i = await jira()?.issueFull(key, accountField());
            const sg: TB.Suggestion = { issueKey: key, issueSummary: i?.summary ?? "", hours: 1, comment: "", reason: "manuell", confidence: 1 };
            TB.resolveAccounts([sg], i ? [i] : [], tbMem());
            entry.suggestions.push(sg); tbSave(tb.selected, entry);
            break;
          }
          case "issue": { const u = jira()?.issueUrl(m.arg!); if (u) await vscode.env.openExternal(vscode.Uri.parse(u)); return; }
          case "settings": openCustomize(); return;
          case "cancel": stopAi.stop?.(); break;
          case "source": { const s2 = tbSources(); (s2 as any)[m.arg!] = !(s2 as any)[m.arg!]; db.set("time.sources", s2); break; }
          case "topic.del": db.set("time.topics", tbTopics().filter((t) => t.id !== m.arg)); break;
          case "topic.add": {
            const v = JSON.parse(m.value!);
            if (!v.topic || !v.ticket) return;
            db.set("time.topics", TB.learnTopic(tbTopics(), { topic: v.topic, repo: v.repo || undefined, issueKey: String(v.ticket).toUpperCase() }, 3));
            break;
          }
          case "rules.save": {
            const v = JSON.parse(m.value!);
            db.set("time.rules", v.rules);
            db.set("time.settings", { ...tbSettings(), minHours: parseH(v.minHours) ?? 8, rounding: parseInt(v.rounding) || 15, dayStart: /^\d{1,2}:\d{2}$/.test(v.dayStart) ? v.dayStart : "08:00" });
            break;
          }
          case "account": {
            const i = Number(m.arg);
            const acc = await askAccount(i);
            if (acc) { const e2 = tbEntry(tb.selected); e2.suggestions[i].account = acc; tbSave(tb.selected, e2); }
            break;
          }
          case "account.ok": { const v = JSON.parse(m.value!); pendingAccount?.(v.account ? v : undefined); return; }
          case "account.skip": pendingAccount?.(undefined); return;
          case "analyze": {
            if (tb.working) return;
            const v = JSON.parse(m.value!);
            const hours = parseH(v.hours);
            if (!hours || hours <= 0) { tb.error = "Bitte angeben, wie viele Stunden gebucht werden sollen."; break; }
            tb.working = true; tb.error = undefined; tb.steps = [];
            const day = new Date(tb.selected);
            const progress = (s: string) => { if (tb.steps[tb.steps.length - 1] !== s) { tb.steps.push(s); tb.steps = tb.steps.slice(-7); } p?.refresh(80); };
            p?.refresh(0);
            try {
              const dayFrom = day.getTime();
              const sessions = await activity(dayFrom, dayFrom + 86400000);
              const c = await TB.gather({ date: day, hoursToBook: hours, jira: jira(), gitlab: gitlab(), repos, sessions, note: entry.note, accountField: accountField(), progress, sources: tbSources(), topics: tbTopics() });
              c.warnings.forEach((w) => progress("Hinweis: " + w));
              let plan: { summary: string; bookings: TB.Suggestion[] };
              if (v.ai) {
                progress(`KI · ${v.provider} formuliert Buchungsvorschläge`);
                stopAi = {};
                try {
                  const text = await TB.askAi(TB.buildPrompt(c, tbRules(), tbSettings().rounding), v.provider, v.provider === "claude" ? cfg().get("claudeModel") || undefined : undefined,
                    v.provider === "claude" ? cfg().get("claudePath") || undefined : cfg().get("copilotPath") || undefined, progress, stopAi);
                  plan = TB.parsePlan(text);
                } catch (e: any) {
                  if (/Abgebrochen/.test(e.message)) throw e;
                  notify("KI nicht verfügbar – Vorschlag ohne KI: " + e.message, "warn");
                  plan = TB.heuristicPlan(c, tbSettings().rounding);
                }
              } else plan = TB.heuristicPlan(c, tbSettings().rounding);
              for (const b of plan.bookings) b.issueSummary = c.issues.find((i) => i.key === b.issueKey)?.summary ?? b.issueSummary;
              TB.normalize(plan.bookings, hours, tbSettings().rounding);
              if (accountRequired()) { progress("Jira · prüfe Accounts der Tickets"); TB.resolveAccounts(plan.bookings, c.issues, tbMem()); }
              progress("Fertig");
              const e2 = tbEntry(tb.selected);
              e2.suggestions = plan.bookings; e2.summary = plan.summary || (plan.bookings.length ? "" : "Keine passenden Tickets gefunden – trage eine Tagesnotiz ein und versuche es erneut.");
              tbSave(tb.selected, e2);
            } catch (e: any) { tb.error = e.message; }
            finally { tb.working = false; }
            break;
          }
          case "book": {
            const j = jira();
            if (!j || tb.booking) return;
            const e2 = tbEntry(tb.selected);
            const unassigned = e2.suggestions.filter((s) => !s.booked && s.hours > 0 && !s.issueKey).length;
            if (unassigned) notify(`${unassigned} Vorschlag/Vorschläge ohne Ticket werden übersprungen – trag das Ticket ein, Copit merkt es sich.`, "warn");
            const open = e2.suggestions.map((s, i) => ({ s, i })).filter(({ s }) => !s.booked && s.hours > 0 && !!s.issueKey);
            if (!open.length) return;
            if (accountRequired()) for (const { s, i } of open.filter(({ s }) => !s.account)) {
              const acc = await askAccount(i);
              if (!acc) { notify(`Buchung abgebrochen – für ${s.issueKey} fehlt der Account.`, "warn"); Panel.all.get("time")?.refresh(); return; }
              const mem = tbMem();
              for (const o of open) if (!o.s.account && (o.s.issueKey === s.issueKey || mem.projects[TB.projectKey(o.s.issueKey)] === acc)) o.s.account = acc;
            }
            const total = open.reduce((x, o) => x + o.s.hours, 0);
            const ok = await vscode.window.showInformationMessage(`${open.length} Buchung(en) mit ${total.toLocaleString("de-DE")} h am ${new Date(tb.selected).toLocaleDateString("de-DE")} in Jira eintragen?`, { modal: true }, "Buchen");
            if (ok !== "Buchen") return;
            tb.booking = true; p?.refresh(0);
            try {
              const logs = (await monthLogs(true)).filter((l) => ymd(new Date(l.started)) === ymd(new Date(tb.selected)));
              let start = TB.nextStart(new Date(tb.selected), tbSettings().dayStart, logs);
              let okCount = 0;
              for (const { s } of open) {
                const sec = Math.round(s.hours * 3600);
                try {
                  await j.book(s.issueKey, start, sec, s.comment, { tempo: !!cfg().get("jira.useTempo"), account: s.account, accountAttribute: cfg().get<string>("jira.tempoAttribute") || "_Account_" });
                  s.booked = true; s.error = undefined; okCount++;
                  if (s.topic || s.repo) db.set("time.topics", TB.learnTopic(tbTopics(), s, 1));
                  start = new Date(start.getTime() + sec * 1000);
                } catch (err: any) { s.error = err.message; }
              }
              tbSave(tb.selected, e2);
              await monthLogs(true);
              notify(okCount === open.length ? `${okCount} Buchung(en) gespeichert (${total.toLocaleString("de-DE")} h).` : `${okCount} von ${open.length} Buchungen gespeichert – Details im Panel.`, okCount === open.length ? "info" : "warn");
            } finally { tb.booking = false; }
            break;
          }
        }
      },
    }));
  };

  /** Fragt den Account für eine Buchung ab (Dialog im Panel) und merkt ihn sich. */
  const askAccount = async (index: number): Promise<string | undefined> => {
    const e = tbEntry(tb.selected);
    const s = e.suggestions[index];
    if (!s) return undefined;
    const mem = tbMem();
    const options = [...mem.known];
    if (cfg().get("jira.useTempo")) for (const a of (await jira()?.tempoAccounts()) ?? []) if (!options.includes(a.key)) options.push(a.key);
    tb.accountDialog = { index, issueKey: s.issueKey, summary: s.issueSummary, options: options.slice(0, 40) };
    Panel.all.get("time")?.refresh(0);
    const r = await new Promise<{ account: string; project: boolean } | undefined>((res) => { pendingAccount = res; });
    pendingAccount = undefined; tb.accountDialog = undefined;
    Panel.all.get("time")?.refresh(0);
    if (!r) return undefined;
    db.set("time.accounts", TB.remember(tbMem(), s.issueKey, r.account, r.project));
    const e2 = tbEntry(tb.selected);
    for (const x of e2.suggestions) if (!x.account && (x.issueKey === s.issueKey || (r.project && TB.projectKey(x.issueKey) === TB.projectKey(s.issueKey)))) { x.account = r.account; x.accountFromMemory = true; }
    tbSave(tb.selected, e2);
    return r.account;
  };
  // Activity: Tag (Zeitleiste) und Woche (Kalender + Stundenzettel)
  const act = { mode: "day" as "day" | "week", scope: "all" as "workspace" | "all", anchor: Date.now() };
  const startOfDay = (ts: number) => { const d = new Date(ts); return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime(); };
  const inWorkspace = (p: string) => repos.some((r) => samePath(r, p) || p.toLowerCase().startsWith(r.toLowerCase() + path.sep));
  const openCalendar = (mode?: "day" | "week") => {
    if (mode) act.mode = mode;
    return Panel.show(ctx, "activity", "Activity", "activity", () => ({
      state: async () => {
        const dayFrom = startOfDay(act.anchor);
        const w = periodRange("week", new Date(act.anchor));
        const running = new Set(manager.list().filter((a) => manager.isRunning(a.id)).map((a) => a.id));
        const filter = (list: Awaited<ReturnType<typeof activity>>) => list
          .filter((s) => act.scope === "all" || inWorkspace(s.projectPath))
          .map((s) => ({ ...s, running: !!s.agentId && running.has(s.agentId), perDay: undefined }));
        const week = filter(await activity(w.from, w.to));
        const day = act.mode === "day" ? filter(await activity(dayFrom, dayFrom + 86400000)) : [];
        return { mode: act.mode, scope: act.scope, anchor: act.anchor, from: dayFrom, to: dayFrom + 86400000, weekFrom: w.from, sessions: day, weekSessions: week, now: Date.now() };
      },
      onAct: async (m) => {
        const step = act.mode === "day" ? 86400000 : 7 * 86400000;
        if (m.act === "nav") act.anchor = m.value === "0" ? Date.now() : act.anchor + Number(m.value) * step;
        else if (m.act === "mode") act.mode = m.value as any;
        else if (m.act === "scope") act.scope = m.value as any;
        else if (m.act === "pickday") { act.anchor = Number(m.value); act.mode = "day"; }
        else if (m.act === "open") openAgent(m.arg!);
        else if (m.act === "copy") { await vscode.env.clipboard.writeText(m.value ?? ""); notify("In die Zwischenablage kopiert."); }
      },
    }));
  };

  let picked: string | undefined;
  const openBudgets = () => Panel.show(ctx, "budgets", "Budgets", "budgets", () => ({
    state: async () => {
      const records = await usage();
      const list = budgets();
      const s = {
        budgets: list, statuses: list.map((b) => { const st = evaluate(b, records); return { id: b.id, used: st.used, fraction: st.fraction, periodLabel: st.periodLabel }; }),
        overview: overview(records), repos: repos.map((r) => ({ path: r, name: repoName(r) })), picked,
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
        void checkBudgets();
        void updateStatus(); Panel.refreshAll("board");
      } else if (m.act === "delete") {
        const ok = await vscode.window.showWarningMessage(`Budget "${m.value}" löschen?`, { modal: true }, "Löschen");
        if (ok === "Löschen") db.set("budgets", list.filter((x) => x.id !== m.arg));
      }
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
        else if (m.act === "delete") {
          const ok = await vscode.window.showWarningMessage(`Automation "${m.value}" löschen?`, { modal: true }, "Löschen");
          if (ok === "Löschen") db.deleteAutomation(m.arg!);
        }
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

  const settingKeys = ["jira.accountField", "jira.useTempo", "jira.tempoAttribute", "claudePath", "copilotPath", "defaultProvider", "claudeModel", "globalInstructions", "useWorktrees", "jira.url", "jira.authMode", "jira.user", "gitlab.url", "ignoreCertificateErrors", "pollSeconds", "update.repo", "update.auto"];
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
  reg("copit.openCalendar", () => openCalendar());
  reg("copit.openPermissions", openCustomize);
  reg("copit.openBudgets", openBudgets);
  reg("copit.openTime", () => openTime());
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
    time: () => openTime(), board: openBoard, calendar: () => openCalendar("week"), activity: () => openCalendar("day"), budgets: openBudgets, automations: () => openAutomations(), customize: openCustomize, newagent: () => openNewAgent(),
    agent: () => { const a = manager.list().find((x) => x.status === "review") ?? manager.list()[0]; if (a) openAgent(a.id); },
  };
  // Aufgeräumtes Copit-Profil: leere Seitenleiste rechts schließen
  if (process.env.COPIT_PROFILE === "1") setTimeout(() => void vscode.commands.executeCommand("workbench.action.closeAuxiliaryBar"), 1200);

  // Selbsttest: echten Agent im ersten Repo starten und Agent-Tab zeigen (COPIT_SELFTEST=1)
  if (process.env.COPIT_SELFTEST === "1" && repos[0]) {
    setTimeout(async () => {
      const a = await manager.create({ title: "Selbsttest: Notiz anlegen", ticket: "COPIT-1", repo: repos[0], provider: "claude", mode: "edit", model: process.env.COPIT_SELFTEST_MODEL || "haiku", start: false,
        prompt: "Lege die Datei notiz.txt mit dem Inhalt 'Copit live' an, lies sie danach mit dem Read-Tool zur Kontrolle und antworte in einem Satz." });
      openAgent(a.id);
      void manager.start(a.id);
    }, 1500);
  } else if (startView && views[startView]) setTimeout(views[startView], 600);
  else if (!db.get("welcomed", false)) { db.set("welcomed", true); setTimeout(openBoard, 800); }
}

export function deactivate() {
  db?.flush();
}