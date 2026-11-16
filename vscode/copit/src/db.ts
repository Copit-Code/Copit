import * as fs from "node:fs";
import * as path from "node:path";
import initSqlJs, { Database } from "sql.js";
import { Agent, AgentEvent, Automation, PermissionMatrix, Run } from "./model";

const SCHEMA = `
CREATE TABLE IF NOT EXISTS agents (id TEXT PRIMARY KEY, status TEXT NOT NULL, updated_at INTEGER NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, agent_id TEXT NOT NULL, run_id TEXT NOT NULL, ts INTEGER NOT NULL,
  kind TEXT NOT NULL, parent_tool_id TEXT, tool_id TEXT, name TEXT, text TEXT, duration_ms INTEGER, is_error INTEGER);
CREATE INDEX IF NOT EXISTS ix_events_agent ON events(agent_id, id);
CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, started_at INTEGER NOT NULL, ended_at INTEGER,
  ai_ms INTEGER, tool_ms INTEGER, cost REAL, turns INTEGER, prompt TEXT, ok INTEGER);
CREATE INDEX IF NOT EXISTS ix_runs_time ON runs(started_at);
CREATE TABLE IF NOT EXISTS automations (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS automation_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, automation_id TEXT, ts INTEGER, ok INTEGER, message TEXT);
CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT);
`;

/** Lokale SQLite-Datenbank (sql.js / WebAssembly), persistiert als Datei. */
export class CopitDb {
  private saveTimer?: NodeJS.Timeout;

  private constructor(private db: Database, private file: string) {}

  static async open(file: string, wasmDir: string): Promise<CopitDb> {
    const SQL = await initSqlJs({ locateFile: (f: string) => path.join(wasmDir, f) });
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const data = fs.existsSync(file) ? fs.readFileSync(file) : undefined;
    const db = new SQL.Database(data);
    db.run(SCHEMA);
    return new CopitDb(db, file);
  }

  /** Für Tests: reine In-Memory-DB */
  static async memory(wasmDir: string): Promise<CopitDb> {
    const SQL = await initSqlJs({ locateFile: (f: string) => path.join(wasmDir, f) });
    const db = new SQL.Database();
    db.run(SCHEMA);
    return new CopitDb(db, "");
  }

