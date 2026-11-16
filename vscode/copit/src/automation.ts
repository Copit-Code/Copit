import { exec } from "node:child_process";
import { CopitDb } from "./db";
import { Action, Agent, Automation, AutomationEvent, Condition, newId, TriggerType } from "./model";
import { AgentManager } from "./agents/manager";
import { GitLab, Jira } from "./integrations";
import * as G from "./git";

export interface EngineDeps {
  db: CopitDb;
  agents: AgentManager;
  jira: () => Jira | undefined;
  gitlab: () => GitLab | undefined;
  notify: (msg: string, level?: "info" | "warn" | "error") => void;
  repos: () => string[];
  pollSeconds: () => number;
}

export const TRIGGERS: { type: TriggerType; label: string; group: string; params?: { key: string; label: string; placeholder?: string }[] }[] = [
  { type: "agent.started", label: "Agent startet", group: "Agent" },
  { type: "agent.finished", label: "Agent fertig", group: "Agent" },
  { type: "agent.needs_input", label: "Agent braucht Input", group: "Agent" },
  { type: "agent.failed", label: "Agent fehlgeschlagen", group: "Agent" },
  { type: "agent.stopped", label: "Agent gestoppt", group: "Agent" },
  { type: "agent.moved", label: "Karte verschoben", group: "Agent", params: [{ key: "to", label: "Nach Spalte", placeholder: "in_progress" }] },
  { type: "git.commit", label: "Neuer Commit", group: "Git" },
  { type: "git.push", label: "Push", group: "Git" },
  { type: "git.branch", label: "Neuer Branch", group: "Git" },
  { type: "pipeline.failed", label: "Pipeline fehlgeschlagen", group: "GitLab" },
  { type: "pipeline.success", label: "Pipeline erfolgreich", group: "GitLab" },
  { type: "jira.transitioned", label: "Ticket-Status geändert", group: "Jira", params: [{ key: "status", label: "Neuer Status", placeholder: "In Arbeit" }, { key: "jql", label: "JQL (optional)", placeholder: "assignee = currentUser()" }] },
  { type: "jira.assigned", label: "Ticket mir zugewiesen", group: "Jira", params: [{ key: "jql", label: "JQL (optional)", placeholder: "project = SHOP" }] },
  { type: "schedule", label: "Zeitplan", group: "Zeit", params: [{ key: "time", label: "Uhrzeit", placeholder: "09:00" }, { key: "days", label: "Tage", placeholder: "Mo,Di,Mi,Do,Fr" }] },
];

export const ACTIONS: { type: Action["type"]; label: string; group: string; params: { key: string; label: string; placeholder?: string; multiline?: boolean }[] }[] = [
  { type: "agent.set_status", label: "Status setzen", group: "Agent", params: [{ key: "status", label: "Status", placeholder: "review" }] },
  { type: "agent.start", label: "Neuen Agent starten", group: "Agent", params: [{ key: "title", label: "Titel", placeholder: "Fix {{ticket}}" }, { key: "prompt", label: "Auftrag", multiline: true, placeholder: "Arbeite an {{ticket}}: {{summary}}" }, { key: "branch", label: "Branch (optional)", placeholder: "feature/{{ticket}}" }, { key: "mode", label: "Modus", placeholder: "edit" }] },
  { type: "agent.continue", label: "Agent fortsetzen", group: "Agent", params: [{ key: "prompt", label: "Nachricht", multiline: true, placeholder: "Bitte führe die Tests aus." }] },
  { type: "notify", label: "Benachrichtigen", group: "Allgemein", params: [{ key: "message", label: "Nachricht", placeholder: "{{title}} ist fertig" }] },
  { type: "jira.comment", label: "Jira-Kommentar", group: "Jira", params: [{ key: "issue", label: "Ticket", placeholder: "{{ticket}}" }, { key: "text", label: "Text", multiline: true, placeholder: "Status: {{status}}\n{{summary}}" }] },
  { type: "jira.transition", label: "Jira-Status ändern", group: "Jira", params: [{ key: "issue", label: "Ticket", placeholder: "{{ticket}}" }, { key: "status", label: "Zielstatus / Übergang", placeholder: "In Review" }] },
  { type: "jira.worklog", label: "Zeit buchen", group: "Jira", params: [{ key: "issue", label: "Ticket", placeholder: "{{ticket}}" }, { key: "minutes", label: "Minuten (leer = Agent-Zeit)", placeholder: "" }, { key: "comment", label: "Buchungstext", placeholder: "{{title}}" }] },
  { type: "git.commit", label: "Commit", group: "Git", params: [{ key: "message", label: "Message", placeholder: "{{ticket}}: {{title}}" }] },
  { type: "git.push", label: "Push", group: "Git", params: [] },
  { type: "git.branch", label: "Branch anlegen", group: "Git", params: [{ key: "name", label: "Name", placeholder: "feature/{{ticket}}" }] },
  { type: "gitlab.mr", label: "Merge Request", group: "GitLab", params: [{ key: "target", label: "Ziel-Branch (leer = Default)", placeholder: "" }, { key: "title", label: "Titel", placeholder: "{{ticket}}: {{title}}" }] },
  { type: "shell", label: "Befehl ausführen", group: "Allgemein", params: [{ key: "command", label: "Befehl", placeholder: "npm test" }] },
];

