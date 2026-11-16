import { delegate, esc, icon, mount, onState, send } from "./ui";

interface Sugg { topic?: string; repo?: string; issueKey: string; issueSummary: string; hours: number; comment: string; reason: string; confidence: number; account?: string; accountFromMemory?: boolean; booked?: boolean; error?: string }
interface Day { date: number; inMonth: boolean; isToday: boolean; workDay: boolean; booked: number; target: number; hasNote: boolean; hasDraft: boolean }
interface Rule { id: string; name: string; text: string; enabled: boolean }
interface State {
  month: number; days: Day[]; selected: number; loading: boolean; jira: boolean; accountRequired: boolean;
  entry: { note: string; target?: number; suggestions: Sugg[]; summary: string };
  worklogs: { issueKey: string; issueSummary: string; seconds: number; comment: string }[];
  working: boolean; booking: boolean; steps: string[]; error?: string;
  accountDialog?: { index: number; issueKey: string; summary: string; options: string[] };
  rules: Rule[]; settings: { minHours: number; rounding: number; dayStart: string; provider: string };
  sources: Record<string, boolean>; topics: { id: string; topic: string; ticket: string; repo?: string; hits: number; manual?: boolean }[];
  stats: { booked: number; target: number; missingDays: number; missingHours: number; fraction: number };
}

const root = document.getElementById("app")!;
let state: State | undefined;
let showRules = false;
let provider = "";
let noteTimer: any;
const de = (n: number) => n.toLocaleString("de-DE", { maximumFractionDigits: 2 });
const DAYS = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

function color(d: Day) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  if (d.target > 0 && d.booked + 0.01 >= d.target) return "var(--green)";
  if (d.booked > 0) return "var(--amber)";
  if (d.target > 0 && d.date < today.getTime()) return "var(--red)";
  return "var(--muted)";
}

function calendar(s: State) {
  return `<div class="cal">
    <div class="wd">${DAYS.map((d) => `<span class="cap">${d}</span>`).join("")}</div>
    <div class="grid">${s.days.map((d) => {
      const c = color(d);
      const f = d.target > 0 ? Math.min(1, d.booked / d.target) : d.booked > 0 ? 1 : 0;
      const future = d.date > Date.now();
      return `<div class="cell ${d.inMonth ? "" : "out"} ${d.workDay ? "" : "off"} ${d.isToday ? "today" : ""} ${d.date === s.selected ? "sel" : ""}" data-act="select" data-value="${d.date}">
        <div class="row top"><b>${new Date(d.date).getDate()}</b><span class="spacer"></span>
          ${d.hasNote ? `<span class="mk" title="Tagesnotiz">${icon("pencil", 11)}</span>` : ""}${d.hasDraft ? `<span class="mk acc" title="Offene Vorschläge">${icon("list", 11)}</span>` : ""}</div>
        <span class="spacer"></span>
        <span class="hrs" style="color:${s.jira ? c : "var(--muted)"}">${d.booked > 0 ? de(d.booked) + "h" : d.target > 0 && !future ? "0h" : ""}${d.target > 0 ? `<span class="muted"> / ${de(d.target)}</span>` : ""}</span>
        <div class="bar"><i style="width:${f * 100}%;background:${c}"></i></div>
      </div>`;
    }).join("")}</div></div>`;
}

