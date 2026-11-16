import { delegate, esc, icon, mount, onState, send, usd } from "./ui";

interface Budget { id: string; name: string; type: "cost" | "hours"; amount: number; period: "week" | "month" | "total"; folders: string[]; warnAt: number[]; hardStop: boolean }
interface Status { id: string; used: number; fraction: number; periodLabel: string }
interface Overview {
  week: { cost: number; hours: number }; month: { cost: number; hours: number };
  byProject: { project: string; cost: number; hours: number }[]; byProvider: { provider: string; cost: number; hours: number }[];
  days: { day: string; cost: number; hours: number }[];
}
interface State { budgets: Budget[]; statuses: Status[]; overview: Overview; repos: { path: string; name: string }[]; picked?: string }

const root = document.getElementById("app")!;
let state: State | undefined;
let sel = "overview";
let draft: Budget | undefined;
let dirty = false;

const fmt = (b: Pick<Budget, "type">, v: number) => b.type === "cost" ? usd(v) : `${v.toFixed(1)} h`;
const PERIOD: Record<string, string> = { week: "Pro Woche", month: "Pro Monat", total: "Gesamt" };
const PERIOD_HINT: Record<string, string> = { week: "Jede Kalenderwoche (Mo–So).", month: "Jeder Kalendermonat.", total: "Seit Beginn, ohne Zurücksetzen." };

function statusOf(id: string) { return state?.statuses.find((s) => s.id === id); }

function bar(f: number) {
  return `<div class="bar ${f >= 1 ? "over" : f >= 0.8 ? "warn" : ""}"><i style="width:${Math.min(100, f * 100)}%"></i></div>`;
}

function sidebar(s: State) {
  return `<div class="side">
    <div class="item ${sel === "overview" ? "sel" : ""}" data-act="sel" data-arg="overview">${icon("grid", 14, "muted")}
      <div><div class="nm">Übersicht</div><div class="muted small">${usd(s.overview.month.cost)} diesen Monat</div></div></div>
    ${s.budgets.map((b) => { const st = statusOf(b.id); return `<div class="item ${sel === b.id ? "sel" : ""}" data-act="sel" data-arg="${b.id}">
      ${icon("wallet", 14, "muted")}<div class="grow"><div class="row nm">${esc(b.name)}${st && st.fraction >= 1 ? `<span class="sq" style="background:var(--red)"></span>` : st && st.fraction >= 0.8 ? `<span class="sq" style="background:var(--amber)"></span>` : ""}</div>
      <div class="muted small">${st ? `${fmt(b, st.used)} / ${fmt(b, b.amount)} · ${esc(st.periodLabel)}` : "Nicht gespeichert"}</div>${st ? bar(st.fraction) : ""}</div></div>`; }).join("")}
    ${draft && !s.budgets.some((b) => b.id === draft!.id) ? `<div class="item sel">${icon("plus", 14, "muted")}<div><div class="nm">${esc(draft.name || "Neues Budget")} <span class="sq" style="background:var(--accent)"></span></div><div class="muted small">Noch nicht gespeichert</div></div></div>` : ""}
  </div>`;
}

function overview(s: State) {
  const o = s.overview;
  const max = Math.max(0.01, ...o.days.map((d) => d.cost));
  return `<div class="pane">
    <div class="h1">Budget-Übersicht</div>
    <div class="kpis">
      <div class="kpi"><span class="cap">Diese Woche</span><span class="big">${usd(o.week.cost)}</span><span class="mono muted">${o.week.hours.toFixed(1)} h Agent-Zeit</span></div>
      <div class="kpi"><span class="cap">Dieser Monat</span><span class="big">${usd(o.month.cost)}</span><span class="mono muted">${o.month.hours.toFixed(1)} h Agent-Zeit</span></div>
      <div class="kpi"><span class="cap">Budgets</span><span class="big">${s.budgets.length}</span><span class="mono muted">${s.statuses.filter((x) => x.fraction >= 1).length} überschritten</span></div>
    </div>
    <div class="cap sect">Budgets</div>
    ${s.budgets.map((b) => { const st = statusOf(b.id)!; return `<div class="brow" data-act="sel" data-arg="${b.id}">
      <span class="grow nm">${esc(b.name)}</span><span class="mono muted">${esc(st.periodLabel)}</span>
      <div class="bw">${bar(st.fraction)}</div><span class="mono">${fmt(b, st.used)} / ${fmt(b, b.amount)}</span><span class="mono ${st.fraction >= 1 ? "del" : "muted"}">${Math.round(st.fraction * 100)}%</span></div>`; }).join("") || `<div class="muted">Noch keine Budgets – lege eines pro Projekt an.</div>`}
    <div class="cols2">
      <div><div class="cap sect">Nach Projekt (Monat)</div>
        ${o.byProject.map((p) => `<div class="brow"><span class="grow">${esc(p.project)}</span><span class="mono muted">${p.hours.toFixed(1)} h</span><span class="mono">${usd(p.cost)}</span></div>`).join("") || `<div class="muted">–</div>`}</div>
      <div><div class="cap sect">Nach Anbieter (Monat)</div>
        ${o.byProvider.map((p) => `<div class="brow"><span class="grow">${esc(p.provider)}</span><span class="mono muted">${p.hours.toFixed(1)} h</span><span class="mono">${usd(p.cost)}</span></div>`).join("") || `<div class="muted">–</div>`}</div>
    </div>
    <div class="cap sect">Letzte 14 Tage</div>
    <div class="chart">${o.days.map((d) => `<div class="col" title="${d.day}: ${usd(d.cost)} · ${d.hours.toFixed(1)} h"><div class="b" style="height:${Math.max(2, (d.cost / max) * 100)}%"></div><span class="mono">${d.day.slice(8)}</span></div>`).join("")}</div>
  </div>`;
}

