import * as path from "node:path";
import { EventEmitter } from "node:events";
import { CopitDb } from "../db";
import { Agent, AgentEvent, AgentStatus, AutomationEvent, Budget, Mode, newId, Provider, Run, TriggerType } from "../model";
import { CliEvent, RunHandle, runCli } from "./cli";
import * as G from "../git";
import { flagsFor, PermissionRule } from "../permissions";
import { budgetCovers, evaluate } from "../budgets";

export const QUESTION_INSTRUCTIONS =
  "Wenn du vom Nutzer eine Entscheidung oder fehlende Information brauchst, stelle die Rückfrage am ENDE deiner Antwort " +
  "in genau diesem Format:\n<rueckfrage>\nFrage: <Frage>\n- <Option 1>\n- <Option 2>\n</rueckfrage>\nFrage nur, wenn es wirklich nötig ist.";

export function extractQuestion(text: string): { clean: string; question?: string; options: string[] } {
  const m = /<rueckfrage>([\s\S]*?)<\/rueckfrage>/i.exec(text ?? "");
  if (!m) return { clean: text, options: [] };
  const lines = m[1].split("\n").map((l) => l.trim()).filter(Boolean);
  let q = "";
  const options: string[] = [];
  for (const l of lines) {
    if (/^[-*] /.test(l)) options.push(l.slice(2).trim());
    else q = (q + " " + l.replace(/^Frage:\s*/i, "")).trim();
  }
  return { clean: text.replace(m[0], "").trim(), question: q || undefined, options };
}

export interface ManagerConfig {
  claudePath?: string;
  copilotPath?: string;
  claudeModel?: string;
  instructions: string;
  useWorktrees: boolean;
}

export interface NewAgentOptions {
  title: string;
  prompt: string;
  repo: string;
  ticket?: string;
  branch?: string;
  provider: Provider;
  mode: Mode;
  model?: string;
  start: boolean;
}

interface LiveRun {
  run: Run;
  handle: RunHandle;
  toolStarts: Map<string, number>;
  openSubagents: Set<string>;
  textEventId?: number;
  textBuf: string;
  lastTextFlush: number;
}

/**
 * Verwaltet Agents: anlegen, starten, fortsetzen, stoppen.
 * Leitet Status automatisch ab und misst AI-Zeit (Generierung) vs. Agent-Zeit (Wanduhr).
 */
export class AgentManager extends EventEmitter {
  private live = new Map<string, LiveRun>();

  constructor(private db: CopitDb, private config: () => ManagerConfig, private rules: () => PermissionRule[], private budgets: () => Budget[]) {
    super();
    // Läufe, die beim letzten Beenden noch liefen, als unterbrochen markieren
    for (const a of db.agents()) if (a.running) {
      a.running = false;
      a.status = "need_input";
      a.pendingQuestion = "Der letzte Lauf wurde durch einen Neustart unterbrochen. Fortsetzen?";
      db.saveAgent(a);
    }
  }

  isRunning(id: string) { return this.live.has(id); }
  list() { return this.db.agents(); }
  get(id: string) { return this.db.agent(id); }

  private changed(a: Agent) {
    this.db.saveAgent(a);
    this.emit("changed", a);
  }

  private fire(type: TriggerType, a: Agent, vars: Record<string, string> = {}) {
    const e: AutomationEvent = { type, agent: a, vars: { status: a.status, ticket: a.ticket ?? "", branch: a.branch ?? "", title: a.title, project: a.project, summary: a.summary ?? "", ...vars } };
    this.emit("automation", e);
  }

  setStatus(id: string, status: AgentStatus, fromUser = false) {
    const a = this.db.agent(id);
    if (!a || a.status === status) return;
    const prev = a.status;
    a.status = status;
    this.changed(a);
    this.addEvent(a, "", { kind: "status", text: `${prev} → ${status}${fromUser ? " (manuell)" : ""}` });
    if (fromUser) this.fire("agent.moved", a, { from: prev, to: status });
  }

