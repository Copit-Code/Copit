import { delegate, esc, icon, mount, onState, send } from "./ui";

interface Sess {
  id: string; source: string; title: string; project: string; projectPath: string; branch?: string; entry: string; models: string[];
  prompts: number; toolCalls: number; subagents: number; segments: [number, number][];
  usage: { input: number; output: number; cacheRead: number; cacheWrite: number }; costUsd: number; costExact: boolean;
  agentId?: string; ticket?: string; running?: boolean;
}
interface State { mode: "day" | "week"; scope: "workspace" | "all"; anchor: number; from: number; to: number; weekFrom: number; sessions: Sess[]; weekSessions: Sess[]; now: number }

const root = document.getElementById("app")!;
let state: State | undefined;
const collapsed = new Set<string>();
const PALETTE = ["#E8743B", "#7FA6C9", "#7BC47F", "#D9A441", "#D88AB0", "#7FC2B8", "#B59AE0", "#C9A27F"];
const color = (p: string) => { let h = 0; for (const c of p) h = (h * 31 + c.charCodeAt(0)) | 0; return PALETTE[Math.abs(h) % PALETTE.length]; };
const DAY = 86400000;

// ------------------------------------------------------------ Rechnen
function union(iv: [number, number][]): number {
  const s = [...iv].sort((a, b) => a[0] - b[0]);
  let t = 0, cs = -1, ce = -1;
  for (const [a, b] of s) { if (a > ce) { if (ce > cs) t += ce - cs; cs = a; ce = b; } else ce = Math.max(ce, b); }
  return ce > cs ? t + ce - cs : t;
}
const clip = (segs: [number, number][], f: number, t: number) => segs.map(([a, b]) => [Math.max(a, f), Math.min(b, t)] as [number, number]).filter(([a, b]) => b > a);
const sum = (segs: [number, number][]) => segs.reduce((x, [a, b]) => x + b - a, 0);
const tokTotal = (u: Sess["usage"]) => u.input + u.output + u.cacheRead + u.cacheWrite;