export function render(tpl: string, vars: Record<string, string>): string {
  return (tpl ?? "").replace(/\{\{\s*([\w.]+)\s*\}\}/g, (_, k) => vars[k] ?? "");
}

export function matches(c: Condition, vars: Record<string, string>): boolean {
  const v = (vars[c.field] ?? "").toLowerCase();
  const x = (c.value ?? "").toLowerCase();
  switch (c.op) {
    case "equals": return v === x;
    case "not_equals": return v !== x;
    case "contains": return v.includes(x);
    case "matches": try { return new RegExp(c.value, "i").test(vars[c.field] ?? ""); } catch { return false; }
  }
}

export function triggerMatches(a: Automation, e: AutomationEvent): boolean {
  if (!a.enabled || a.trigger.type !== e.type) return false;
  const p = a.trigger.params ?? {};
  if (e.type === "agent.moved" && p.to && p.to !== e.vars.to) return false;
  if (e.type === "jira.transitioned" && p.status && p.status.toLowerCase() !== (e.vars.status ?? "").toLowerCase()) return false;
  if (e.vars.automationId && e.vars.automationId !== a.id) return false;
  return a.conditions.every((c) => matches(c, e.vars));
}

export function agentVars(a?: Agent): Record<string, string> {
  if (!a) return {};
  return {
    agentId: a.id, title: a.title, ticket: a.ticket ?? "", branch: a.branch ?? "", project: a.project, status: a.status,
    summary: a.summary ?? "", provider: a.provider, cost: a.costUsd.toFixed(2),
    agentMinutes: (a.agentMs / 60000).toFixed(0), aiMinutes: (a.aiMs / 60000).toFixed(0),
    lines: `+${a.linesAdded} -${a.linesRemoved}`, files: String(a.filesChanged),
  };
}

export class AutomationEngine {
  private timer?: NodeJS.Timeout;
  private heads = new Map<string, string>();
  private upstreams = new Map<string, string>();
  private branches = new Map<string, Set<string>>();
  private pipelines = new Map<string, string>();
  private jiraStatus = new Map<string, string>();
  private jiraAssigned?: Set<string>;
  private scheduleDone = new Set<string>();
  private depth = 0;

  constructor(private d: EngineDeps) {
    d.agents.on("automation", (e: AutomationEvent) => void this.dispatch(e));
  }