function suggestions(s: State) {
  const e = s.entry;
  const total = e.suggestions.filter((x) => !x.booked).reduce((t, x) => t + x.hours, 0);
  const unassigned = e.suggestions.filter((x) => !x.booked && !x.issueKey).length;
  return `${e.summary ? `<div class="sum">${icon("bot", 14, "acc")}<span>${esc(e.summary)}</span></div>` : ""}
  ${e.suggestions.map((x, i) => `<div class="sg ${x.booked ? "done" : ""} ${s.accountRequired && !x.account && !x.booked ? "noacc" : ""}">
    <div class="row">
      <div class="grow"><div class="row kr">${x.booked ? `<span class="key">${esc(x.issueKey)}</span>` : `<input id="k-${i}" class="key-in ${x.issueKey ? "" : "missing"}" data-i="${i}" data-f="issueKey" value="${esc(x.issueKey)}" placeholder="Ticket?" title="Ticket eintragen/ändern – Copit merkt sich die Zuordnung">`}
        <button class="icon" data-act="issue" data-arg="${esc(x.issueKey)}" title="In Jira öffnen">${icon("external", 11)}</button>
        ${x.topic || x.repo ? `<span class="pill topic" title="Thema / Repo">${esc([x.topic, x.repo].filter(Boolean).join(" · "))}</span>` : ""}</div>
        <div class="muted small trunc">${esc(x.issueSummary)}</div></div>
      ${x.booked ? `<span class="add">${icon("check-circle", 16)}</span>` : `<input id="h-${i}" class="hrs-in" data-i="${i}" data-f="hours" value="${de(x.hours)}"><span class="muted">h</span>
      <button class="icon" data-act="remove" data-arg="${i}" title="Entfernen">${icon("x", 12)}</button>`}
    </div>
    <textarea id="c-${i}" data-i="${i}" data-f="comment" rows="2" ${x.booked ? "disabled" : ""} placeholder="Buchungstext">${esc(x.comment)}</textarea>
    <div class="row"><span class="muted small grow">${esc(x.reason)}${x.confidence ? ` · ${Math.round(x.confidence * 100)} %` : ""}</span>
      ${s.accountRequired ? `<button class="pill ${x.account ? "" : "warn"}" data-act="account" data-arg="${i}">${x.account ? esc(x.account) + (x.accountFromMemory ? " · gemerkt" : "") : "Account fehlt"}</button>` : ""}</div>
    ${x.error ? `<div class="del small">${esc(x.error)}</div>` : ""}
  </div>`).join("")}
  ${e.suggestions.length ? `<div class="row"><button class="ghost" data-act="add">${icon("plus", 12)} Buchung</button><span class="spacer"></span>
    ${unassigned ? `<span class="amb small">${unassigned} ohne Ticket&nbsp;·&nbsp;</span>` : ""}<b>${de(total)} h verteilt</b></div>
    <button class="primary big" data-act="book" ${s.booking || !s.jira || !e.suggestions.some((x) => !x.booked) ? "disabled" : ""}>${s.booking ? `<span class="loader"><i></i><i></i><i></i><i></i></span> Buche…` : `${icon("check", 13)} In Jira buchen`}</button>` :
    `<button class="ghost" data-act="add">${icon("plus", 12)} Manuelle Buchung</button>`}`;
}

function panel(s: State) {
  const d = new Date(s.selected);
  const day = s.days.find((x) => x.date === s.selected);
  const booked = s.worklogs.reduce((t, w) => t + w.seconds, 0) / 3600;
  const target = day?.target ?? 0;
  const missing = Math.max(0, target - booked);
  return `<div class="pan">
    <div class="row"><div class="grow"><div class="h2">${d.toLocaleDateString("de-DE", { weekday: "long", day: "numeric", month: "long" })}</div>
      <div class="muted">${de(booked)} h von ${de(target)} h gebucht</div></div></div>
    ${s.worklogs.length ? `<div class="wls">${s.worklogs.map((w) => `<div class="row wl"><span class="add">${icon("check", 11)}</span><span class="key">${esc(w.issueKey)}</span>
      <span class="grow trunc text2">${esc(w.comment || w.issueSummary)}</span><b class="mono">${de(w.seconds / 3600)}h</b></div>`).join("")}</div>` : ""}
    <label class="field"><span>Tagesnotiz</span><textarea id="note" data-keep rows="3" placeholder="Was war heute? z. B. 1,5 h Sprint-Planning, Kundentermin zu ABC-42…">${esc(s.entry.note)}</textarea></label>
    <div class="row g">
      <label class="field grow"><span>Soll heute</span><input id="target" data-keep value="${de(target)}"></label>
      <label class="field grow"><span>Jetzt buchen (h)</span><input id="hours" data-keep value="${missing ? de(missing) : ""}"></label>
    </div>
    <div class="srcs"><span class="cap">Quellen</span>${[["tickets", "Jira-Tickets"], ["commits", "Git-Commits"], ["gitlab", "GitLab"], ["sessions", "Agent-Sessions"], ["note", "Notiz"]]
      .map(([k, l]) => `<button class="src ${s.sources[k] ? "on" : ""}" data-act="source" data-arg="${k}">${s.sources[k] ? icon("check", 11) : icon("square", 11)} ${l}</button>`).join("")}</div>
    <div class="row"><div class="seg">${["claude", "copilot"].map((p) => `<button class="${(provider || s.settings.provider) === p ? "on" : ""}" data-act="prov" data-value="${p}">${p}</button>`).join("")}</div>
      <span class="spacer"></span><button class="ghost" data-act="heuristic" ${s.working ? "disabled" : ""}>Ohne KI</button></div>
    ${s.working ? `<div class="work"><div class="row"><span class="loader big"><i></i><i></i><i></i><i></i></span><b>Copit recherchiert deinen Tag…</b><span class="spacer"></span><button class="ghost" data-act="cancel">Abbrechen</button></div>
      ${s.steps.map((x, i) => `<div class="row step">${i < s.steps.length - 1 ? icon("check", 11, "add") : `<span class="loader"><i></i><i></i><i></i><i></i></span>`}<span class="text2">${esc(x)}</span></div>`).join("")}</div>`
      : `<button class="primary big" data-act="analyze">${icon("zap", 13)} Analysieren &amp; Buchungen vorschlagen</button>`}
    ${s.error ? `<div class="del small">${esc(s.error)}</div>` : ""}
    ${!s.jira ? `<div class="hint">Jira ist noch nicht verbunden – <span class="acc click" data-act="settings">Customize › Integrationen</span>. Vorschläge gehen trotzdem (Commits, Sessions, Notiz).</div>` : ""}
    ${suggestions(s)}
  </div>`;
}