function hm(ms: number) {
  if (ms <= 0) return "–";
  const m = Math.round(ms / 60000);
  if (m < 1) return "<1m";
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, "0")}m`;
}
const tok = (n: number) => n >= 1e9 ? `${(n / 1e9).toFixed(1)}B` : n >= 1e6 ? `${Math.round(n / 1e6)}M` : n >= 1e3 ? `${Math.round(n / 1e3)}k` : String(n);
const usd = (n: number) => n >= 100 ? `$${Math.round(n).toLocaleString("de-DE")}` : `$${n.toFixed(2)}`;
const time = (ts: number) => new Date(ts).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
const range = (segs: [number, number][]) => segs.length ? `${time(Math.min(...segs.map((s) => s[0])))}–${time(Math.max(...segs.map((s) => s[1])))}` : "";

interface Group { project: string; path: string; sessions: Sess[] }
function groups(list: Sess[]): Group[] {
  const m = new Map<string, Group>();
  for (const s of list) {
    const g = m.get(s.project) ?? m.set(s.project, { project: s.project, path: s.projectPath, sessions: [] }).get(s.project)!;
    g.sessions.push(s);
  }
  return [...m.values()].sort((a, b) => union(b.sessions.flatMap((s) => s.segments)) - union(a.sessions.flatMap((s) => s.segments)));
}

function kpis(list: Sess[], f: number, t: number) {
  const segs = list.map((s) => clip(s.segments, f, t));
  const all = segs.flat();
  const tokens = list.reduce((x, s) => x + tokTotal(s.usage), 0);
  const out = list.reduce((x, s) => x + s.usage.output, 0);
  const cost = list.reduce((x, s) => x + s.costUsd, 0);
  const exact = list.every((s) => s.costExact);
  return {
    ai: union(all), agent: segs.reduce((x, s) => x + sum(s), 0), tokens, out, cost, exact,
    agents: list.length, projects: new Set(list.map((s) => s.project)).size, prompts: list.reduce((x, s) => x + s.prompts, 0), range: range(all),
  };
}

// ------------------------------------------------------------ Tag
function dayStrip(s: State) {
  const days = [...Array(7)].map((_, i) => s.weekFrom + i * DAY);
  const per = days.map((d) => union(s.weekSessions.flatMap((x) => clip(x.segments, d, d + DAY))));
  const max = Math.max(1, ...per);
  return `<div class="strip">${days.map((d, i) => {
    const sel = s.mode === "day" && d === s.from;
    return `<button class="dayb ${sel ? "sel" : ""} ${per[i] ? "" : "empty"}" data-act="pickday" data-value="${d}">
      <span class="dl">${new Date(d).toLocaleDateString("de-DE", { weekday: "short" })} ${new Date(d).getDate()}</span>
      <span class="db"><i style="height:${per[i] ? Math.max(12, (per[i] / max) * 100) : 0}%"></i></span>
      <span class="mono dt">${per[i] ? hm(per[i]) : ""}</span></button>`;
  }).join("")}</div>`;
}

function kpiCards(k: ReturnType<typeof kpis>) {
  return `<div class="kpis">
    <div class="kpi"><span class="kl">AI time</span><span class="kv">${hm(k.ai)}</span><span class="ks">${k.range || "–"}</span></div>
    <div class="kpi"><span class="kl">Agent time</span><span class="kv">${hm(k.agent)}</span><span class="ks">Parallele Agents addieren sich</span></div>
    <div class="kpi"><span class="kl">Tokens</span><span class="kv">${tok(k.tokens)}</span><span class="ks">${tok(k.out)} output</span></div>
    <div class="kpi acc"><span class="kl">Kosten</span><span class="kv">${usd(k.cost)}</span><span class="ks">${k.exact ? "laut CLI" : "geschätzt · Tokens × Listenpreis"}</span></div>
    <div class="kpi"><span class="kl">Agents</span><span class="kv">${k.agents}</span><span class="ks">${k.projects} Projekte · ${k.prompts} Prompts</span></div>
  </div>`;
}

const INFO = `<div class="info">${icon("info", 13)}<span>AI time ist die Wanduhrzeit, in der mindestens ein Agent gearbeitet hat; Pausen über 10 Minuten zählen nicht.
  Agent time addiert die Zeit jedes Agents – zwei parallel arbeitende Agents zählen doppelt. Kosten: von der CLI gemeldet, sonst Tokens × Listenpreis (Einstellung copit.prices).</span></div>`;

function timeline(segs: [number, number][], f: number, t: number, cls: string, col?: string) {
  const w = t - f;
  return segs.map(([a, b]) => `<i class="${cls}" style="left:${((a - f) / w) * 100}%;width:${Math.max(0.35, ((b - a) / w) * 100)}%;${col ? `background:${col}` : ""}"></i>`).join("");
}

function dayView(s: State) {
  const list = s.sessions;
  const k = kpis(list, s.from, s.to);
  const all = list.flatMap((x) => clip(x.segments, s.from, s.to));
  let h0 = all.length ? new Date(Math.min(...all.map((a) => a[0]))).getHours() : 8;
  let h1 = all.length ? new Date(Math.max(...all.map((a) => a[1]))).getHours() + 1 : 18;
  if (h1 - h0 < 4) { h0 = Math.max(0, h0 - 1); h1 = Math.min(24, h0 + 5); }
  const f = s.from + h0 * 3600000, t = s.from + h1 * 3600000;
  const hours = [...Array(h1 - h0 + 1)].map((_, i) => h0 + i);
  const isToday = s.now >= s.from && s.now < s.to;
  const nowLine = isToday && s.now > f && s.now < t ? `<b class="now" style="left:${((s.now - f) / (t - f)) * 100}%"></b>` : "";
  const grid = hours.map((h) => `<b class="hl" style="left:${((h - h0) / (h1 - h0)) * 100}%"></b>`).join("");
  const axis = hours.slice(0, -1).map((h) => `<span style="left:${((h - h0) / (h1 - h0)) * 100}%">${h}</span>`).join("");

  const rows = groups(list).map((g) => {
    const gsegs = g.sessions.flatMap((x) => clip(x.segments, s.from, s.to));
    const open = !collapsed.has(g.project);
    const gTok = g.sessions.reduce((x, y) => x + tokTotal(y.usage), 0), gOut = g.sessions.reduce((x, y) => x + y.usage.output, 0);
    const gCost = g.sessions.reduce((x, y) => x + y.costUsd, 0);
    const c = color(g.project);
    return `<div class="grow2 proj" data-act="toggle" data-arg="${esc(g.project)}">
        <div class="name">${icon(open ? "chevd" : "chevr", 11, "muted")}<b>${esc(g.project)}</b><span class="dot" style="background:${c}"></span><span class="cnt">${g.sessions.length}</span></div>
        <div class="tl">${grid}${timeline(gsegs, f, t, "gb")}${nowLine}</div>
        <div class="num"><b>${hm(union(gsegs))}</b><span>${range(gsegs)}</span></div>
        <div class="num"><b>${tok(gTok)}</b><span>${tok(gOut)} out</span></div>
        <div class="num"><b class="acc">${usd(gCost)}</b><span>&nbsp;</span></div>
      </div>` + (open ? g.sessions.map((x) => {
        const segs = clip(x.segments, s.from, s.to);
        const meta = [x.branch ? `${icon("branch", 11)} ${esc(x.branch)}` : "", esc(x.entry), esc(x.models.join(", ")),
          `${x.prompts} Prompts`, x.toolCalls ? `${x.toolCalls} Tool-Calls` : "", x.subagents ? `${x.subagents} Sub-Agents` : ""].filter(Boolean).join('<span class="sep"></span>');
        return `<div class="grow2 ag" ${x.agentId ? `data-act="open" data-arg="${x.agentId}"` : ""}>
          <div class="name"><span class="sd" style="background:${x.running ? "var(--green)" : c}"></span>
            <div class="nt"><div class="trunc">${x.ticket ? `<span class="key">${esc(x.ticket)}</span> ` : ""}${esc(x.title)}</div><div class="meta">${meta}</div></div></div>
          <div class="tl">${grid}${timeline(segs, f, t, "ab", c)}${nowLine}</div>
          <div class="num"><b>${hm(union(segs))}</b><span>${range(segs)}</span></div>
          <div class="num"><b>${tok(tokTotal(x.usage))}</b><span>${tok(x.usage.output)} out</span></div>
          <div class="num"><b>${usd(x.costUsd)}</b><span>${x.costExact ? "CLI" : "geschätzt"}</span></div>
        </div>`;
      }).join("") : "");
  }).join("");

  return `${dayStrip(s)}${kpiCards(k)}${INFO}
    <div class="table">
      <div class="grow2 head"><div class="name cap">Projekt · Agent</div><div class="tl axis">${axis}</div><div class="num cap">AI time</div><div class="num cap">Tokens</div><div class="num cap">Kosten</div></div>
      ${rows || `<div class="empty">Keine Agent-Aktivität an diesem Tag.</div>`}
    </div>`;
}

// ------------------------------------------------------------ Woche
function lanes<T extends { a: number; b: number }>(items: T[]): (T & { lane: number; of: number })[] {
  const sorted = [...items].sort((x, y) => x.a - y.a);
  const out: (T & { lane: number; of: number })[] = [];
  let cluster: (T & { lane: number; of: number })[] = [], ends: number[] = [], cEnd = 0;
  const flush = () => { cluster.forEach((c) => (c.of = Math.max(1, ends.length))); out.push(...cluster); cluster = []; ends = []; };
  for (const it of sorted) {
    const b = Math.max(it.b, it.a + 25 * 60000);
    if (cluster.length && it.a >= cEnd) flush();
    let lane = ends.findIndex((e) => e <= it.a);
    if (lane < 0) { lane = ends.length; ends.push(0); }
    ends[lane] = b; cEnd = Math.max(cEnd, b);
    cluster.push({ ...it, lane, of: 1 });
  }
  flush();
  return out;
}

function weekView(s: State) {
  const list = s.weekSessions;
  const k = kpis(list, s.weekFrom, s.weekFrom + 7 * DAY);
  const PX = 46;
  let days = [...Array(7)].map((_, i) => s.weekFrom + i * DAY);
  days = days.filter((d, i) => i < 5 || list.some((x) => clip(x.segments, d, d + DAY).length));
  const hours = [...Array(24)].map((_, i) => i);
  const today = new Date(s.now).toDateString();

  const cal = `<div class="cal scroll" id="cal" data-scroll><div class="cgrid" style="grid-template-columns:56px repeat(${days.length},1fr)">
    <div class="times"><div class="dh"></div>${hours.map((h) => `<div class="hr mono">${String(h).padStart(2, "0")}:00</div>`).join("")}</div>
    ${days.map((d) => {
      // Sessions unter 3 Minuten nur im Stundenzettel, nicht als Kalenderblock
      const blocks = lanes(list.flatMap((x) => clip(x.segments, d, d + DAY).filter(([a, b]) => b - a >= 3 * 60000).map(([a, b]) => ({ a, b, x }))));
      const tot = union(list.flatMap((x) => clip(x.segments, d, d + DAY)));
      const isToday = new Date(d).toDateString() === today;
      return `<div class="day ${isToday ? "today" : ""}">
        <div class="dh"><span class="muted">${new Date(d).toLocaleDateString("de-DE", { weekday: "short" })}</span> <b class="${isToday ? "badge" : ""}">${new Date(d).getDate()}</b><div class="mono">${tot ? hm(tot) : ""}</div></div>
        <div class="slots" style="height:${24 * PX}px">${hours.map(() => `<div class="slot" style="height:${PX}px"></div>`).join("")}
          ${blocks.map(({ a, b, x, lane, of }) => {
            const st = new Date(a);
            const top = (st.getHours() + st.getMinutes() / 60) * PX;
            const h = Math.max(22, ((b - a) / 3600000) * PX);
            const c = color(x.project);
            return `<div class="blk" style="top:${top}px;height:${h}px;left:calc(${(lane / of) * 100}% + 2px);width:calc(${100 / of}% - 4px);border-left-color:${c};background:${c}26"
              ${x.agentId ? `data-act="open" data-arg="${x.agentId}"` : ""} title="${esc(x.title)} · ${time(a)}–${time(b)}">
              <div class="row bt"><b class="trunc">${esc(x.project)}</b><span class="mono">${hm(b - a)}</span></div>
              ${h > 34 ? `<div class="bd">${esc(x.title)}</div>` : ""}</div>`;
          }).join("")}
          ${isToday ? `<div class="nowl" style="top:${(new Date(s.now).getHours() + new Date(s.now).getMinutes() / 60) * PX}px"></div>` : ""}
        </div></div>`;
    }).join("")}</div></div>`;

  const cols = `minmax(260px,2fr) repeat(${days.length}, minmax(64px,1fr)) 90px 80px 90px`;
  const sheet = `<div class="sheet"><div class="srow head" style="grid-template-columns:${cols}"><div class="cap">Ticket / Agent</div>
      ${days.map((d) => `<div class="cap r">${new Date(d).toLocaleDateString("de-DE", { weekday: "short", day: "numeric" })}</div>`).join("")}
      <div class="cap r">Gesamt</div><div class="cap r">Tokens</div><div class="cap r">Kosten</div></div>
    ${groups(list).map((g) => {
      const per = days.map((d) => union(g.sessions.flatMap((x) => clip(x.segments, d, d + DAY))));
      const c = color(g.project);
      return `<div class="srow grp" style="grid-template-columns:${cols}"><div><span class="dot" style="background:${c}"></span><b>${esc(g.project)}</b></div>
        ${per.map((v) => `<div class="r mono"><b>${v ? hm(v) : ""}</b></div>`).join("")}
        <div class="r mono"><b>${hm(union(g.sessions.flatMap((x) => clip(x.segments, s.weekFrom, s.weekFrom + 7 * DAY))))}</b></div>
        <div class="r mono"><b>${tok(g.sessions.reduce((a, x) => a + tokTotal(x.usage), 0))}</b></div>
        <div class="r mono acc"><b>${usd(g.sessions.reduce((a, x) => a + x.costUsd, 0))}</b></div></div>` +
        g.sessions.map((x) => {
          const pd = days.map((d) => union(clip(x.segments, d, d + DAY)));
          return `<div class="srow" style="grid-template-columns:${cols}" ${x.agentId ? `data-act="open" data-arg="${x.agentId}"` : ""}>
            <div class="trunc">${x.ticket ? `<span class="key">${esc(x.ticket)}</span> ` : ""}${esc(x.title)}</div>
            ${pd.map((v) => `<div class="r mono">${v ? hm(v) : ""}</div>`).join("")}
            <div class="r mono">${hm(union(clip(x.segments, s.weekFrom, s.weekFrom + 7 * DAY)))}</div>
            <div class="r mono muted">${tok(tokTotal(x.usage))}</div><div class="r mono">${usd(x.costUsd)}</div></div>`;
        }).join("");
    }).join("") || `<div class="empty">Keine Aktivität in dieser Woche.</div>`}</div>`;

  return `<div class="sumline"><b>${hm(k.ai)}</b> AI time <b>${hm(k.agent)}</b> Agent time <b>${tok(k.tokens)}</b> Tokens <b class="acc">${usd(k.cost)}</b> Kosten <b>${k.agents}</b> Agents in <b>${k.projects}</b> Projekten</div>
    ${INFO}${cal}${sheet}`;
}

// ------------------------------------------------------------ Markdown
function markdown(s: State): string {
  if (s.mode === "day") {
    const d = new Date(s.from).toLocaleDateString("de-DE", { weekday: "long", day: "numeric", month: "long", year: "numeric" });
    const k = kpis(s.sessions, s.from, s.to);
    let md = `## Agent-Aktivität ${d}\n\n**${hm(k.ai)}** AI time · **${hm(k.agent)}** Agent time · **${tok(k.tokens)}** Tokens · **${usd(k.cost)}**\n\n| Projekt | Agent | AI time | Zeitraum | Tokens | Kosten |\n|---|---|---:|---|---:|---:|\n`;
    for (const g of groups(s.sessions)) for (const x of g.sessions) {
      const segs = clip(x.segments, s.from, s.to);
      md += `| ${g.project} | ${x.ticket ? x.ticket + " " : ""}${x.title} | ${hm(union(segs))} | ${range(segs)} | ${tok(tokTotal(x.usage))} | ${usd(x.costUsd)} |\n`;
    }
    return md;
  }
  const days = [...Array(7)].map((_, i) => s.weekFrom + i * DAY);
  let md = `## Stundenzettel ${new Date(s.weekFrom).toLocaleDateString("de-DE")} – ${new Date(s.weekFrom + 6 * DAY).toLocaleDateString("de-DE")}\n\n| Projekt / Agent | ${days.map((d) => new Date(d).toLocaleDateString("de-DE", { weekday: "short", day: "numeric" })).join(" | ")} | Gesamt | Kosten |\n|---|${days.map(() => "---:").join("|")}|---:|---:|\n`;
  for (const g of groups(s.weekSessions)) for (const x of g.sessions)
    md += `| ${g.project} · ${x.ticket ? x.ticket + " " : ""}${x.title} | ${days.map((d) => { const v = union(clip(x.segments, d, d + DAY)); return v ? hm(v) : ""; }).join(" | ")} | ${hm(union(x.segments))} | ${usd(x.costUsd)} |\n`;
  return md;
}

