import { COLORS, delegate, dur, esc, icon, md, mount, onState, send, STATUS_LABEL, tokens, usd } from "./ui";

interface Ev { id: number; runId: string; ts: number; kind: string; parentToolId?: string; toolId?: string; name?: string; text?: string; durationMs?: number; isError?: boolean }
interface AgentDto {
  id: string; title: string; ticket?: string; project: string; branch?: string; cwd: string; status: string; provider: string; mode: string; model?: string;
  aiMs: number; agentMs: number; toolMs: number; costUsd: number; contextTokens: number; turns: number; linesAdded: number; linesRemoved: number; filesChanged: number;
  pendingQuestion?: string; pendingPermissions?: { tool: string; input: string; pattern: string }[]; running: boolean; error?: string; startedAt?: number;
}
interface State { agent: AgentDto; events: Ev[]; files: { path: string; added: number; removed: number; status: string }[]; columns: { id: string; title: string }[] }

const root = document.getElementById("app")!;
let state: State | undefined;
let mode: "flow" | "list" = "flow";
const open = new Set<string>();
let filesOpen = false;

const TOOL_ICON: Record<string, string> = { Read: "file", Edit: "pencil", Write: "pencil", MultiEdit: "pencil", Bash: "terminal", Grep: "search", Glob: "search", WebFetch: "link", WebSearch: "link", TodoWrite: "list", Task: "flow", Agent: "flow" };
const VERB: Record<string, string> = { Read: "Liest", Edit: "Bearbeitet", MultiEdit: "Bearbeitet", Write: "Schreibt", Bash: "Führt aus", Grep: "Sucht", Glob: "Findet", WebFetch: "Lädt", WebSearch: "Sucht im Web", TodoWrite: "Plan", Task: "Sub-Agent", Agent: "Sub-Agent" };

function summarize(input?: string): string {
  try {
    const j = JSON.parse(input ?? "{}");
    for (const k of ["description", "file_path", "path", "command", "pattern", "url", "query", "prompt"]) if (j[k]) return String(j[k]).replace(/\s+/g, " ").slice(0, 140);
    if (Array.isArray(j.todos)) return `${j.todos.filter((t: any) => t.status === "completed").length}/${j.todos.length} erledigt`;
  } catch { /* */ }
  return (input ?? "").slice(0, 120);
}

function counts(tools: Ev[]): string {
  const c = { edits: 0, reads: 0, commands: 0, other: 0 };
  for (const t of tools) {
    if (["Edit", "Write", "MultiEdit", "NotebookEdit"].includes(t.name!)) c.edits++;
    else if (["Read", "Grep", "Glob", "LS"].includes(t.name!)) c.reads++;
    else if (t.name === "Bash") c.commands++;
    else if (t.name !== "Task" && t.name !== "Agent") c.other++;
  }
  return [c.edits && `${c.edits} edits`, c.reads && `${c.reads} reads`, c.commands && `${c.commands} commands`, c.other && `${c.other} other`].filter(Boolean).join(", ") || "denkt nach";
}

function toolRow(t: Ev, results: Map<string, Ev>, depth = 0) {
  const r = results.get(t.toolId ?? "");
  const key = "t" + t.id;
  const status = r ? (r.isError ? `<span class="del">${icon("x", 12)}</span>` : `<span class="add">${icon("check", 12)}</span>`) : `<span class="loader"><i></i><i></i><i></i><i></i></span>`;
  return `<div class="tl" style="padding-left:${18 + depth * 18}px">
    <div class="row tool" data-act="toggle" data-arg="${key}">
      ${icon(TOOL_ICON[t.name ?? ""] ?? "zap", 13, "muted")}
      <span class="verb">${esc(VERB[t.name ?? ""] ?? t.name)}</span>
      <span class="mono muted grow trunc">${esc(summarize(t.text))}</span>
      <span class="mono muted">${t.durationMs ? dur(t.durationMs) : ""}</span>${status}
    </div>
    ${open.has(key) ? `<pre class="code">${esc(t.text)}</pre>${r ? `<pre class="code">${esc((r.text ?? "").slice(0, 3000))}</pre>` : ""}` : ""}
  </div>`;
}

