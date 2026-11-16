import { spawn, ChildProcess } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { Mode, Provider } from "../model";

export interface CliCommand { file: string; prefix: string[]; display: string }

/** Findet claude / copilot / git. npm-Shims (*.cmd) werden auf "node script.js" aufgelöst. */
export function locateCli(name: string, configured?: string): CliCommand | undefined {
  if (configured && fs.existsSync(configured)) return fromFile(configured);
  const home = os.homedir();
  const appData = process.env.APPDATA ?? path.join(home, "AppData", "Roaming");
  const local = process.env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
  const dirs = [
    path.join(home, ".local", "bin"), path.join(home, ".claude", "local"), path.join(appData, "npm"),
    path.join(local, "Programs", "claude"), path.join(local, "Microsoft", "WinGet", "Links"),
    ...(process.env.PATH ?? "").split(path.delimiter),
  ].filter(Boolean);
  const exts = process.platform === "win32" ? [".exe", ".cmd", ".bat"] : [""];
  for (const d of dirs) for (const e of exts) {
    const f = path.join(d, name + e);
    try { if (fs.statSync(f).isFile()) return fromFile(f); } catch { /* weiter */ }
  }
  return undefined;
}

function fromFile(f: string): CliCommand {
  const ext = path.extname(f).toLowerCase();
  if (ext === ".cmd" || ext === ".bat") {
    try {
      const txt = fs.readFileSync(f, "utf8");
      const m = /"%(?:~)?dp0%?\\?([^"]+?\.(?:js|mjs|cjs))"/i.exec(txt);
      if (m) {
        const script = path.resolve(path.dirname(f), m[1].replace(/^[\\/]/, ""));
        const node = fs.existsSync(path.join(path.dirname(f), "node.exe")) ? path.join(path.dirname(f), "node.exe") : locateCli("node")?.file ?? "node";
        if (fs.existsSync(script)) return { file: node, prefix: [script], display: f };
      }
    } catch { /* fallback */ }
    return { file: "cmd.exe", prefix: ["/d", "/s", "/c", f], display: f };
  }
  return { file: f, prefix: [], display: f };
}

// ------------------------------------------------------------------ Events

export type CliEvent =
  | { t: "init"; sessionId: string; model: string }
  | { t: "delta"; text: string; parent?: string }
  | { t: "thinking"; parent?: string }
  | { t: "text"; text: string; parent?: string }
  | { t: "tool_use"; id: string; name: string; input: string; parent?: string }
  | { t: "tool_result"; toolId: string; content: string; isError: boolean; parent?: string }
  | { t: "usage"; context: number; output: number }
  | { t: "stderr"; text: string }
  | { t: "result"; isError: boolean; text: string; cost: number; durationMs: number; turns: number; denials: { tool: string; input: string }[]; sessionId?: string; subtype: string };

const str = (v: unknown) => (typeof v === "string" ? v : v == null ? "" : JSON.stringify(v));

function contentToText(c: unknown): string {
  if (typeof c === "string") return c;
  if (Array.isArray(c)) return c.map((p: any) => (p?.type === "text" ? p.text : p?.type === "image" ? "[Bild]" : "")).join("\n").trim();
  return c == null ? "" : JSON.stringify(c);
}