function seg2(opts: [string, string][], cur: string, field: string) {
  return `<div class="seg">${opts.map(([v, l]) => `<button class="${v === cur ? "on" : ""}" data-act="set" data-arg="${field}" data-value="${v}">${l}</button>`).join("")}</div>`;
}

function editor(s: State) {
  const b = draft!;
  const st = statusOf(b.id);
  return `<div class="pane form">
    <div class="row"><div><div class="h2">${s.budgets.some((x) => x.id === b.id) ? esc(b.name) : "Neues Budget"}</div>
      <div class="muted small">${dirty ? "Ungespeicherte Änderungen" : "Gespeichert"} · lokal in Copit</div></div>
      <span class="spacer"></span><button class="ghost" data-act="cancel">Abbrechen</button>
      ${s.budgets.some((x) => x.id === b.id) ? `<button class="ghost" data-act="delete">${icon("trash", 12)}</button>` : ""}
      <button class="primary" data-act="save">${icon("check", 12)} Speichern</button></div>
    ${st ? `<div class="now">${bar(st.fraction)}<span class="mono">${fmt(b, st.used)} von ${fmt(b, b.amount)} · ${esc(st.periodLabel)} · ${Math.round(st.fraction * 100)}%</span></div>` : ""}
    <label class="field"><span>Name</span><input data-f="name" value="${esc(b.name)}" placeholder="z. B. Shop Relaunch"></label>
    <label class="field"><span>Typ</span>${seg2([["cost", "Kosten (USD)"], ["hours", "Agent-Stunden"]], b.type, "type")}
      <span class="hint">${b.type === "cost" ? "API-äquivalente Kosten laut CLI (Tokens × Listenpreis). Bei Abo: was es kosten würde." : "Wanduhr-Laufzeit aller Agent-Läufe."}</span></label>
    <label class="field"><span>Betrag</span><div class="row amt"><span class="mono">${b.type === "cost" ? "$" : "h"}</span><input data-f="amount" value="${b.amount}"><span class="mono muted">${b.type === "cost" ? "USD" : "Stunden"}</span></div></label>
    <label class="field"><span>Zeitraum</span>${seg2([["week", "Pro Woche"], ["month", "Pro Monat"], ["total", "Gesamt"]], b.period, "period")}<span class="hint">${PERIOD_HINT[b.period]}</span></label>
    <label class="field"><span>Projekte / Ordner</span>
      ${b.folders.length ? b.folders.map((f, i) => `<div class="row folder">${icon("folder", 13, "muted")}<span class="mono grow trunc">${esc(f)}</span><button class="icon" data-act="unfolder" data-arg="${i}">${icon("x", 12)}</button></div>`).join("") : `<span class="hint">Noch keine Ordner – dann zählen alle Projekte.</span>`}
      <div class="row"><select id="addrepo" class="grow"><option value="">Projekt hinzufügen, in dem du gearbeitet hast…</option>${s.repos.filter((r) => !b.folders.includes(r.path)).map((r) => `<option value="${esc(r.path)}">${esc(r.name)} · ${esc(r.path)}</option>`).join("")}</select>
        <button data-act="browse">${icon("folder", 13)} Durchsuchen…</button></div>
      <span class="hint">Agent-Läufe in diesen Ordnern (oder darunter) zählen.</span></label>
    <label class="field"><span>Warnen bei</span><div class="row amt"><input data-f="warnAt" value="${esc(b.warnAt.join(", "))}"><span class="mono muted">%</span></div>
      <span class="hint">Prozent des Betrags, durch Komma getrennt. Jede Schwelle meldet sich einmal pro Zeitraum.</span></label>
    <label class="field"><span>Hard Stop</span><div class="row"><button class="toggle ${b.hardStop ? "on" : ""}" data-act="flip"></button><span class="hint">Bei 100 % keine neuen Agent-Läufe in diesen Projekten starten</span></div></label>
  </div>`;
}

