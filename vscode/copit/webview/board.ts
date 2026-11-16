import { ago, COLORS, delegate, esc, icon, mount, onState, send, usd } from "./ui";

interface Card {
  id: string; title: string; ticket?: string; project: string; branch?: string; status: string; updatedAt: number;
  linesAdded: number; linesRemoved: number; running: boolean; costUsd: number; agentMs: number; pendingQuestion?: string; provider: string;
}
interface State {
  columns: { id: string; title: string; color: string }[];
  agents: Card[];
  projects: string[];
  budget?: { label: string; fraction: number; text: string }[];
}

const root = document.getElementById("app")!;
let state: State | undefined;
let filter = "";
let project = "";
let dragId: string | undefined;

function card(a: Card) {
  return `<div class="card ${a.running ? "run" : ""}" draggable="true" data-id="${a.id}" data-act="open" data-arg="${a.id}">
    <div class="row top">
      <div class="grow ctitle">${a.ticket ? `<span class="key">${esc(a.ticket)}</span> ` : ""}${esc(a.title)}</div>
      ${a.running ? `<span class="loader"><i></i><i></i><i></i><i></i></span>` : ""}
      <button class="icon menu" data-act="menu" data-arg="${a.id}" title="Aktionen">${icon("dots", 13)}</button>
    </div>
    <div class="row meta mono">
      <span>${ago(a.updatedAt)}</span>
      <span class="row g4">${icon("folder", 11)}${esc(a.project.toUpperCase())}</span>
      ${a.branch ? `<span class="row g4 trunc">${icon("branch", 11)}${esc(a.branch.toUpperCase())}</span>` : ""}
    </div>
    ${a.pendingQuestion ? `<div class="q">${icon("question", 12)}<span class="trunc2">${esc(a.pendingQuestion.split("\n")[0])}</span></div>` : ""}
    <div class="row foot">
      ${a.ticket ? `<span class="pill acc">● ${esc(a.ticket)}</span>` : `<span class="pill">${esc(a.provider)}</span>`}
      ${a.linesAdded || a.linesRemoved ? `<span class="mono add">+${a.linesAdded}</span><span class="mono del">-${a.linesRemoved}</span>` : ""}
      <span class="spacer"></span>
      ${a.costUsd ? `<span class="mono muted">${usd(a.costUsd)}</span>` : ""}
    </div>
  </div>`;
}

function render() {
  if (!state) return;
  const f = filter.toLowerCase();
  const list = state.agents.filter((a) =>
    (!project || a.project === project) &&
    (!f || `${a.title} ${a.ticket ?? ""} ${a.branch ?? ""}`.toLowerCase().includes(f)));
  const running = state.agents.filter((a) => a.running).length;
  mount(root, `
  <div class="hdr">
    <span class="title">Board</span>
    <span class="sub">${state.agents.length} Agents · ${running} aktiv</span>
    <div class="spacer"></div>
    ${(state.budget ?? []).map((b) => `<div class="bud" title="${esc(b.text)}"><span class="cap">${esc(b.label)}</span><div class="bar ${b.fraction >= 1 ? "over" : b.fraction >= .8 ? "warn" : ""}"><i style="width:${Math.min(100, b.fraction * 100)}%"></i></div></div>`).join("")}
    <input id="q" data-keep class="search" placeholder="Suchen…" value="${esc(filter)}">
    <select id="proj" data-keep><option value="">Alle Projekte</option>${state.projects.map((p) => `<option ${p === project ? "selected" : ""}>${esc(p)}</option>`).join("")}</select>
    <button data-act="calendar" class="ghost">${icon("calendar")} Activity</button>
    <button class="primary" data-act="new">${icon("plus")} Neuer Agent</button>
  </div>
  <div class="board">
    ${state.columns.map((c) => {
      const cards = list.filter((a) => a.status === c.id);
      return `<div class="col" data-col="${c.id}">
        <div class="colhead"><span class="sq" style="background:${c.color}"></span><span class="cap">${esc(c.title)}</span><span class="cap dim">${cards.length}</span></div>
        <div class="cards">${cards.map(card).join("") || `<div class="drop">—</div>`}</div>
      </div>`;
    }).join("")}
  </div>`);
}

onState<State>((s) => { state = s; render(); });

delegate({
  menu: (el, ev) => { ev.stopPropagation(); send("act", { act: "menu", arg: el.dataset.arg }); },
});

document.addEventListener("input", (e) => {
  const t = e.target as HTMLInputElement;
  if (t.id === "q") { filter = t.value; render(); }
});
document.addEventListener("change", (e) => {
  const t = e.target as HTMLSelectElement;
  if (t.id === "proj") { project = t.value; render(); }
});

// Drag & Drop zwischen Spalten
document.addEventListener("dragstart", (e) => {
  const c = (e.target as HTMLElement).closest<HTMLElement>(".card");
  if (!c) return;
  dragId = c.dataset.id;
  c.classList.add("dragging");
  e.dataTransfer?.setData("text/plain", dragId ?? "");
});
document.addEventListener("dragend", () => { dragId = undefined; document.querySelectorAll(".dragging,.over").forEach((x) => x.classList.remove("dragging", "over")); });
document.addEventListener("dragover", (e) => {
  const col = (e.target as HTMLElement).closest<HTMLElement>(".col");
  if (!col || !dragId) return;
  e.preventDefault();
  document.querySelectorAll(".col.over").forEach((x) => x !== col && x.classList.remove("over"));
  col.classList.add("over");
});
document.addEventListener("drop", (e) => {
  const col = (e.target as HTMLElement).closest<HTMLElement>(".col");
  if (!col || !dragId) return;
  e.preventDefault();
  send("act", { act: "move", arg: dragId, value: col.dataset.col });
  const a = state?.agents.find((x) => x.id === dragId);
  if (a) { a.status = col.dataset.col!; render(); }
});

const css = document.createElement("style");
css.textContent = `
.board { flex: 1; display: flex; gap: 0; overflow-x: auto; overflow-y: hidden; }
.col { flex: 0 0 270px; display: flex; flex-direction: column; border-right: 1px solid var(--line-soft); min-height: 0; }
.col.over { background: #1B1714; }
.colhead { display: flex; align-items: center; gap: 8px; padding: 12px 12px 10px; }
.colhead .dim { margin-left: auto; }
.cards { flex: 1; overflow-y: auto; padding: 0 10px 14px; display: flex; flex-direction: column; gap: 8px; }
.card { background: var(--card); border: 1px solid var(--line-soft); padding: 10px 11px 9px; cursor: pointer; display: flex; flex-direction: column; gap: 7px; }
.card:hover { border-color: var(--line); background: #221C18; }
.card.run { border-left: 2px solid var(--accent); }
.card.dragging { opacity: .4; }
.card .ctitle { font-size: 12.5px; line-height: 1.35; }
.card .top { align-items: flex-start; }
.card .menu { opacity: 0; margin: -3px -4px 0 0; }
.card:hover .menu { opacity: 1; }
.meta { gap: 10px; color: var(--muted); font-size: 10px; letter-spacing: .8px; }
.g4 { gap: 4px; }
.foot { gap: 8px; }
.q { display: flex; gap: 6px; color: var(--accent); font-size: 11.5px; }
.trunc2 { display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.drop { color: var(--line); text-align: center; padding: 10px; font-family: var(--mono); }
.search { width: 200px; }
#proj { width: 160px; }
.bud { width: 110px; display: flex; flex-direction: column; gap: 4px; }
`;
document.head.appendChild(css);
