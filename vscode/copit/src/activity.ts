import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import * as readline from "node:readline";
import { Agent, Run } from "./model";
import { Price, Usage, addUsage, costOf, emptyUsage, fromApiUsage } from "./pricing";

/** Pausen über 10 Minuten zählen nicht als aktive Zeit. */
export const PAUSE_MS = 10 * 60 * 1000;

export interface ActSession {
  id: string;
  source: "claude" | "copit";
  title: string;
  project: string;
  projectPath: string;
  branch?: string;
  entry: string;
  models: string[];
  prompts: number;
  toolCalls: number;
  subagents: number;
  /** Aktive Zeitspannen (Pausen > 10 min getrennt) */
  segments: [number, number][];
  usage: Usage;
  costUsd: number;
  /** true = Kosten von der CLI gemeldet, false = aus Tokens geschätzt */
  costExact: boolean;
  agentId?: string;
  ticket?: string;
  /** Kosten/Tokens pro Tag (yyyy-mm-dd) */
  perDay: Record<string, { ms: number; tokens: number; cost: number }>;
}

interface Point { ts: number; usage?: Usage; model?: string; cost?: number }

interface FileCache { mtime: number; size: number; sessions: Map<string, RawSession> }
interface RawSession { id: string; cwd: string; branch?: string; entry: string; title: string; points: Point[]; models: Set<string>; prompts: number; toolCalls: number; subagents: number; seen: Set<string> }

const ENTRY: Record<string, string> = { "claude-desktop": "Desktop app", cli: "CLI", "sdk-ts": "SDK", "sdk-py": "SDK", "claude-vscode": "VS Code" };

export function dayKey(ts: number) {
  const d = new Date(ts);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

/** Aktive Segmente aus Zeitpunkten: aufeinanderfolgende Punkte mit Abstand ≤ PAUSE_MS gehören zusammen. */
export function segmentsOf(times: number[], minLen = 30000): [number, number][] {
  const t = [...times].sort((a, b) => a - b);
  const out: [number, number][] = [];
  for (const x of t) {
    const last = out[out.length - 1];
    if (last && x - last[1] <= PAUSE_MS) last[1] = x;
    else out.push([x, x]);
  }
  return out.map(([a, b]) => [a, Math.max(b, a + minLen)] as [number, number]);
}

/** Vereinigung von Intervallen → Gesamtdauer (Wanduhr, parallele Arbeit zählt einmal). */
export function unionMs(intervals: [number, number][]): number {
  const s = [...intervals].sort((a, b) => a[0] - b[0]);
  let total = 0, cs = -1, ce = -1;
  for (const [a, b] of s) {
    if (a > ce) { if (ce > cs) total += ce - cs; cs = a; ce = b; }
    else ce = Math.max(ce, b);
  }
  if (ce > cs) total += ce - cs;
  return total;
}

export function clip(segs: [number, number][], from: number, to: number): [number, number][] {
  return segs.map(([a, b]) => [Math.max(a, from), Math.min(b, to)] as [number, number]).filter(([a, b]) => b > a);
}

/** Liest Claude-Code-Transkripte (~/.claude/projects/**.jsonl) mit Cache nach mtime/Größe. */
export class ClaudeTranscripts {
  private cache = new Map<string, FileCache>();
  constructor(private root = path.join(os.homedir(), ".claude", "projects")) {}

  async sessions(from: number, to: number): Promise<RawSession[]> {
    if (!fs.existsSync(this.root)) return [];
    const files: string[] = [];
    for (const dir of fs.readdirSync(this.root, { withFileTypes: true })) {
      if (!dir.isDirectory()) continue;
      const d = path.join(this.root, dir.name);
      for (const f of fs.readdirSync(d)) if (f.endsWith(".jsonl")) files.push(path.join(d, f));
    }
    const out: RawSession[] = [];
    for (const f of files) {
      let st: fs.Stats;
      try { st = fs.statSync(f); } catch { continue; }
      if (st.mtimeMs < from) continue; // seit Beginn des Zeitraums nicht geändert
      let c = this.cache.get(f);
      if (!c || c.mtime !== st.mtimeMs || c.size !== st.size) {
        c = { mtime: st.mtimeMs, size: st.size, sessions: await parseFile(f) };
        this.cache.set(f, c);
      }
      for (const s of c.sessions.values()) if (s.points.some((p) => p.ts >= from && p.ts < to)) out.push(s);
    }
    return out;
  }
}

const TS = /"timestamp":"([^"]+)"/;