function pull() {
  if (!draft) return;
  document.querySelectorAll<HTMLInputElement>("[data-f]").forEach((el) => {
    const f = el.dataset.f!;
    if (f === "name") draft!.name = el.value;
    if (f === "amount") draft!.amount = parseFloat(el.value.replace(",", ".")) || 0;
    if (f === "warnAt") draft!.warnAt = el.value.split(",").map((x) => parseFloat(x)).filter((x) => !isNaN(x));
  });
}

function render() {
  if (!state) return;
  mount(root, `
  <div class="hdr"><span class="logo"></span><span class="title">Budgets</span><button class="icon" data-act="refresh">${icon("refresh", 13)}</button>
    <button data-act="new">${icon("plus", 12)} Neues Budget</button><span class="spacer"></span></div>
  <div class="split">${sidebar(state)}<div class="scroll main" id="main" data-scroll>${sel === "overview" || !draft ? overview(state) : editor(state)}</div></div>`);
}

onState<State>((s) => {
  state = s;
  if (s.picked && draft && !draft.folders.includes(s.picked)) { pull(); draft.folders.push(s.picked); dirty = true; }
  if (draft && !dirty) draft = structuredClone(s.budgets.find((b) => b.id === draft!.id)) ?? draft;
  render();
});

delegate({
  sel: (el) => {
    const id = el.dataset.arg!;
    if (dirty && !confirm("Ungespeicherte Änderungen verwerfen?")) return;
    sel = id; dirty = false;
    draft = id === "overview" ? undefined : structuredClone(state!.budgets.find((b) => b.id === id));
    render();
  },
  new: () => {
    draft = { id: "b" + Date.now().toString(36), name: "", type: "cost", amount: 50, period: "week", folders: [], warnAt: [80, 100], hardStop: false };
    sel = draft.id; dirty = true; render();
  },
  set: (el) => { pull(); (draft as any)[el.dataset.arg!] = el.dataset.value; dirty = true; render(); },
  flip: () => { pull(); draft!.hardStop = !draft!.hardStop; dirty = true; render(); },
  unfolder: (el) => { pull(); draft!.folders.splice(+el.dataset.arg!, 1); dirty = true; render(); },
  browse: () => { pull(); send("act", { act: "browse" }); },
  cancel: () => { dirty = false; sel = "overview"; draft = undefined; render(); },
  save: () => { pull(); if (!draft!.name.trim()) { alert("Name fehlt"); return; } dirty = false; send("act", { act: "save", value: JSON.stringify(draft) }); },
  delete: () => { if (confirm(`Budget "${draft!.name}" löschen?`)) { send("act", { act: "delete", arg: draft!.id }); draft = undefined; sel = "overview"; dirty = false; } },
});
document.addEventListener("change", (e) => {
  const t = e.target as HTMLSelectElement;
  if (t.id === "addrepo" && t.value) { pull(); draft!.folders.push(t.value); dirty = true; render(); }
});
document.addEventListener("input", (e) => { if ((e.target as HTMLElement).dataset.f) dirty = true; });

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.split { flex: 1; display: grid; grid-template-columns: 280px 1fr; min-height: 0; }
.side { border-right: 1px solid var(--line-soft); background: var(--bg2); padding: 8px; overflow: auto; }
.item { display: flex; gap: 10px; align-items: flex-start; padding: 9px 10px; cursor: pointer; border: 1px solid transparent; margin-bottom: 4px; }
.item:hover { background: #1E1916; } .item.sel { background: var(--hover); border-color: var(--line); }
.item .bar { margin-top: 6px; }
.nm { font-weight: 600; gap: 6px; }
.small { font-size: 11px; }
.main { padding: 20px 26px 40px; }
.pane { max-width: 760px; display: flex; flex-direction: column; gap: 14px; }
.kpis { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; }
.kpi { border: 1px solid var(--line-soft); background: var(--panel); padding: 14px; display: flex; flex-direction: column; gap: 6px; }
.big { font-size: 22px; font-weight: 700; font-family: var(--mono); }
.sect { margin-top: 8px; }
.brow { display: flex; align-items: center; gap: 14px; padding: 8px 2px; border-bottom: 1px solid var(--line-soft); cursor: default; }
.brow[data-act] { cursor: pointer; } .brow[data-act]:hover { background: #1B1714; }
.bw { width: 160px; }
.cols2 { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; }
.chart { display: flex; gap: 6px; align-items: flex-end; height: 120px; border-bottom: 1px solid var(--line-soft); padding-bottom: 4px; }
.chart .col { flex: 1; height: 100%; display: flex; flex-direction: column; justify-content: flex-end; align-items: center; gap: 4px; }
.chart .b { width: 100%; background: var(--accent); opacity: .85; }
.chart span { font-size: 9.5px; color: var(--muted); }
.amt { max-width: 260px; } .amt input { width: 140px; }
.folder { border: 1px solid var(--line-soft); padding: 5px 8px; background: var(--bg2); }
.now { display: flex; flex-direction: column; gap: 6px; border: 1px solid var(--line-soft); padding: 10px; background: var(--panel); }
.del { color: var(--red); }
`;
document.head.appendChild(css);