function flow(s: State) {
  const runs = new Map<string, Ev[]>();
  for (const e of s.events) { if (!runs.has(e.runId)) runs.set(e.runId, []); runs.get(e.runId)!.push(e); }
  const results = new Map(s.events.filter((e) => e.kind === "tool_result" && e.toolId).map((e) => [e.toolId!, e]));
  let html = "";
  for (const [runId, evs] of runs) {
    if (!runId) {
      for (const e of evs.filter((x) => x.kind === "status" || x.kind === "error"))
        html += `<div class="node"><span class="sq" style="background:${e.kind === "error" ? "var(--red)" : "var(--line)"}"></span><span class="mono muted">${esc(e.text)}</span></div>`;
      continue;
    }
    const user = evs.find((e) => e.kind === "user");
    const main = evs.filter((e) => e.kind === "tool_use" && !e.parentToolId);
    const tasks = main.filter((t) => t.name === "Task" || t.name === "Agent");
    const texts = evs.filter((e) => e.kind === "text" && !e.parentToolId);
    const result = evs.find((e) => e.kind === "result");
    const rk = "r" + runId;
    const expanded = open.has(rk) || (!result && s.agent.running);
    html += `<div class="run">`;
    if (user) html += `<div class="node"><span class="sq"></span>${icon("user", 15)}<b>You</b><span class="grow trunc text2">${esc(user.text)}</span></div>`;
    html += `<div class="node click" data-act="toggle" data-arg="${rk}"><span class="sq acc"></span>${icon("bot", 15)}<b>Main agent</b><span class="text2">${counts(main)}</span>
      <span class="spacer"></span>${icon(expanded ? "chevu" : "chevd", 12, "muted")}</div>`;
    if (expanded) {
      for (const e of evs) {
        if (e.parentToolId) continue;
        if (e.kind === "tool_use" && e.name !== "Task" && e.name !== "Agent") html += toolRow(e, results);
        if (e.kind === "text" && e.text) html += `<div class="say">${md(e.text.replace(/<rueckfrage>[\s\S]*?<\/rueckfrage>/i, ""))}</div>`;
      }
    } else if (texts.length) {
      const last = texts[texts.length - 1].text ?? "";
      html += `<div class="say preview">${md(last.replace(/<rueckfrage>[\s\S]*?<\/rueckfrage>/i, "").slice(0, 600))}</div>`;
    }
    if (tasks.length) {
      html += `<div class="node"><span class="sq" style="background:var(--amber)"></span>${icon("flow", 15)}<b>${tasks.length} Sub-Agent${tasks.length > 1 ? "s" : ""}</b></div>`;
      const maxDur = Math.max(1, ...tasks.map((t) => t.durationMs ?? (Date.now() - t.ts)));
      for (const t of tasks) {
        const done = results.has(t.toolId!);
        const d = t.durationMs ?? (Date.now() - t.ts);
        let sub: any = {};
        try { sub = JSON.parse(t.text ?? "{}"); } catch { /* */ }
        const sk = "s" + t.id;
        const children = s.events.filter((e) => e.parentToolId === t.toolId && e.kind === "tool_use");
        html += `<div class="sub click" data-act="toggle" data-arg="${sk}">
          <span class="sq" style="background:${done ? "var(--green)" : "var(--accent)"}"></span>
          <b class="trunc">${esc(sub.subagent_type ?? "agent")}</b><span class="text2 trunc grow">${esc(sub.description ?? "")}</span>
          <span class="pill">${children.length} Tools</span>
          <div class="bar sbar"><i style="width:${Math.max(4, (d / maxDur) * 100)}%"></i></div>
          ${done ? "" : `<span class="loader"><i></i><i></i><i></i><i></i></span>`}<span class="mono muted">${dur(d)}</span>
        </div>`;
        if (open.has(sk)) for (const c of children) html += toolRow(c, results, 2);
      }
      const back = tasks.filter((t) => results.has(t.toolId!)).length;
      html += `<div class="node"><span class="sq"></span>${back === tasks.length ? icon("check", 14, "add") : `<span class="loader"><i></i><i></i><i></i><i></i></span>`}<span>Results back</span><span class="mono muted">${back} of ${tasks.length}</span></div>`;
    }
    if (result) {
      let r: any = {};
      try { r = JSON.parse(result.text ?? "{}"); } catch { /* */ }
      const ok = !result.isError;
      html += `<div class="node"><span class="sq" style="background:${ok ? "var(--green)" : "var(--red)"}"></span>${icon(ok ? "check-circle" : "x-circle", 15, ok ? "add" : "del")}
        <b>${ok ? "Done" : r.subtype === "cancelled" ? "Gestoppt" : "Fehler"}</b>
        <span class="mono muted">${dur(result.durationMs ?? 0)} · AI ${dur(r.aiMs ?? 0)} · Tools ${dur(r.toolMs ?? 0)}${r.cost ? " · " + usd(r.cost) : ""}</span></div>`;
      if (!ok && r.text) html += `<div class="say del">${esc(r.text)}</div>`;
    } else if (s.agent.running) {
      html += `<div class="node"><span class="loader"><i></i><i></i><i></i><i></i></span><span class="mono muted">arbeitet · ${dur(Date.now() - (user?.ts ?? Date.now()))}</span></div>`;
    }
    html += `</div>`;
  }
  return html || `<div class="empty">Noch keine Läufe. Starte den Agent oder schreibe unten einen Auftrag.</div>`;
}