/** Parst eine Zeile von `claude -p --output-format stream-json --verbose --include-partial-messages`. */
export function parseClaudeLine(line: string): CliEvent[] {
  if (!line || line[0] !== "{") return [];
  let n: any;
  try { n = JSON.parse(line); } catch { return []; }
  const parent: string | undefined = n.parent_tool_use_id || undefined;
  const out: CliEvent[] = [];
  switch (n.type) {
    case "system":
      if (n.subtype === "init") out.push({ t: "init", sessionId: str(n.session_id), model: str(n.model) });
      break;
    case "stream_event": {
      const ev = n.event ?? {};
      if (ev.type === "content_block_delta") {
        if (ev.delta?.type === "text_delta") out.push({ t: "delta", text: str(ev.delta.text), parent });
        else if (ev.delta?.type === "thinking_delta") out.push({ t: "thinking", parent });
      } else if (ev.type === "message_start" && ev.message?.usage && !parent) {
        const u = ev.message.usage;
        out.push({ t: "usage", context: (u.input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0), output: u.output_tokens ?? 0 });
      }
      break;
    }
    case "assistant": {
      for (const c of n.message?.content ?? []) {
        if (c.type === "text") out.push({ t: "text", text: str(c.text), parent });
        else if (c.type === "tool_use") out.push({ t: "tool_use", id: str(c.id), name: str(c.name), input: JSON.stringify(c.input ?? {}), parent });
      }
      const u = n.message?.usage;
      if (u && !parent) out.push({ t: "usage", context: (u.input_tokens ?? 0) + (u.cache_creation_input_tokens ?? 0) + (u.cache_read_input_tokens ?? 0), output: u.output_tokens ?? 0 });
      break;
    }
    case "user":
      for (const c of n.message?.content ?? []) {
        if (c?.type === "tool_result") out.push({ t: "tool_result", toolId: str(c.tool_use_id), content: contentToText(c.content), isError: !!c.is_error, parent });
      }
      break;
    case "result":
      out.push({
        t: "result", isError: !!n.is_error, text: str(n.result), cost: Number(n.total_cost_usd ?? 0), durationMs: Number(n.duration_ms ?? 0),
        turns: Number(n.num_turns ?? 0), sessionId: n.session_id || undefined, subtype: str(n.subtype),
        denials: (n.permission_denials ?? []).map((d: any) => ({ tool: str(d.tool_name), input: JSON.stringify(d.tool_input ?? {}) })),
      });
      break;
  }
  return out;
}

// ------------------------------------------------------------------ Runner

export interface RunOptions {
  provider: Provider;
  prompt: string;
  cwd: string;
  mode: Mode;
  model?: string;
  resumeSessionId?: string;
  allowed: string[];
  disallowed: string[];
  systemPrompt?: string;
  /** Verlauf für Copilot (kein Resume) */
  history?: string;
  cliPath?: string;
}

export function claudePermissionMode(m: Mode): string {
  return m === "plan" ? "plan" : m === "edit" ? "acceptEdits" : m === "auto" ? "bypassPermissions" : "default";
}

export function buildClaudeArgs(o: RunOptions): string[] {
  const a = ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-mode", claudePermissionMode(o.mode)];
  if (o.resumeSessionId) a.push("--resume", o.resumeSessionId);
  if (o.model) a.push("--model", o.model);
  if (o.allowed.length) a.push("--allowedTools", o.allowed.join(","));
  if (o.disallowed.length) a.push("--disallowedTools", o.disallowed.join(","));
  if (o.systemPrompt) a.push("--append-system-prompt", o.systemPrompt);
  return a;
}

/** Claude-Muster → Copilot-Tool-Muster */
export function toCopilotTool(p: string): string | undefined {
  if (p === "Edit" || p === "Write" || p === "MultiEdit") return "write";
  if (p === "Bash") return "shell";
  const m = /^Bash\((.+?)(?::\*)?\)$/.exec(p);
  return m ? `shell(${m[1]})` : undefined;
}

export function buildCopilotArgs(o: RunOptions): string[] {
  let prompt = "";
  if (o.systemPrompt) prompt += `[Anweisungen]\n${o.systemPrompt}\n\n`;
  if (o.mode === "plan") prompt += "[Modus: PLAN] Ändere keine Dateien. Erstelle nur einen nummerierten Plan.\n\n";
  if (o.mode === "ask") prompt += "[Modus: FRAGEN] Ändere keine Dateien.\n\n";
  if (o.history) prompt += `[Bisheriger Verlauf]\n${o.history}\n\n[Neue Nachricht]\n`;
  prompt += o.prompt;
  if (prompt.length > 24000) prompt = prompt.slice(-24000);
  const a = ["-p", prompt];
  if (o.mode === "auto") a.push("--allow-all-tools");
  else {
    const allow = new Set(o.allowed.map(toCopilotTool).filter((x): x is string => !!x));
    if (o.mode === "edit") allow.add("write");
    for (const t of allow) a.push("--allow-tool", t);
  }
  for (const t of new Set(o.disallowed.map(toCopilotTool).filter((x): x is string => !!x))) a.push("--deny-tool", t);
  if (o.model) a.push("--model", o.model);
  return a;
}