function rulesDrawer(s: State) {
  return `<div class="overlay"><div class="drawer">
    <div class="row"><span class="h2 grow">Regeln &amp; Soll</span><button class="icon" data-act="rules">${icon("x")}</button></div>
    <div class="row g"><label class="field grow"><span>Mindeststunden / Arbeitstag</span><input id="r-min" value="${de(s.settings.minHours)}"></label>
      <label class="field grow"><span>Rundung (min)</span><input id="r-round" value="${s.settings.rounding}"></label>
      <label class="field grow"><span>Arbeitsbeginn</span><input id="r-start" value="${esc(s.settings.dayStart)}"></label></div>
    <div class="cap">Regeln für die KI</div>
    ${s.rules.map((r, i) => `<div class="rule"><div class="row"><button class="toggle ${r.enabled ? "on" : ""}" data-act="rule.toggle" data-arg="${i}"></button>
      <input id="rn-${i}" value="${esc(r.name)}" class="grow"><button class="icon" data-act="rule.del" data-arg="${i}">${icon("trash", 12)}</button></div>
      <textarea id="rt-${i}" rows="2" placeholder="z. B. Meetings immer auf ORG-1 buchen · max. 6 h pro Ticket">${esc(r.text)}</textarea></div>`).join("")}
    <div class="row"><button class="ghost" data-act="rule.add">${icon("plus", 12)} Regel</button><span class="spacer"></span><button class="primary" data-act="rules.save">Speichern</button></div>
    <div class="cap">Zuordnungen Thema / Repo → Ticket</div>
    <div class="hint">Wird beim Buchen gelernt und wenn du im Vorschlag das Ticket änderst. Feste Zuordnungen (manuell/korrigiert) haben Vorrang – ideal für Weiterentwicklungs-Tickets.</div>
    <div class="topics">${s.topics.map((t) => `<div class="row tp"><span class="grow trunc">${t.repo ? `<span class="pill">${esc(t.repo)}</span> ` : ""}${esc(t.topic)}</span>
      <span class="key">${esc(t.ticket)}</span><span class="muted small">${t.manual ? "fest" : `${t.hits}×`}</span>
      <button class="icon" data-act="topic.del" data-arg="${t.id}">${icon("x", 12)}</button></div>`).join("") || `<div class="muted small">Noch nichts gelernt.</div>`}</div>
    <div class="row g"><input id="t-topic" placeholder="Thema, z. B. Checkout Performance" class="grow"><input id="t-repo" placeholder="Repo (optional)" style="width:130px"><input id="t-ticket" placeholder="HAW-100" style="width:110px">
      <button data-act="topic.add">${icon("plus", 12)}</button></div>
  </div></div>`;
}