// ------------------------------------------------------------ Rahmen
function render() {
  if (!state) return;
  const s = state;
  const label = s.mode === "day"
    ? (s.now >= s.from && s.now < s.to ? "Heute" : new Date(s.from).toLocaleDateString("de-DE", { weekday: "long", day: "numeric", month: "short" }))
    : `${s.now >= s.weekFrom && s.now < s.weekFrom + 7 * DAY ? "Diese Woche" : "Woche"} · ${new Date(s.weekFrom).toLocaleDateString("de-DE", { day: "numeric", month: "short" })} – ${new Date(s.weekFrom + 6 * DAY).toLocaleDateString("de-DE", { day: "numeric", month: "short" })}`;
  mount(root, `
  <div class="hdr">
    <span class="logo"></span><span class="title">Activity</span><span class="spacer"></span>
    <button class="icon" data-act="nav" data-value="-1">${icon("chevl")}</button>
    <button class="ghost lbl" data-act="nav" data-value="0">${esc(label)} ${icon("calendar", 13)}</button>
    <button class="icon" data-act="nav" data-value="1">${icon("chevr")}</button>
    <div class="seg"><button class="${s.mode === "week" ? "on" : ""}" data-act="mode" data-value="week">${icon("calendar", 11)} Woche</button><button class="${s.mode === "day" ? "on" : ""}" data-act="mode" data-value="day">${icon("zap", 11)} Tag</button></div>
    <div class="seg"><button class="${s.scope === "workspace" ? "on" : ""}" data-act="scope" data-value="workspace">Dieser Workspace</button><button class="${s.scope === "all" ? "on" : ""}" data-act="scope" data-value="all">Alle Projekte</button></div>
    <button class="ghost" data-act="copy">${icon("copy", 13)} ${s.mode === "day" ? "Als Markdown kopieren" : "Stundenzettel kopieren"}</button>
    <button class="icon" data-act="refresh">${icon("refresh", 14)}</button>
  </div>
  <div class="scroll body" id="body" data-scroll><div class="wrap">${s.mode === "day" ? dayView(s) : weekView(s)}</div></div>`);
}

