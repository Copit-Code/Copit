import { delegate, esc, icon, mount, onState, send } from "./ui";

interface State { repos: { path: string; name: string }[]; provider: string; model: string; prefill?: { title?: string; prompt?: string; ticket?: string }; jira: boolean; error?: string }

const root = document.getElementById("app")!;
let state: State | undefined;
const f = { title: "", ticket: "", prompt: "", repo: "", branch: "", provider: "claude", mode: "plan", model: "", start: true };

const MODES = [
  ["ask", "Fragen", "Nur lesen und antworten"],
  ["plan", "Plan", "Erst Plan, keine Änderungen"],
  ["edit", "Edit", "Dateien ändern, Shell nach Regeln"],
  ["auto", "Auto", "Volle Rechte (Deny-Regeln gelten weiter)"],
];

function pull() {
  for (const k of ["title", "ticket", "prompt", "branch", "model"] as const) {
    const el = document.getElementById(k) as HTMLInputElement | null;
    if (el) f[k] = el.value;
  }
  const r = document.getElementById("repo") as HTMLSelectElement | null;
  if (r) f.repo = r.value;
}

function render() {
  if (!state) return;
  mount(root, `
  <div class="hdr"><span class="logo"></span><div><div class="title">Neuer Agent</div><div class="sub">Landet in der Inbox oder startet sofort</div></div></div>
  <div class="scroll"><div class="wrap form">
    <div class="row g">
      <label class="field grow"><span>Ticket</span><div class="row"><input id="ticket" value="${esc(f.ticket)}" placeholder="HAW-178"><button data-act="jira" ${state.jira ? "" : "disabled"} title="Titel und Beschreibung aus Jira übernehmen">${icon("ticket", 13)} Aus Jira</button></div></label>
      <label class="field grow2"><span>Titel</span><input id="title" value="${esc(f.title)}" placeholder="Kurzer Titel (sonst erste Zeile des Auftrags)"></label>
    </div>
    <label class="field"><span>Auftrag</span><textarea id="prompt" rows="9" placeholder="Was soll der Agent tun? Kontext, Akzeptanzkriterien, Hinweise…">${esc(f.prompt)}</textarea></label>
    <div class="row g">
      <label class="field grow"><span>Projekt</span><select id="repo">${state.repos.map((r) => `<option value="${esc(r.path)}" ${r.path === f.repo ? "selected" : ""}>${esc(r.name)}</option>`).join("")}</select></label>
      <label class="field grow"><span>Branch (optional)</span><input id="branch" value="${esc(f.branch)}" placeholder="${esc(f.ticket ? `feature/${f.ticket}` : "feature/…")}"></label>
    </div>
    <label class="field"><span>Anbieter</span><div class="seg">${["claude", "copilot"].map((p) => `<button class="${f.provider === p ? "on" : ""}" data-act="prov" data-value="${p}">${p}</button>`).join("")}</div></label>
    <label class="field"><span>Modus</span><div class="modes">${MODES.map(([v, l, h]) => `<button class="mode ${f.mode === v ? "on" : ""}" data-act="mode" data-value="${v}"><b>${l}</b><span class="muted">${h}</span></button>`).join("")}</div></label>
    <label class="field"><span>Modell</span><input id="model" value="${esc(f.model)}" placeholder="Standard"></label>
    <div class="row"><button class="toggle ${f.start ? "on" : ""}" data-act="start"></button><span>Sofort starten</span><span class="spacer"></span>
      ${state.error ? `<span class="del">${esc(state.error)}</span>` : ""}
      <button class="primary" data-act="create">${icon("plus", 12)} Agent anlegen</button></div>
  </div></div>`);
}

onState<State>((s) => {
  const first = !state;
  state = s;
  if (first) { f.repo = s.repos[0]?.path ?? ""; f.provider = s.provider; f.model = s.model; }
  if (s.prefill) { f.title = s.prefill.title ?? f.title; f.prompt = s.prefill.prompt ?? f.prompt; f.ticket = s.prefill.ticket ?? f.ticket; s.prefill = undefined; }
  render();
});

delegate({
  prov: (el) => { pull(); f.provider = el.dataset.value!; render(); },
  mode: (el) => { pull(); f.mode = el.dataset.value!; render(); },
  start: () => { pull(); f.start = !f.start; render(); },
  jira: () => { pull(); if (f.ticket) send("act", { act: "jira", value: f.ticket }); },
  create: () => {
    pull();
    if (!f.prompt.trim()) { state!.error = "Auftrag fehlt"; render(); return; }
    send("act", { act: "create", value: JSON.stringify(f) });
  },
});

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.wrap { max-width: 760px; margin: 0 auto; padding: 20px; }
.g { gap: 14px; align-items: flex-start; } .grow2 { flex: 2; }
.modes { display: grid; grid-template-columns: repeat(4, 1fr); gap: 6px; }
.mode { flex-direction: column; align-items: flex-start; gap: 3px; padding: 8px 10px; text-align: left; }
.mode.on { border-color: var(--accent); background: var(--accent-soft); }
.mode span { font-size: 11px; }
.del { color: var(--red); }
`;
document.head.appendChild(css);