function accountDialog(s: State) {
  const a = s.accountDialog!;
  return `<div class="overlay"><div class="dlg">
    <div class="row">${icon("alert", 16, "amb")}<span class="h2">Account fehlt</span></div>
    <p>Das Ticket <span class="key">${esc(a.issueKey)}</span> hat keinen Account hinterlegt. Auf welchen Account soll gebucht werden? Copit merkt sich deine Antwort.</p>
    <div class="muted small">${esc(a.summary)}</div>
    <div class="chips">${a.options.map((o) => `<button data-act="acc.pick" data-value="${esc(o)}">${esc(o)}</button>`).join("")}</div>
    <input id="acc" placeholder="Account-Key eingeben">
    <label class="row"><input type="checkbox" id="accproj" checked style="width:auto"> Für alle Tickets dieses Jira-Projekts merken</label>
    <div class="row"><span class="spacer"></span><button class="ghost" data-act="acc.skip">Abbrechen</button><button class="primary" data-act="acc.ok">Übernehmen &amp; merken</button></div>
  </div></div>`;
}

function render() {
  if (!state) return;
  const s = state;
  const m = new Date(s.month);
  mount(root, `
  <div class="hdr"><span class="logo"></span><div><div class="title">Zeitbuchung</div><div class="sub">${m.toLocaleDateString("de-DE", { month: "long", year: "numeric" })}</div></div>
    <button class="icon" data-act="month" data-value="-1">${icon("chevl")}</button><button data-act="month" data-value="0">Heute</button><button class="icon" data-act="month" data-value="1">${icon("chevr")}</button>
    ${s.loading ? `<span class="loader"><i></i><i></i><i></i><i></i></span>` : ""}
    <span class="spacer"></span>
    ${s.jira ? `<div class="stat"><span class="cap">Monat</span><b>${de(s.stats.booked)} / ${de(s.stats.target)} h</b></div>
    <div class="stat"><span class="cap">Lücken</span><b class="${s.stats.missingDays ? "del" : "add"}">${s.stats.missingDays ? `${s.stats.missingDays} Tage · ${de(s.stats.missingHours)} h` : "keine"}</b></div>`
      : `<div class="stat"><span class="cap">Jira</span><b class="amb click" data-act="settings">nicht verbunden</b></div>`}
    <button class="ghost" data-act="rules">${icon("sliders", 13)} Regeln &amp; Soll</button>
  </div>
  <div class="split"><div class="scroll left">${calendar(s)}</div><div class="scroll right" id="right" data-scroll>${panel(s)}</div></div>
  ${showRules ? rulesDrawer(s) : ""}${s.accountDialog ? accountDialog(s) : ""}`);
}

onState<State>((s) => { state = s; render(); });