function list(s: State) {
  return s.events.map((e) => `<div class="li"><span class="mono muted">${new Date(e.ts).toLocaleTimeString("de-DE")}</span>
    <span class="pill">${esc(e.kind)}</span>${e.parentToolId ? `<span class="pill acc">sub</span>` : ""}
    <span class="mono">${esc(e.name ?? "")}</span><span class="grow trunc text2">${esc((e.kind === "tool_use" ? summarize(e.text) : e.text ?? "").slice(0, 200))}</span>
    ${e.durationMs ? `<span class="mono muted">${dur(e.durationMs)}</span>` : ""}</div>`).join("");
}

function render() {
  if (!state) return;
  const a = state.agent;
  const metric = (label: string, value: string, hint = "") => `<div class="m" title="${esc(hint)}"><span class="cap">${label}</span><span class="v">${value}</span></div>`;
  mount(root, `
  <div class="hdr">
    <span class="sq" style="background:${COLORS[a.status]}"></span>
    ${a.ticket ? `<span class="key">${esc(a.ticket)}</span>` : ""}
    <span class="title grow trunc">${esc(a.title)}</span>
    <select id="status" title="Status">${state.columns.map((c) => `<option value="${c.id}" ${c.id === a.status ? "selected" : ""}>${esc(c.title)}</option>`).join("")}</select>
    <button class="ghost" data-act="terminal" title="Terminal im Arbeitsordner">${icon("terminal")}</button>
    ${a.running ? `<button class="danger" data-act="stop">${icon("stop", 12)} Stopp</button>` : `<button data-act="run">${icon("play", 12)} ${a.turns ? "Erneut" : "Starten"}</button>`}
  </div>
  <div class="metrics">
    ${metric("AI Time", dur(a.aiMs), "Zeit, in der das Modell generiert hat")}
    ${metric("Agent Time", dur(a.agentMs + (a.running && a.startedAt ? Date.now() - a.startedAt : 0)), "Gesamtlaufzeit (Wanduhr)")}
    ${metric("Tool Time", dur(a.toolMs), "Ausführung von Tools/Befehlen")}
    ${metric("Turns", String(a.turns))}
    ${metric("Kosten", usd(a.costUsd))}
    ${metric("Kontext", tokens(a.contextTokens))}
    ${metric("Änderungen", `<span class="add">+${a.linesAdded}</span> <span class="del">-${a.linesRemoved}</span>`)}
    <div class="spacer"></div>
    <div class="m"><span class="cap">${esc(a.provider)} · ${esc(a.mode)}</span><span class="v mono">${esc(a.model || "Standard")}</span></div>
    <div class="m"><span class="cap">Branch</span><span class="v mono">${icon("branch", 11)} ${esc(a.branch || "aktueller")}</span></div>
  </div>
  <div class="bar2">
    <span class="cap">${STATUS_LABEL[a.status]}</span><span class="spacer"></span>
    <div class="seg"><button class="${mode === "flow" ? "on" : ""}" data-act="mode" data-value="flow">${icon("flow", 11)} Flow</button><button class="${mode === "list" ? "on" : ""}" data-act="mode" data-value="list">${icon("list", 11)} List</button></div>
  </div>
  <div class="main scroll" id="main" data-scroll>
    ${mode === "flow" ? flow(state) : list(state)}
    ${a.error && a.status === "failed" ? `<div class="box err">${icon("x-circle")} ${esc(a.error)}</div>` : ""}
    ${(a.pendingPermissions ?? []).length ? `<div class="box">
      <div class="row">${icon("lock", 15, "acc")}<b>Freigabe nötig</b></div>
      ${a.pendingPermissions!.map((p) => `<div class="row perm"><span class="mono grow trunc">${esc(p.pattern)}</span><span class="mono muted trunc">${esc(summarize(p.input))}</span>
        <button class="primary" data-act="approve" data-arg="${esc(p.pattern)}">Einmal erlauben</button>
        <button data-act="always" data-arg="${esc(p.pattern)}">Immer erlauben</button></div>`).join("")}
    </div>` : ""}
    ${a.pendingQuestion && !(a.pendingPermissions ?? []).length ? (() => {
      const [q, ...opts] = a.pendingQuestion!.split("\n");
      return `<div class="box"><div class="row">${icon("question", 15, "acc")}<b>${esc(q)}</b></div>
      <div class="row wrap">${opts.map((o) => o.replace(/^- /, "")).map((o) => `<button data-act="answer" data-value="${esc(o)}">${esc(o)}</button>`).join("")}</div></div>`;
    })() : ""}
  </div>
  <div class="files">
    <div class="row click" data-act="files">${icon(filesOpen ? "chevd" : "chevr", 12)}<span>${state.files.length} Dateien</span>
      <span class="mono add">+${a.linesAdded}</span><span class="mono del">-${a.linesRemoved}</span><span class="spacer"></span>
      <button data-act="review" ${state.files.length ? "" : "disabled"}>Review</button></div>
    ${filesOpen ? `<div class="flist scroll">${state.files.map((f) => `<div class="row frow" data-act="diff" data-arg="${esc(f.path)}">
      <span class="pill ${f.status === "A" ? "acc" : ""}">${f.status}</span><span class="mono grow trunc">${esc(f.path)}</span>
      <span class="mono add">+${f.added}</span><span class="mono del">-${f.removed}</span></div>`).join("")}</div>` : ""}
  </div>
  <div class="composer">
    <textarea id="msg" data-keep placeholder="${a.status === "need_input" ? "Antworten…" : "Folgeauftrag an den Agent…"}   Ctrl+Enter = senden"></textarea>
    <div class="row cfoot"><span class="cap">${esc(a.mode)} · ${esc(a.model || "Standard")} · ${esc(a.project)}</span><span class="spacer"></span>
      <button class="primary" data-act="send" ${a.running ? "disabled" : ""}>${icon("send", 12)} Senden</button></div>
  </div>`);
  const main = document.getElementById("main");
  if (main && stick) main.scrollTop = main.scrollHeight;
}