  seedDefaults() {
    if (this.d.db.get("automationsSeeded", false)) return;
    const mk = (name: string, type: TriggerType, actions: Action[], enabled = true, params: Record<string, string> = {}): Automation =>
      ({ id: newId(), name, enabled, trigger: { type, params }, conditions: [], actions, runs: 0 });
    [
      mk("Braucht Input → benachrichtigen", "agent.needs_input", [{ type: "notify", params: { message: "{{title}} braucht deinen Input" } }]),
      mk("Fehlgeschlagen → benachrichtigen", "agent.failed", [{ type: "notify", params: { message: "{{title}} ist fehlgeschlagen" } }]),
      mk("Karte auf In Progress → umsetzen", "agent.moved", [{ type: "agent.continue", params: { prompt: "Setze den besprochenen Plan jetzt um." } }], true, { to: "in_progress" }),
      mk("Fertig → Jira-Kommentar", "agent.finished", [{ type: "jira.comment", params: { issue: "{{ticket}}", text: "Copit-Agent fertig ({{lines}} in {{files}} Dateien):\n{{summary}}" } }], false),
      mk("Fertig → Jira In Review", "agent.finished", [{ type: "jira.transition", params: { issue: "{{ticket}}", status: "In Review" } }], false),
      mk("Fertig → Agent-Zeit buchen", "agent.finished", [{ type: "jira.worklog", params: { issue: "{{ticket}}", minutes: "", comment: "{{title}}" } }], false),
      mk("Pipeline rot → Fix-Agent", "pipeline.failed", [{ type: "agent.start", params: { title: "Pipeline fixen {{branch}}", prompt: "Die GitLab-Pipeline auf {{branch}} ist fehlgeschlagen ({{pipelineUrl}}). Finde die Ursache und behebe sie.", mode: "edit" } }], false),
      mk("Ticket In Arbeit → Agent anlegen", "jira.transitioned", [{ type: "agent.start", params: { title: "{{ticket}} {{issueSummary}}", prompt: "Analysiere Ticket {{ticket}} ({{issueSummary}}) und erstelle einen Umsetzungsplan.", mode: "plan", branch: "feature/{{ticket}}" } }], false, { status: "In Arbeit" }),
    ].forEach((a) => this.d.db.saveAutomation(a));
    this.d.db.set("automationsSeeded", true);
  }

  start() {
    const tick = async () => {
      try { await this.poll(); } catch { /* weiter */ }
      this.timer = setTimeout(tick, Math.max(15, this.d.pollSeconds()) * 1000);
    };
    this.timer = setTimeout(tick, 5000);
  }

  dispose() { clearTimeout(this.timer); }

  async dispatch(e: AutomationEvent) {
    if (this.depth > 3) return; // Endlosschleifen verhindern
    const vars = { ...agentVars(e.agent), ...e.vars };
    for (const a of this.d.db.automations().filter((x) => triggerMatches(x, { ...e, vars }))) {
      this.depth++;
      try {
        const results: string[] = [];
        for (const act of a.actions) results.push(await this.run(act, vars, e.agent));
        a.lastResult = results.filter(Boolean).join(" · ") || "ok";
        this.d.db.logAutomation(a.id, true, a.lastResult);
      } catch (err: any) {
        a.lastResult = "Fehler: " + err.message;
        this.d.db.logAutomation(a.id, false, a.lastResult);
        this.d.notify(`Automation "${a.name}": ${err.message}`, "error");
      } finally {
        this.depth--;
        a.lastRun = Date.now();
        a.runs = (a.runs ?? 0) + 1;
        this.d.db.saveAutomation(a);
      }
    }
  }

  /** Testlauf aus dem Builder */
  async test(a: Automation, agent?: Agent) {
    const vars = { ...agentVars(agent), ticket: agent?.ticket ?? "TEST-1", automationId: a.id };
    await this.dispatch({ type: a.trigger.type, agent, vars: { ...vars, ...a.trigger.params } });
  }