const val = (id: string) => (document.getElementById(id) as HTMLInputElement | null)?.value ?? "";
delegate({
  rules: () => { showRules = !showRules; render(); },
  prov: (el) => { provider = el.dataset.value!; render(); },
  analyze: () => send("act", { act: "analyze", value: JSON.stringify({ hours: val("hours"), provider: provider || state?.settings.provider, ai: true }) }),
  heuristic: () => send("act", { act: "analyze", value: JSON.stringify({ hours: val("hours"), provider: provider || state?.settings.provider, ai: false }) }),
  "acc.pick": (el) => { (document.getElementById("acc") as HTMLInputElement).value = el.dataset.value!; },
  "acc.ok": () => send("act", { act: "account.ok", value: JSON.stringify({ account: val("acc").trim(), project: (document.getElementById("accproj") as HTMLInputElement).checked }) }),
  "acc.skip": () => send("act", { act: "account.skip" }),
  "rule.add": () => { state!.rules.push({ id: "r" + Date.now(), name: "Neue Regel", text: "", enabled: true }); render(); },
  "rule.del": (el) => { state!.rules.splice(+el.dataset.arg!, 1); render(); },
  "rule.toggle": (el) => { const r = state!.rules[+el.dataset.arg!]; r.enabled = !r.enabled; render(); },
  "topic.add": () => send("act", { act: "topic.add", value: JSON.stringify({ topic: val("t-topic").trim(), repo: val("t-repo").trim(), ticket: val("t-ticket").trim() }) }),
  "rules.save": () => {
    const rules = state!.rules.map((r, i) => ({ ...r, name: val(`rn-${i}`) || r.name, text: val(`rt-${i}`) }));
    send("act", { act: "rules.save", value: JSON.stringify({ rules, minHours: val("r-min"), rounding: val("r-round"), dayStart: val("r-start") }) });
    showRules = false;
  },
});
document.addEventListener("input", (e) => {
  const t = e.target as HTMLTextAreaElement;
  if (t.id === "note") { clearTimeout(noteTimer); noteTimer = setTimeout(() => send("act", { act: "note", value: t.value }), 600); }
});
document.addEventListener("change", (e) => {
  const t = e.target as HTMLInputElement;
  if (t.id === "target") send("act", { act: "target", value: t.value });
  if (t.dataset.f) send("act", { act: "edit", arg: t.dataset.i, value: JSON.stringify({ field: t.dataset.f, value: t.value }) });
});

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.stat { display: flex; flex-direction: column; gap: 2px; margin-right: 10px; }
.split { flex: 1; display: grid; grid-template-columns: 1fr 470px; min-height: 0; }
.left { padding: 14px 16px; }
.right { border-left: 1px solid var(--line-soft); background: var(--bg2); }
.wd { display: grid; grid-template-columns: repeat(7, 1fr); gap: 4px; padding: 0 2px 6px; }
.grid { display: grid; grid-template-columns: repeat(7, 1fr); gap: 4px; }
.cell { height: 92px; border: 1px solid var(--line-soft); background: var(--card); padding: 7px 9px; display: flex; flex-direction: column; gap: 4px; cursor: pointer; }
.cell:hover { border-color: var(--line); }
.cell.off { background: var(--panel); } .cell.out { opacity: .35; }
.cell.today { border-color: var(--accent); } .cell.sel { border-color: var(--text); background: #221C18; }
.cell .top { gap: 4px; } .mk { color: var(--amber); } .mk.acc { color: var(--accent); }
.hrs { font-family: var(--mono); font-size: 12px; font-weight: 700; }
.pan { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.wls { border: 1px solid var(--line-soft); }
.wl { padding: 5px 8px; gap: 8px; border-bottom: 1px solid var(--line-soft); font-size: 12px; }
.g { gap: 10px; align-items: flex-start; }
button.big { justify-content: center; padding: 9px; }
.work { border: 1px solid var(--line); background: var(--panel); padding: 12px; display: flex; flex-direction: column; gap: 7px; }
.loader.big { width: 18px; height: 18px; }
.step { gap: 8px; font-size: 12px; }
.sum { display: flex; gap: 8px; border: 1px solid #5A3A28; background: #1E1612; padding: 10px; line-height: 1.45; }
.sg { border: 1px solid var(--line-soft); background: var(--card); padding: 10px; display: flex; flex-direction: column; gap: 7px; }
.sg.done { opacity: .6; border-color: #355A37; } .sg.noacc { border-color: #6A5320; }
.hrs-in { width: 64px; text-align: right; font-weight: 700; font-family: var(--mono); }
.pill { cursor: pointer; } .pill.warn { color: var(--amber); border-color: #6A5320; }
.small { font-size: 11.5px; } .add { color: var(--green); } .del { color: var(--red); } .amb { color: var(--amber); } .acc { color: var(--accent); }
.click { cursor: pointer; }
.hint { font-size: 12px; color: var(--text2); border-left: 2px solid var(--accent); padding: 4px 10px; }
.overlay { position: fixed; inset: 0; background: rgba(8,6,5,.8); display: grid; place-items: center; z-index: 20; }
.dlg, .drawer { width: 520px; max-width: 92vw; max-height: 86vh; overflow: auto; background: var(--panel); border: 1px solid var(--line); padding: 20px; display: flex; flex-direction: column; gap: 12px; }
.drawer { width: 620px; }
.chips { display: flex; flex-wrap: wrap; gap: 6px; }
.srcs { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.srcs .cap { margin-right: 4px; }
.src { padding: 3px 8px; font-size: 12px; color: var(--muted); }
.src.on { color: var(--text); border-color: var(--muted); }
.key-in.missing { border-color: var(--amber); }
.key-in { width: 120px; font-family: var(--mono); color: var(--accent); padding: 3px 6px; }
.kr { gap: 6px; }
.pill.topic { text-transform: none; letter-spacing: 0; font-size: 11px; max-width: 170px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sg > .row:first-child > .grow { min-width: 0; } .kr { min-width: 0; flex-wrap: nowrap; }
.topics { border: 1px solid var(--line-soft); max-height: 220px; overflow: auto; }
.tp { padding: 5px 8px; gap: 8px; border-bottom: 1px solid var(--line-soft); }
.rule { border: 1px solid var(--line-soft); padding: 8px; display: flex; flex-direction: column; gap: 6px; }
`;
document.head.appendChild(css);