let stick = true;
document.addEventListener("scroll", (e) => {
  const m = e.target as HTMLElement;
  if (m.id === "main") stick = m.scrollTop + m.clientHeight >= m.scrollHeight - 40;
}, true);

onState<State>((s) => { state = s; render(); });

function sendMsg() {
  const t = document.getElementById("msg") as HTMLTextAreaElement;
  if (!t?.value.trim() || state?.agent.running) return;
  send("act", { act: "reply", value: t.value });
  t.value = "";
  stick = true;
}

delegate({
  toggle: (el) => { const k = el.dataset.arg!; open.has(k) ? open.delete(k) : open.add(k); render(); },
  mode: (el) => { mode = el.dataset.value as any; render(); },
  files: () => { filesOpen = !filesOpen; render(); },
  send: () => sendMsg(),
});
document.addEventListener("keydown", (e) => {
  if ((e.target as HTMLElement).id === "msg" && e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); sendMsg(); }
});
document.addEventListener("change", (e) => {
  const t = e.target as HTMLSelectElement;
  if (t.id === "status") send("act", { act: "status", value: t.value });
});
// Laufzeit-Anzeige aktualisieren
setInterval(() => { if (state?.agent.running) render(); }, 1000);

const css = document.createElement("style");
css.textContent = `
#status { width: 170px; }
.metrics { display: flex; gap: 26px; padding: 12px 16px; border-bottom: 1px solid var(--line-soft); flex-wrap: wrap; }
.m { display: flex; flex-direction: column; gap: 4px; }
.m .v { font-size: 15px; font-weight: 600; font-family: var(--mono); }
.bar2 { display: flex; align-items: center; padding: 8px 16px; border-bottom: 1px solid var(--line-soft); }
.main { flex: 1; padding: 10px 20px 20px; }
.run { border-bottom: 1px dashed var(--line); padding: 8px 0 10px; }
.node { display: flex; align-items: center; gap: 9px; padding: 4px 0; min-width: 0; }
.node .sq.acc { background: var(--accent); }
.click { cursor: pointer; }
.click:hover { background: #1B1714; }
.tl { border-left: 1px solid var(--line); margin-left: 3px; }
.tool { gap: 9px; padding: 3px 0; cursor: pointer; }
.tool .verb { color: var(--text2); white-space: nowrap; }
.say { padding: 4px 0 6px 21px; line-height: 1.5; border-left: 1px solid var(--line); margin-left: 3px; }
.say.preview { color: var(--text2); }
.sub { display: flex; align-items: center; gap: 9px; padding: 4px 0 4px 21px; border-left: 1px solid var(--line); margin-left: 3px; }
.sbar { width: 120px; flex: none; }
.li { display: flex; gap: 8px; align-items: center; padding: 3px 0; border-bottom: 1px solid var(--line-soft); }
.box { border: 1px solid var(--accent); padding: 12px; margin: 12px 0; display: flex; flex-direction: column; gap: 10px; background: #1B1714; }
.box.err { border-color: var(--red); color: var(--red); flex-direction: row; }
.acc { color: var(--accent); }
.perm { gap: 10px; }
.wrap { flex-wrap: wrap; }
.files { border-top: 1px solid var(--line-soft); padding: 8px 16px; }
.files .row { gap: 10px; }
.flist { max-height: 200px; margin-top: 6px; }
.frow { padding: 3px 0; cursor: pointer; } .frow:hover { background: #1B1714; }
.composer { border-top: 1px solid var(--line-soft); padding: 10px 16px 12px; }
.composer textarea { min-height: 56px; border-color: var(--line); }
.cfoot { margin-top: 6px; }
`;
document.head.appendChild(css);