  private async run(act: Action, vars: Record<string, string>, agent?: Agent): Promise<string> {
    const p = Object.fromEntries(Object.entries(act.params ?? {}).map(([k, v]) => [k, render(v, vars)]));
    const cwd = agent?.cwd ?? vars.repo ?? this.d.repos()[0];
    const jira = () => { const j = this.d.jira(); if (!j) throw new Error("Jira nicht konfiguriert"); return j; };
    const issue = () => { const k = p.issue || vars.ticket; if (!k) throw new Error("Kein Ticket"); return k; };
    switch (act.type) {
      case "agent.set_status":
        if (agent) this.d.agents.setStatus(agent.id, p.status as any);
        return `Status ${p.status}`;
      case "agent.start": {
        const repo = agent?.repo ?? vars.repo ?? this.d.repos()[0];
        if (!repo) throw new Error("Kein Repository");
        const a = await this.d.agents.create({ title: p.title, prompt: p.prompt, repo, ticket: vars.ticket, branch: p.branch, provider: agent?.provider ?? "claude", mode: (p.mode as any) || "edit", start: false });
        void this.d.agents.start(a.id);
        return `Agent ${a.title}`;
      }
      case "agent.continue":
        if (agent) void this.d.agents.reply(agent.id, p.prompt);
        return "fortgesetzt";
      case "notify":
        this.d.notify(p.message);
        return "";
      case "jira.comment":
        await jira().comment(issue(), p.text);
        return `Kommentar ${issue()}`;
      case "jira.transition":
        await jira().transition(issue(), p.status);
        return `${issue()} → ${p.status}`;
      case "jira.worklog": {
        const sec = p.minutes ? Number(p.minutes) * 60 : (agent?.agentMs ?? 0) / 1000;
        await jira().worklog(issue(), sec, p.comment);
        return `${Math.round(sec / 60)} min auf ${issue()}`;
      }
      case "git.commit":
        if (!cwd) throw new Error("Kein Repository");
        await G.commitAll(cwd, p.message || "Copit-Commit");
        return "Commit";
      case "git.push":
        if (!cwd) throw new Error("Kein Repository");
        await G.push(cwd);
        return "Push";
      case "git.branch":
        if (!cwd) throw new Error("Kein Repository");
        await G.checkoutBranch(cwd, p.name);
        return `Branch ${p.name}`;
      case "gitlab.mr": {
        const gl = this.d.gitlab();
        if (!gl || !cwd) throw new Error("GitLab nicht konfiguriert");
        const project = G.projectPathFromRemote(await G.remoteUrl(cwd));
        if (!project) throw new Error("GitLab-Projekt nicht erkannt");
        const source = await G.currentBranch(cwd);
        await G.push(cwd).catch(() => undefined);
        const url = await gl.createMr(project, source, p.target || await gl.defaultBranch(project), p.title, vars.summary ?? "");
        return `MR ${url}`;
      }
      case "shell":
        return new Promise((res, rej) => exec(p.command, { cwd, windowsHide: true, timeout: 10 * 60000 }, (e, out, err) =>
          e ? rej(new Error((err || e.message).slice(0, 300))) : res((out || "").trim().slice(0, 200))));
    }
  }

  // ------------------------------------------------------------ Watcher

  private wants(t: TriggerType) { return this.d.db.automations().some((a) => a.enabled && a.trigger.type === t); }

  private async poll() {
    const repos = new Set<string>(this.d.repos());
    for (const a of this.d.agents.list()) if (a.worktree) repos.add(a.cwd);

    if (this.wants("git.commit") || this.wants("git.push") || this.wants("git.branch") || this.wants("pipeline.failed") || this.wants("pipeline.success"))
      for (const r of repos) await this.pollGit(r);
    if (this.wants("jira.transitioned") || this.wants("jira.assigned")) await this.pollJira();
    this.pollSchedule();
  }

  private agentFor(cwd: string) {
    return this.d.agents.list().find((a) => a.cwd.toLowerCase() === cwd.toLowerCase());
  }