async function parseFile(file: string): Promise<Map<string, RawSession>> {
  const map = new Map<string, RawSession>();
  const rl = readline.createInterface({ input: fs.createReadStream(file, { encoding: "utf8" }), crlfDelay: Infinity });
  const titles = new Map<string, string>();
  for await (const line of rl) {
    if (!line.includes('"sessionId"')) continue;
    if (line.startsWith('{"type":"custom-title"') || line.startsWith('{"type":"ai-title"')) {
      try { const j = JSON.parse(line); const t = j.customTitle ?? j.title; if (t) titles.set(j.sessionId, String(t)); } catch { /* */ }
      continue;
    }
    const m = TS.exec(line);
    if (!m) continue;
    let j: any;
    try { j = JSON.parse(line); } catch { continue; }
    if (j.type !== "user" && j.type !== "assistant") continue;
    const ts = Date.parse(j.timestamp);
    if (!ts) continue;
    let s = map.get(j.sessionId);
    if (!s) {
      s = { id: j.sessionId, cwd: j.cwd ?? "", branch: j.gitBranch && j.gitBranch !== "HEAD" ? j.gitBranch : undefined, entry: ENTRY[j.entrypoint] ?? j.entrypoint ?? "Claude Code",
        title: "", points: [], models: new Set(), prompts: 0, toolCalls: 0, subagents: 0, seen: new Set() };
      map.set(j.sessionId, s);
    }
    if (j.gitBranch && j.gitBranch !== "HEAD") s.branch = j.gitBranch;
    if (j.type === "user") {
      const c = j.message?.content;
      const isPrompt = !j.isSidechain && (typeof c === "string" || (Array.isArray(c) && c.some((x: any) => x?.type === "text")));
      if (isPrompt && !j.isMeta) {
        s.prompts++;
        if (!s.title) {
          const text = typeof c === "string" ? c : c.find((x: any) => x?.type === "text")?.text ?? "";
          s.title = text.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim().slice(0, 80);
        }
      }
      s.points.push({ ts });
    } else {
      const msg = j.message ?? {};
      if (msg.model && msg.model !== "<synthetic>") s.models.add(msg.model);
      for (const c of msg.content ?? []) if (c?.type === "tool_use") { s.toolCalls++; if (c.name === "Task" || c.name === "Agent") s.subagents++; }
      // Streaming schreibt eine Nachricht in mehreren Zeilen → Usage nur einmal pro message.id zählen
      const key = msg.id ?? j.requestId ?? j.uuid;
      if (msg.usage && key && !s.seen.has(key)) {
        s.seen.add(key);
        s.points.push({ ts, usage: fromApiUsage(msg.usage), model: msg.model });
      } else s.points.push({ ts });
    }
  }
  for (const s of map.values()) { s.seen.clear(); const t = titles.get(s.id); if (t) s.title = t; }
  return map;
}

export interface ActivityInput {
  from: number;
  to: number;
  transcripts: RawSession[];
  agents: Agent[];
  runs: Run[];
  prices?: Record<string, Price>;
  /** Pfad → Anzeigename (Repo-relativ) */
  projectName?: (p: string) => string;
}