  private addEvent(a: Agent, runId: string, e: Partial<AgentEvent> & { kind: AgentEvent["kind"] }): AgentEvent {
    const ev: AgentEvent = { agentId: a.id, runId, ts: Date.now(), ...e };
    ev.id = this.db.addEvent(ev);
    this.emit("event", ev);
    return ev;
  }

  async create(o: NewAgentOptions): Promise<Agent> {
    const cfg = this.config();
    const a: Agent = {
      id: newId(), title: o.title || o.prompt.split("\n")[0].slice(0, 60), prompt: o.prompt, ticket: o.ticket?.toUpperCase() || undefined,
      project: path.basename(o.repo), repo: o.repo, cwd: o.repo, branch: o.branch || undefined,
      provider: o.provider, mode: o.mode, model: o.model || (o.provider === "claude" ? cfg.claudeModel : undefined) || undefined,
      status: "inbox", createdAt: Date.now(), updatedAt: Date.now(),
      aiMs: 0, agentMs: 0, toolMs: 0, costUsd: 0, contextTokens: 0, turns: 0, linesAdded: 0, linesRemoved: 0, filesChanged: 0,
    };
    a.baseSha = await G.headSha(o.repo);
    this.changed(a);
    if (o.start) await this.start(a.id);
    return a;
  }

  /** Prüft Budgets mit Hard-Stop. Gibt Fehlertext zurück, falls blockiert. */
  blockedByBudget(a: Agent): string | undefined {
    const runs = this.db.runs(0, Date.now() + 1);
    const agents = new Map(this.db.agents().map((x) => [x.id, x]));
    for (const b of this.budgets().filter((x) => x.hardStop && budgetCovers(x, a))) {
      const s = evaluate(b, runs, agents);
      if (s.fraction >= 1) return `Budget "${b.name}" ist aufgebraucht (${s.used.toFixed(2)} / ${b.amount} ${b.type === "cost" ? "USD" : "h"}, ${s.periodLabel}).`;
    }
    return undefined;
  }

  async start(id: string, message?: string, displayText?: string): Promise<void> {
    const a = this.db.agent(id);
    if (!a || this.live.has(id)) return;
    const blocked = this.blockedByBudget(a);
    if (blocked) {
      a.status = "need_input"; a.pendingQuestion = blocked;
      this.changed(a);
      this.addEvent(a, "", { kind: "error", text: blocked, isError: true });
      return;
    }
    const cfg = this.config();
    const firstRun = !a.sessionId && !this.db.events(id).some((e) => e.kind === "user");
    const prompt = message ?? a.prompt;

    // Arbeitsordner vorbereiten (Branch / Worktree)
    try {
      if (a.branch && firstRun) {
        if (cfg.useWorktrees) { a.cwd = await G.ensureWorktree(a.repo, a.branch, a.id); a.worktree = true; }
        else await G.checkoutBranch(a.repo, a.branch);
        a.baseSha = await G.headSha(a.cwd) ?? a.baseSha;
      }
    } catch (e: any) {
      this.addEvent(a, "", { kind: "error", text: "Branch/Worktree: " + e.message, isError: true });
      a.status = "failed"; a.error = e.message;
      this.changed(a);
      this.fire("agent.failed", a, { error: e.message });
      return;
    }

    const run: Run = { id: newId(), agentId: a.id, startedAt: Date.now(), aiMs: 0, toolMs: 0, costUsd: 0, turns: 0, prompt };
    this.db.saveRun(run);
    a.status = "in_progress"; a.running = true; a.pendingQuestion = undefined; a.pendingPermissions = undefined; a.error = undefined;
    this.changed(a);
    this.addEvent(a, run.id, { kind: "user", text: displayText ?? prompt });
    this.fire("agent.started", a);

    const flags = flagsFor(this.rules(), a.repo, a.provider === "copilot");
    const allowed = [...flags.allowed, ...(a.allowedOnce ?? [])];
    const sys = [cfg.instructions, a.ticket ? `Ticket: ${a.ticket}.` : "", a.branch ? `Du arbeitest auf Branch ${a.branch}.` : "", QUESTION_INSTRUCTIONS].filter(Boolean).join("\n\n");
    const history = a.provider === "copilot"
      ? this.db.events(a.id).filter((e) => e.kind === "user" || e.kind === "text").slice(-12).map((e) => `${e.kind === "user" ? "Nutzer" : "Assistent"}: ${(e.text ?? "").slice(0, 1500)}`).join("\n")
      : undefined;

    const lr: LiveRun = {
      run, toolStarts: new Map(), openSubagents: new Set(), textBuf: "", lastTextFlush: 0,
      handle: runCli({
        provider: a.provider, prompt, cwd: a.cwd, mode: a.mode, model: a.model, resumeSessionId: a.provider === "claude" ? a.sessionId : undefined,
        allowed, disallowed: flags.disallowed, systemPrompt: sys, history: firstRun ? undefined : history,
        cliPath: a.provider === "claude" ? cfg.claudePath : cfg.copilotPath,
      }, (e) => this.onCli(a.id, e)),
    };
    this.live.set(a.id, lr);
    await lr.handle.done;
  }