  private touch() {
    if (!this.file) return;
    clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => this.flush(), 400);
  }

  flush() {
    if (!this.file) return;
    clearTimeout(this.saveTimer);
    const tmp = this.file + ".tmp";
    fs.writeFileSync(tmp, Buffer.from(this.db.export()));
    fs.renameSync(tmp, this.file);
  }

  private all<T = any>(sql: string, params: any[] = []): T[] {
    const st = this.db.prepare(sql);
    st.bind(params);
    const out: T[] = [];
    while (st.step()) out.push(st.getAsObject() as T);
    st.free();
    return out;
  }

  private exec(sql: string, params: any[] = []) {
    this.db.run(sql, params);
    this.touch();
  }

  // ---------- Agents ----------
  agents(): Agent[] {
    return this.all<{ json: string }>("SELECT json FROM agents ORDER BY updated_at DESC").map((r) => JSON.parse(r.json));
  }

  agent(id: string): Agent | undefined {
    const r = this.all<{ json: string }>("SELECT json FROM agents WHERE id = ?", [id])[0];
    return r ? JSON.parse(r.json) : undefined;
  }

  saveAgent(a: Agent) {
    a.updatedAt = Date.now();
    this.exec("INSERT OR REPLACE INTO agents (id, status, updated_at, json) VALUES (?, ?, ?, ?)", [a.id, a.status, a.updatedAt, JSON.stringify(a)]);
  }

  deleteAgent(id: string) {
    this.exec("DELETE FROM agents WHERE id = ?", [id]);
    this.exec("DELETE FROM events WHERE agent_id = ?", [id]);
    this.exec("DELETE FROM runs WHERE agent_id = ?", [id]);
  }

  // ---------- Events ----------
  addEvent(e: AgentEvent): number {
    this.db.run(
      "INSERT INTO events (agent_id, run_id, ts, kind, parent_tool_id, tool_id, name, text, duration_ms, is_error) VALUES (?,?,?,?,?,?,?,?,?,?)",
      [e.agentId, e.runId, e.ts, e.kind, e.parentToolId ?? null, e.toolId ?? null, e.name ?? null, e.text ?? null, e.durationMs ?? null, e.isError ? 1 : 0],
    );
    const id = this.all<{ id: number }>("SELECT last_insert_rowid() AS id")[0].id;
    this.touch();
    return id;
  }

  updateEventText(id: number, text: string) {
    this.exec("UPDATE events SET text = ? WHERE id = ?", [text, id]);
  }

  completeTool(agentId: string, toolId: string, durationMs: number, isError: boolean) {
    this.exec("UPDATE events SET duration_ms = ?, is_error = ? WHERE agent_id = ? AND tool_id = ? AND kind = 'tool_use'", [durationMs, isError ? 1 : 0, agentId, toolId]);
  }

  events(agentId: string): AgentEvent[] {
    return this.all<any>("SELECT * FROM events WHERE agent_id = ? ORDER BY id", [agentId]).map((r) => ({
      id: r.id, agentId: r.agent_id, runId: r.run_id, ts: r.ts, kind: r.kind,
      parentToolId: r.parent_tool_id ?? undefined, toolId: r.tool_id ?? undefined, name: r.name ?? undefined,
      text: r.text ?? undefined, durationMs: r.duration_ms ?? undefined, isError: !!r.is_error,
    }));
  }

  // ---------- Runs ----------
  saveRun(r: Run) {
    this.exec("INSERT OR REPLACE INTO runs (id, agent_id, started_at, ended_at, ai_ms, tool_ms, cost, turns, prompt, ok) VALUES (?,?,?,?,?,?,?,?,?,?)",
      [r.id, r.agentId, r.startedAt, r.endedAt ?? null, r.aiMs, r.toolMs, r.costUsd, r.turns, r.prompt, r.ok == null ? null : r.ok ? 1 : 0]);
  }

  runs(from: number, to: number): Run[] {
    return this.all<any>("SELECT * FROM runs WHERE started_at < ? AND COALESCE(ended_at, started_at) >= ? ORDER BY started_at", [to, from]).map((r) => ({
      id: r.id, agentId: r.agent_id, startedAt: r.started_at, endedAt: r.ended_at ?? undefined, aiMs: r.ai_ms ?? 0, toolMs: r.tool_ms ?? 0,
      costUsd: r.cost ?? 0, turns: r.turns ?? 0, prompt: r.prompt ?? "", ok: r.ok == null ? undefined : !!r.ok,
    }));
  }

  // ---------- Automationen ----------
  automations(): Automation[] {
    return this.all<{ json: string }>("SELECT json FROM automations").map((r) => JSON.parse(r.json));
  }

  saveAutomation(a: Automation) {
    this.exec("INSERT OR REPLACE INTO automations (id, json) VALUES (?, ?)", [a.id, JSON.stringify(a)]);
  }

  deleteAutomation(id: string) {
    this.exec("DELETE FROM automations WHERE id = ?", [id]);
  }

  logAutomation(id: string, ok: boolean, message: string) {
    this.exec("INSERT INTO automation_runs (automation_id, ts, ok, message) VALUES (?,?,?,?)", [id, Date.now(), ok ? 1 : 0, message]);
  }

  automationLog(limit = 50): { automation_id: string; ts: number; ok: number; message: string }[] {
    return this.all("SELECT automation_id, ts, ok, message FROM automation_runs ORDER BY id DESC LIMIT ?", [limit]);
  }

  // ---------- Key/Value ----------
  get<T>(key: string, fallback: T): T {
    const r = this.all<{ value: string }>("SELECT value FROM kv WHERE key = ?", [key])[0];
    return r ? (JSON.parse(r.value) as T) : fallback;
  }

  set(key: string, value: unknown) {
    this.exec("INSERT OR REPLACE INTO kv (key, value) VALUES (?, ?)", [key, JSON.stringify(value)]);
  }

  permissions(): PermissionMatrix | undefined {
    return this.get<PermissionMatrix | undefined>("permissions", undefined);
  }
}