export interface RunHandle { proc?: ChildProcess; done: Promise<void>; stop(): void }

/** Startet die CLI. onEvent erhält alle Events; das letzte Event ist immer ein "result". */
export function runCli(o: RunOptions, onEvent: (e: CliEvent) => void): RunHandle {
  const name = o.provider === "claude" ? "claude" : "copilot";
  const cmd = locateCli(name, o.cliPath);
  if (!cmd) {
    onEvent({ t: "result", isError: true, subtype: "error", cost: 0, durationMs: 0, turns: 0, denials: [],
      text: name === "claude" ? "Claude CLI nicht gefunden (Einstellung copit.claudePath)." : "Copilot CLI nicht gefunden – npm i -g @github/copilot" });
    return { done: Promise.resolve(), stop() {} };
  }
  const args = o.provider === "claude" ? buildClaudeArgs(o) : buildCopilotArgs(o);
  const started = Date.now();
  const proc = spawn(cmd.file, [...cmd.prefix, ...args], { cwd: o.cwd, env: { ...process.env, NO_COLOR: "1", FORCE_COLOR: "0" }, windowsHide: true });
  let gotResult = false;
  let stopped = false;
  let buf = "";
  let full = "";
  let err = "";
  const emit = (e: CliEvent) => { if (e.t === "result") gotResult = true; try { onEvent(e); } catch { /* UI-Fehler nicht durchreichen */ } };

  if (o.provider === "copilot") emit({ t: "init", sessionId: "", model: o.model || "copilot" });

  proc.stdout.setEncoding("utf8");
  proc.stdout.on("data", (chunk: string) => {
    buf += chunk;
    let i: number;
    while ((i = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, i).replace(/\r$/, "");
      buf = buf.slice(i + 1);
      if (o.provider === "claude") parseClaudeLine(line).forEach(emit);
      else {
        const clean = line.replace(/\x1B\[[0-9;]*[A-Za-z]/g, "");
        full += clean + "\n";
        emit({ t: "delta", text: clean + "\n" });
      }
    }
  });
  proc.stderr.setEncoding("utf8");
  proc.stderr.on("data", (c: string) => { err += c; c.split(/\r?\n/).filter(Boolean).forEach((l) => emit({ t: "stderr", text: l })); });
  if (o.provider === "claude") { proc.stdin.write(o.prompt); }
  proc.stdin.end();

  const done = new Promise<void>((resolve) => {
    proc.on("error", (e) => { if (!gotResult) emit({ t: "result", isError: true, subtype: "error", text: e.message, cost: 0, durationMs: 0, turns: 0, denials: [] }); resolve(); });
    proc.on("close", (code) => {
      if (buf && o.provider === "claude") parseClaudeLine(buf).forEach(emit);
      if (!gotResult) {
        const isError = stopped || code !== 0;
        emit({
          t: "result", isError, subtype: stopped ? "cancelled" : isError ? "error" : "success",
          text: stopped ? "Gestoppt." : (full.trim() || err.trim() || (isError ? `CLI beendet mit Code ${code}` : "")),
          cost: 0, durationMs: Date.now() - started, turns: 1, denials: [],
        });
      }
      resolve();
    });
  });

  return {
    proc, done,
    stop() {
      stopped = true;
      if (process.platform === "win32" && proc.pid) spawn("taskkill", ["/pid", String(proc.pid), "/t", "/f"], { windowsHide: true });
      else proc.kill("SIGTERM");
    },
  };
}