  private onCli(id: string, e: CliEvent) {
    const lr = this.live.get(id);
    const a = this.db.agent(id);
    if (!lr || !a) return;
    const runId = lr.run.id;
    switch (e.t) {
      case "init":
        if (e.sessionId) a.sessionId = e.sessionId;
        if (e.model) a.model = e.model;
        this.changed(a);
        break;
      case "delta":
        if (e.parent) break; // Sub-Agent-Text nicht live streamen
        lr.textBuf += e.text;
        if (lr.textEventId == null) lr.textEventId = this.addEvent(a, runId, { kind: "text", text: lr.textBuf }).id;
        else this.emit("delta", { agentId: id, eventId: lr.textEventId, text: lr.textBuf });
        if (Date.now() - lr.lastTextFlush > 1500) { this.db.updateEventText(lr.textEventId!, lr.textBuf); lr.lastTextFlush = Date.now(); }
        break;
      case "text":
        if (!e.parent && lr.textEventId != null) {
          this.db.updateEventText(lr.textEventId, e.text);
          this.emit("delta", { agentId: id, eventId: lr.textEventId, text: e.text, final: true });
          lr.textEventId = undefined; lr.textBuf = "";
        } else this.addEvent(a, runId, { kind: "text", text: e.text, parentToolId: e.parent });
        break;
      case "tool_use": {
        if (!e.parent && lr.textEventId != null) { this.db.updateEventText(lr.textEventId, lr.textBuf); lr.textEventId = undefined; lr.textBuf = ""; }
        lr.toolStarts.set(e.id, Date.now());
        this.addEvent(a, runId, { kind: "tool_use", toolId: e.id, name: e.name, text: e.input, parentToolId: e.parent });
        if (!e.parent && (e.name === "Task" || e.name === "Agent")) {
          lr.openSubagents.add(e.id);
          if (a.status !== "waiting_subagent") { a.status = "waiting_subagent"; this.changed(a); }
        }
        break;
      }
      case "tool_result": {
        const start = lr.toolStarts.get(e.toolId);
        const dur = start ? Date.now() - start : 0;
        if (!e.parent) lr.run.toolMs += dur;
        this.db.completeTool(id, e.toolId, dur, e.isError);
        this.addEvent(a, runId, { kind: "tool_result", toolId: e.toolId, text: e.content.slice(0, 4000), isError: e.isError, durationMs: dur, parentToolId: e.parent });
        if (lr.openSubagents.delete(e.toolId) && lr.openSubagents.size === 0 && a.status === "waiting_subagent") {
          a.status = "in_progress"; this.changed(a);
        }
        break;
      }
      case "usage":
        if (e.context > 0) { a.contextTokens = e.context; this.changed(a); }
        break;
      case "stderr":
        this.emit("status", { agentId: id, text: e.text });
        break;
      case "result":
        void this.finish(a, lr, e);
        break;
    }
  }