/** Baut die Activity-Sessions für einen Zeitraum (Claude-Transkripte + Copit-Läufe ohne Transkript, z. B. Copilot). */
export function buildActivity(i: ActivityInput): ActSession[] {
  const name = i.projectName ?? ((p: string) => path.basename(p) || p);
  const bySession = new Map(i.agents.filter((a) => a.sessionId).map((a) => [a.sessionId!, a]));
  const out: ActSession[] = [];

  for (const s of i.transcripts) {
    const pts = s.points.filter((p) => p.ts >= i.from && p.ts < i.to);
    if (!pts.length) continue;
    const agent = bySession.get(s.id);
    const usage = emptyUsage();
    let cost = 0;
    const perDay: ActSession["perDay"] = {};
    for (const p of pts) {
      const d = (perDay[dayKey(p.ts)] ??= { ms: 0, tokens: 0, cost: 0 });
      if (p.usage) {
        addUsage(usage, p.usage);
        const c = costOf(p.usage, p.model, i.prices);
        cost += c;
        d.cost += c;
        d.tokens += p.usage.input + p.usage.output + p.usage.cacheRead + p.usage.cacheWrite;
      }
    }
    const segs = segmentsOf(pts.map((p) => p.ts));
    for (const [a, b] of segs) (perDay[dayKey(a)] ??= { ms: 0, tokens: 0, cost: 0 }).ms += b - a;
    const projectPath = agent?.repo ?? s.cwd;
    let exact = false;
    if (agent) {
      const runCost = i.runs.filter((r) => r.agentId === agent.id && r.startedAt >= i.from && r.startedAt < i.to).reduce((x, r) => x + r.costUsd, 0);
      if (runCost > 0) { const k = runCost / (cost || 1); for (const d of Object.values(perDay)) d.cost *= cost ? k : 0; cost = runCost; exact = true; }
    }
    out.push({
      id: s.id, source: agent ? "copit" : "claude", title: agent?.title ?? (s.title || "Session"), project: name(projectPath), projectPath,
      branch: agent?.branch ?? s.branch, entry: agent ? "Copit" : s.entry, models: [...s.models].map(prettyModel), prompts: s.prompts,
      toolCalls: s.toolCalls, subagents: s.subagents, segments: segs, usage, costUsd: cost, costExact: exact, agentId: agent?.id, ticket: agent?.ticket, perDay,
    });
  }

  // Copit-Läufe ohne Transkript (Copilot oder falls Transkript fehlt)
  const covered = new Set(out.filter((x) => x.agentId).map((x) => x.agentId));
  const agents = new Map(i.agents.map((a) => [a.id, a]));
  const runsByAgent = new Map<string, Run[]>();
  for (const r of i.runs) {
    if (covered.has(r.agentId) || r.startedAt >= i.to || (r.endedAt ?? Date.now()) < i.from) continue;
    (runsByAgent.get(r.agentId) ?? runsByAgent.set(r.agentId, []).get(r.agentId)!).push(r);
  }
  for (const [agentId, runs] of runsByAgent) {
    const a = agents.get(agentId);
    const usage = emptyUsage();
    const perDay: ActSession["perDay"] = {};
    let cost = 0;
    const segs: [number, number][] = [];
    for (const r of runs) {
      const end = r.endedAt ?? Date.now();
      segs.push([r.startedAt, end]);
      addUsage(usage, { input: r.inputTokens ?? 0, output: r.outputTokens ?? 0, cacheRead: r.cacheRead ?? 0, cacheWrite: r.cacheWrite ?? 0 });
      cost += r.costUsd;
      const d = (perDay[dayKey(r.startedAt)] ??= { ms: 0, tokens: 0, cost: 0 });
      d.ms += end - r.startedAt; d.cost += r.costUsd; d.tokens += (r.inputTokens ?? 0) + (r.outputTokens ?? 0) + (r.cacheRead ?? 0) + (r.cacheWrite ?? 0);
    }
    out.push({
      id: "copit:" + agentId, source: "copit", title: a?.title ?? "(gelöscht)", project: name(a?.repo ?? "?"), projectPath: a?.repo ?? "",
      branch: a?.branch, entry: a ? `Copit · ${a.provider}` : "Copit", models: a?.model ? [prettyModel(a.model)] : [], prompts: runs.length,
      toolCalls: 0, subagents: 0, segments: segs, usage, costUsd: cost, costExact: a?.provider === "claude", agentId, ticket: a?.ticket, perDay,
    });
  }
  return out.sort((x, y) => (y.segments[0]?.[0] ?? 0) - (x.segments[0]?.[0] ?? 0));
}

export function prettyModel(m: string): string {
  const x = /claude-(\w+)-(\d+)-(\d+)/.exec(m);
  if (x) return `${x[1][0].toUpperCase()}${x[1].slice(1)} ${x[2]}.${x[3]}`;
  return m;
}

/** Datensätze für Budgets: Kosten und aktive Zeit pro Segment. */
export function usageRecords(sessions: ActSession[]) {
  return sessions.flatMap((s) => {
    const total = s.segments.reduce((t, [a, b]) => t + (b - a), 0) || 1;
    return s.segments.map(([a, b]) => ({ start: a, end: b, costUsd: s.costUsd * ((b - a) / total), path: s.projectPath, project: s.project, provider: s.entry }));
  });
}