  private async pollGit(cwd: string) {
    const agent = this.agentFor(cwd);
    const base = { repo: cwd, ...agentVars(agent) };
    const branch = await G.currentBranch(cwd);
    const head = await G.headSha(cwd);
    if (head) {
      const prev = this.heads.get(cwd);
      if (prev && prev !== head) {
        const c = await G.lastCommit(cwd);
        await this.dispatch({ type: "git.commit", agent, vars: { ...base, branch, sha: head, message: c?.subject ?? "", author: c?.author ?? "" } });
      }
      this.heads.set(cwd, head);
    }
    const up = await G.git(cwd, "rev-parse", "@{u}");
    if (up.code === 0) {
      const prev = this.upstreams.get(cwd + branch);
      if (prev && prev !== up.out.trim()) await this.dispatch({ type: "git.push", agent, vars: { ...base, branch } });
      this.upstreams.set(cwd + branch, up.out.trim());
    }
    const list = new Set((await G.git(cwd, "branch", "--format=%(refname:short)")).out.split("\n").map((s) => s.trim()).filter(Boolean));
    const known = this.branches.get(cwd);
    if (known) for (const b of list) if (!known.has(b)) await this.dispatch({ type: "git.branch", agent, vars: { ...base, branch: b } });
    this.branches.set(cwd, list);

    const gl = this.d.gitlab();
    if (gl && (this.wants("pipeline.failed") || this.wants("pipeline.success"))) {
      const project = G.projectPathFromRemote(await G.remoteUrl(cwd));
      if (!project) return;
      const p = await gl.latestPipeline(project, branch).catch(() => undefined);
      if (!p) return;
      const key = `${cwd}|${branch}`;
      const prev = this.pipelines.get(key);
      const state = `${p.id}:${p.status}`;
      if (prev && prev !== state && (p.status === "failed" || p.status === "success"))
        await this.dispatch({ type: p.status === "failed" ? "pipeline.failed" : "pipeline.success", agent, vars: { ...base, branch, pipelineUrl: p.webUrl, pipelineId: String(p.id) } });
      this.pipelines.set(key, state);
    }
  }

  private async pollJira() {
    const j = this.d.jira();
    if (!j) return;
    const autos = this.d.db.automations().filter((a) => a.enabled && a.trigger.type.startsWith("jira."));
    const jqls = new Set(autos.map((a) => a.trigger.params?.jql || "assignee = currentUser()"));
    for (const jql of jqls) {
      const issues = await j.search(`(${jql}) AND updated >= -1d ORDER BY updated DESC`, 50).catch(() => []);
      for (const i of issues) {
        const prev = this.jiraStatus.get(i.key);
        if (prev && prev !== i.status)
          await this.dispatch({ type: "jira.transitioned", vars: { ticket: i.key, status: i.status, from: prev, issueSummary: i.summary, repo: this.d.repos()[0] ?? "" } });
        this.jiraStatus.set(i.key, i.status);
      }
    }
    if (autos.some((a) => a.trigger.type === "jira.assigned")) {
      const mine = await j.search("assignee = currentUser() AND statusCategory != Done ORDER BY created DESC", 50).catch(() => []);
      const keys = new Set(mine.map((i) => i.key));
      if (this.jiraAssigned) for (const i of mine) if (!this.jiraAssigned.has(i.key))
        await this.dispatch({ type: "jira.assigned", vars: { ticket: i.key, status: i.status, issueSummary: i.summary, repo: this.d.repos()[0] ?? "" } });
      this.jiraAssigned = keys;
    }
  }

  private pollSchedule() {
    const now = new Date();
    const days = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    for (const a of this.d.db.automations().filter((x) => x.enabled && x.trigger.type === "schedule")) {
      const [h, m] = (a.trigger.params.time || "09:00").split(":").map(Number);
      const allowed = (a.trigger.params.days || "Mo,Di,Mi,Do,Fr").split(",").map((s) => s.trim());
      if (!allowed.includes(days[now.getDay()])) continue;
      const due = new Date(now.getFullYear(), now.getMonth(), now.getDate(), h, m);
      const key = `${a.id}|${due.toDateString()}`;
      if (now >= due && now.getTime() - due.getTime() < 2 * 3600000 && !this.scheduleDone.has(key) && (a.lastRun ?? 0) < due.getTime()) {
        this.scheduleDone.add(key);
        void this.dispatch({ type: "schedule", vars: { repo: this.d.repos()[0] ?? "", automationId: a.id } });
      }
    }
  }
}