  private async finish(a: Agent, lr: LiveRun, e: Extract<CliEvent, { t: "result" }>) {
    this.live.delete(a.id);
    const run = lr.run;
    run.endedAt = Date.now();
    const wall = run.endedAt - run.startedAt;
    run.aiMs = Math.max(0, wall - run.toolMs);
    run.costUsd = e.cost;
    run.turns = e.turns;
    run.ok = !e.isError;
    this.db.saveRun(run);
    if (lr.textEventId != null) this.db.updateEventText(lr.textEventId, lr.textBuf);

    a.running = false;
    if (e.sessionId) a.sessionId = e.sessionId;
    a.agentMs += wall; a.aiMs += run.aiMs; a.toolMs += run.toolMs; a.costUsd += e.cost; a.turns += e.turns;
    a.allowedOnce = undefined;

    try {
      const st = await G.diffStat(a.cwd, a.baseSha);
      a.linesAdded = st.added; a.linesRemoved = st.removed; a.filesChanged = st.files.length;
    } catch { /* kein Git */ }

    const events = this.db.events(a.id);
    const lastText = [...events].reverse().find((x) => x.kind === "text" && !x.parentToolId && x.runId === run.id)?.text ?? e.text ?? "";
    const q = extractQuestion(lastText);
    a.summary = (q.clean || e.text || "").slice(0, 600);

    this.addEvent(a, run.id, {
      kind: "result", isError: e.isError, durationMs: wall,
      text: JSON.stringify({ subtype: e.subtype, cost: e.cost, turns: e.turns, aiMs: run.aiMs, toolMs: run.toolMs, text: e.isError ? e.text : "" }),
    });

    let trigger: TriggerType;
    if (e.subtype === "cancelled") { a.status = "failed"; a.error = "Gestoppt"; trigger = "agent.stopped"; }
    else if (e.isError) { a.status = "failed"; a.error = e.text; trigger = "agent.failed"; }
    else if (e.denials.length) {
      a.status = "need_input";
      a.pendingPermissions = e.denials;
      a.pendingQuestion = `Freigabe nötig: ${[...new Set(e.denials.map((d) => d.tool))].join(", ")}`;
      trigger = "agent.needs_input";
    } else if (q.question) {
      a.status = "need_input";
      a.pendingQuestion = q.question + (q.options.length ? "\n" + q.options.map((o) => "- " + o).join("\n") : "");
      trigger = "agent.needs_input";
    } else {
      a.status = a.filesChanged > 0 ? "review" : "done";
      trigger = "agent.finished";
    }
    this.changed(a);
    this.fire(trigger, a, { durationMin: (wall / 60000).toFixed(1), cost: e.cost.toFixed(2) });
    this.emit("runFinished", a, run);
  }

  stop(id: string) { this.live.get(id)?.handle.stop(); }

  /** Antwort / Folgeauftrag an einen Agent. */
  async reply(id: string, text: string) {
    return this.start(id, text);
  }

  /** Erlaubt die angefragten Tools für den nächsten Lauf und setzt fort. */
  async approve(id: string, patterns: string[]) {
    const a = this.db.agent(id);
    if (!a) return;
    a.allowedOnce = [...new Set([...(a.allowedOnce ?? []), ...patterns])];
    this.db.saveAgent(a);
    await this.start(id, `Die Freigabe für ${patterns.join(", ")} wurde erteilt. Führe die blockierten Schritte jetzt aus und fahre fort.`, `Freigegeben: ${patterns.join(", ")}`);
  }

  async remove(id: string) {
    this.stop(id);
    const a = this.db.agent(id);
    if (a?.worktree) await G.removeWorktree(a.repo, a.cwd).catch(() => undefined);
    this.db.deleteAgent(id);
    this.emit("removed", id);
  }

  update(a: Agent) { this.changed(a); }
}

/** Leitet aus einer Freigabe-Anfrage ein Muster ab (z. B. Bash(git push:*)). */
export function patternForDenial(tool: string, input: string): string {
  if (tool !== "Bash") return tool;
  try {
    const cmd: string = JSON.parse(input).command ?? "";
    const words = cmd.trim().split(/\s+/);
    const head = words[0] === "git" || words[0] === "npm" || words[0] === "dotnet" ? words.slice(0, 2).join(" ") : words[0];
    return head ? `Bash(${head}:*)` : "Bash";
  } catch { return "Bash"; }
}