let scrolled = false;
onState<State>((s) => {
  const modeChanged = state?.mode !== s.mode;
  state = s; render();
  if (s.mode === "week" && (!scrolled || modeChanged)) {
    scrolled = true;
    const c = document.getElementById("cal");
    const first = s.weekSessions.flatMap((x) => x.segments).map((g) => new Date(g[0]).getHours());
    if (c) c.scrollTop = Math.max(0, (first.length ? Math.min(...first) : 8) - 1) * 46;
  }
});
delegate({
  toggle: (el) => { const k = el.dataset.arg!; collapsed.has(k) ? collapsed.delete(k) : collapsed.add(k); render(); },
  copy: () => { if (state) send("act", { act: "copy", value: markdown(state) }); },
});
setInterval(() => send("refresh"), 60000);

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.hdr .lbl { font-weight: 700; color: var(--text); }
.body { flex: 1; }
.wrap { padding: 14px 18px 40px; display: flex; flex-direction: column; gap: 14px; }
.strip { display: grid; grid-template-columns: repeat(7, 1fr); gap: 8px; }
.dayb { flex-direction: column; align-items: center; gap: 6px; padding: 8px 4px; border: 1px solid transparent; height: 96px; }
.dayb.sel { border-color: var(--line); background: var(--panel); }
.dayb.empty .dl { color: var(--muted); }
.dl { font-size: 12px; font-weight: 600; }
.db { width: 46px; height: 34px; background: var(--line-soft); position: relative; display: block; }
.db i { position: absolute; left: 0; right: 0; bottom: 0; background: var(--accent); }
.dt { font-size: 10.5px; color: var(--text2); min-height: 13px; }
.kpis { display: grid; grid-template-columns: repeat(5, 1fr); gap: 10px; }
.kpi { border: 1px solid var(--line-soft); background: var(--panel); padding: 12px 14px; display: flex; flex-direction: column; gap: 5px; }
.kpi.acc { border-color: #5A3A28; }
.kl { font-size: 12px; color: var(--text2); }
.kv { font-size: 24px; font-weight: 700; }
.kpi.acc .kv, .acc { color: var(--accent); }
.ks { font-size: 11px; color: var(--muted); }
.info { display: flex; gap: 8px; color: var(--text2); font-size: 11.5px; max-width: 900px; line-height: 1.5; }
.table { border: 1px solid var(--line-soft); }
.grow2 { display: grid; grid-template-columns: minmax(260px, 2.2fr) 4fr 96px 90px 84px; align-items: center; gap: 12px; padding: 9px 12px; border-top: 1px solid var(--line-soft); }
.grow2.head { border-top: none; padding: 8px 12px; }
.grow2.proj { cursor: pointer; background: #17130F; }
.grow2.ag[data-act] { cursor: pointer; }
.grow2:hover:not(.head) { background: #1E1916; }
.name { display: flex; align-items: center; gap: 8px; min-width: 0; }
.ag .name { padding-left: 20px; align-items: flex-start; }
.nt { min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.meta { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 11px; color: var(--muted); }
.sep { width: 3px; height: 3px; background: var(--line); }
.dot { width: 7px; height: 7px; display: inline-block; flex: none; }
.sd { width: 7px; height: 7px; flex: none; margin-top: 5px; }
.cnt { font-family: var(--mono); font-size: 10.5px; color: var(--muted); border: 1px solid var(--line); padding: 0 5px; }
.tl { position: relative; height: 22px; }
.tl .hl { position: absolute; top: -4px; bottom: -4px; width: 1px; background: #241E19; }
.tl .now { position: absolute; top: -9px; bottom: -9px; border-left: 1px dashed var(--muted); }
.tl i { position: absolute; top: 6px; height: 10px; }
.tl i.gb { background: #5A5048; }
.axis span { position: absolute; top: 3px; transform: translateX(-50%); font-family: var(--mono); font-size: 10.5px; color: var(--muted); }
.num { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; text-align: right; }
.num b { font-size: 13px; } .num span { font-size: 10.5px; color: var(--muted); font-family: var(--mono); }
.sumline { display: flex; gap: 6px; flex-wrap: wrap; color: var(--text2); align-items: baseline; }
.sumline b { color: var(--text); margin-left: 10px; } .sumline b:first-child { margin-left: 0; }
.cal { max-height: 520px; border: 1px solid var(--line-soft); }
.cgrid { display: grid; min-width: 700px; }
.times .hr { font-size: 10.5px; color: var(--muted); padding: 0 8px; height: 46px; transform: translateY(-7px); text-align: right; }
.dh { height: 56px; padding: 8px 10px; border-bottom: 1px solid var(--line-soft); position: sticky; top: 0; background: var(--bg); z-index: 2; }
.day { border-left: 1px solid var(--line-soft); }
.day.today { background: #17130F; }
.badge { background: var(--accent); color: var(--bg); padding: 0 5px; }
.slots { position: relative; }
.slot { border-bottom: 1px solid #1E1916; }
.blk { position: absolute; border-left: 2px solid; padding: 4px 6px; font-size: 11.5px; overflow: hidden; cursor: default; }
.blk[data-act] { cursor: pointer; } .blk:hover { filter: brightness(1.2); }
.bt { justify-content: space-between; gap: 6px; } .bt span { font-size: 10.5px; color: var(--text2); }
.bd { color: var(--text2); margin-top: 2px; line-height: 1.3; }
.nowl { position: absolute; left: 0; right: 0; border-top: 1px solid var(--accent); }
.sheet { border: 1px solid var(--line-soft); }
.srow { display: grid; gap: 10px; padding: 8px 12px; border-top: 1px solid var(--line-soft); align-items: center; }
.srow.head { border-top: none; }
.srow.grp { background: #17130F; } .srow.grp .dot { margin-right: 8px; }
.srow[data-act] { cursor: pointer; } .srow[data-act]:hover { background: #1E1916; }
.r { text-align: right; }
`;
document.head.appendChild(css);
