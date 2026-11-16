import { delegate, esc, icon, mount, onState, send } from "./ui";

interface Param { key: string; label: string; placeholder?: string; multiline?: boolean }
interface Def { type: string; label: string; group: string; params?: Param[] }
interface Cond { field: string; op: string; value: string }
interface Act { type: string; params: Record<string, string> }
interface Auto { id: string; name: string; enabled: boolean; trigger: { type: string; params: Record<string, string> }; conditions: Cond[]; actions: Act[]; lastRun?: number; lastResult?: string; runs?: number }
interface State { automations: Auto[]; triggers: Def[]; actions: Def[]; log: { automation_id: string; ts: number; ok: number; message: string }[]; vars: string[] }

const root = document.getElementById("app")!;
let state: State | undefined;
let selected: string | undefined;
let draft: Auto | undefined;
let dirty = false;

function trig(t: string) { return state?.triggers.find((x) => x.type === t); }
function actDef(t: string) { return state?.actions.find((x) => x.type === t); }

function listItem(a: Auto) {
  const ok = a.lastResult && !a.lastResult.startsWith("Fehler");
  return `<div class="item ${a.id === selected ? "sel" : ""}" data-act="select" data-arg="${a.id}">
    <span class="st">${a.lastRun ? (ok ? icon("check-circle", 15, "add") : icon("x-circle", 15, "del")) : icon("circle", 15, "muted")}</span>
    <div class="grow"><div class="nm ${a.enabled ? "" : "dim"}">${esc(a.name)}</div><div class="cap">${esc(trig(a.trigger.type)?.label ?? a.trigger.type)}</div></div>
    <button class="toggle ${a.enabled ? "on" : ""}" data-act="toggle" data-arg="${a.id}"></button>
  </div>`;
}

function select(id: string, options: { value: string; label: string; group?: string }[], value: string, attrs = "") {
  const groups = [...new Set(options.map((o) => o.group ?? ""))];
  const opts = groups.map((g) => {
    const os = options.filter((o) => (o.group ?? "") === g).map((o) => `<option value="${esc(o.value)}" ${o.value === value ? "selected" : ""}>${esc(o.label)}</option>`).join("");
    return g ? `<optgroup label="${esc(g)}">${os}</optgroup>` : os;
  }).join("");
  return `<select data-f="${id}" ${attrs}>${opts}</select>`;
}

function paramInputs(prefix: string, defs: Param[] = [], values: Record<string, string>) {
  return defs.map((p) => `<label class="field"><span>${esc(p.label)}</span>${p.multiline
    ? `<textarea data-f="${prefix}.${p.key}" placeholder="${esc(p.placeholder ?? "")}">${esc(values[p.key] ?? "")}</textarea>`
    : `<input data-f="${prefix}.${p.key}" value="${esc(values[p.key] ?? "")}" placeholder="${esc(p.placeholder ?? "")}">`}</label>`).join("");
}

function editor(s: State) {
  if (!draft) return `<div class="empty">Wähle links eine Automation oder lege eine neue an.<br><br>
    Beispiele: <i>Agent fertig → Jira-Kommentar + Status „In Review“</i>, <i>Pipeline rot → Fix-Agent starten</i>, <i>Ticket „In Arbeit“ → Agent mit Plan anlegen</i>.</div>`;
  const d = draft;
  const fields = ["status", "ticket", "branch", "project", "title", "provider", "summary", "from", "to"];
  return `
  <div class="ed">
    <div class="row"><input data-f="name" class="name" value="${esc(d.name)}" placeholder="Name der Automation">
      <span class="spacer"></span>${dirty ? `<span class="cap acc">Ungespeichert</span>` : ""}
      <button class="ghost" data-act="test">${icon("play", 11)} Testen</button>
      <button class="ghost" data-act="delete">${icon("trash", 12)}</button>
      <button class="primary" data-act="save">${icon("check", 12)} Speichern</button></div>

    <div class="step"><div class="lbl"><span class="sq" style="background:var(--accent)"></span><span class="cap">Wenn</span></div>
      <div class="body2">
        ${select("trigger", s.triggers.map((t) => ({ value: t.type, label: t.label, group: t.group })), d.trigger.type)}
        ${paramInputs("tp", trig(d.trigger.type)?.params, d.trigger.params)}
      </div></div>

    <div class="step"><div class="lbl"><span class="sq" style="background:var(--amber)"></span><span class="cap">Nur wenn</span></div>
      <div class="body2">
        ${d.conditions.map((c, i) => `<div class="row cond">
          ${select(`c${i}.field`, fields.map((f) => ({ value: f, label: f })), c.field)}
          ${select(`c${i}.op`, [{ value: "equals", label: "ist" }, { value: "not_equals", label: "ist nicht" }, { value: "contains", label: "enthält" }, { value: "matches", label: "Regex" }], c.op)}
          <input data-f="c${i}.value" value="${esc(c.value)}" placeholder="Wert">
          <button class="icon" data-act="cond.del" data-arg="${i}">${icon("x", 12)}</button></div>`).join("")}
        <button class="ghost add" data-act="cond.add">${icon("plus", 11)} Bedingung</button>
      </div></div>

    <div class="step"><div class="lbl"><span class="sq" style="background:var(--green)"></span><span class="cap">Dann</span></div>
      <div class="body2">
        ${d.actions.map((a, i) => `<div class="action">
          <div class="row"><span class="mono muted">${i + 1}</span>
            ${select(`a${i}.type`, s.actions.map((x) => ({ value: x.type, label: x.label, group: x.group })), a.type)}
            <span class="spacer"></span>
            <button class="icon" data-act="act.up" data-arg="${i}">${icon("up", 12)}</button>
            <button class="icon" data-act="act.down" data-arg="${i}">${icon("down", 12)}</button>
            <button class="icon" data-act="act.del" data-arg="${i}">${icon("x", 12)}</button></div>
          <div class="params">${paramInputs(`a${i}.p`, actDef(a.type)?.params, a.params)}</div>
        </div>`).join("")}
        <button class="ghost add" data-act="act.add">${icon("plus", 11)} Schritt</button>
      </div></div>

    <div class="vars"><span class="cap">Variablen</span> ${s.vars.map((v) => `<code>{{${esc(v)}}}</code>`).join(" ")}</div>

    <div class="cap logh">Letzte Läufe</div>
    ${s.log.filter((l) => l.automation_id === d.id).slice(0, 12).map((l) => `<div class="row logr">${l.ok ? icon("check", 12, "add") : icon("x", 12, "del")}
      <span class="mono muted">${new Date(l.ts).toLocaleString("de-DE")}</span><span class="grow trunc text2">${esc(l.message)}</span></div>`).join("") || `<div class="muted">Noch nicht ausgeführt.</div>`}
  </div>`;
}

function render() {
  if (!state) return;
  mount(root, `
  <div class="hdr"><span class="logo"></span><div><div class="title">Automations</div><div class="sub">Schritte, die bei Agent-, Git-, GitLab- und Jira-Ereignissen laufen</div></div>
    <span class="spacer"></span><button class="primary" data-act="new">${icon("plus", 12)} Neue Automation</button></div>
  <div class="split">
    <div class="list scroll" id="list" data-scroll>${state.automations.map(listItem).join("")}</div>
    <div class="right scroll" id="right" data-scroll>${editor(state)}</div>
  </div>`);
}

/** Werte aus dem Formular in den Entwurf übernehmen. */
function pull() {
  if (!draft) return;
  document.querySelectorAll<HTMLInputElement>("[data-f]").forEach((el) => {
    const f = el.dataset.f!, v = el.value;
    if (f === "name") draft!.name = v;
    else if (f === "trigger") draft!.trigger.type = v;
    else if (f.startsWith("tp.")) draft!.trigger.params[f.slice(3)] = v;
    else if (/^c\d+\./.test(f)) { const [i, k] = f.slice(1).split("."); (draft!.conditions[+i] as any)[k] = v; }
    else if (/^a\d+\.type$/.test(f)) { const i = +f.slice(1, f.indexOf(".")); if (draft!.actions[i].type !== v) { draft!.actions[i].type = v; draft!.actions[i].params = {}; } }
    else if (/^a\d+\.p\./.test(f)) { const i = +f.slice(1, f.indexOf(".")); draft!.actions[i].params[f.split(".")[2]] = v; }
  });
}

window.addEventListener("copit:select", (e) => {
  const id = (e as CustomEvent).detail.id as string;
  const a = state?.automations.find((x) => x.id === id);
  if (a) { selected = id; draft = structuredClone(a); dirty = false; render(); } else selected = id;
});

onState<State>((s) => {
  state = s;
  if (selected && !dirty) draft = structuredClone(s.automations.find((a) => a.id === selected));
  render();
});

delegate({
  select: (el, ev) => {
    if ((ev.target as HTMLElement).closest(".toggle")) return;
    if (dirty && !confirm("Ungespeicherte Änderungen verwerfen?")) return;
    selected = el.dataset.arg; draft = structuredClone(state!.automations.find((a) => a.id === selected)); dirty = false; render();
  },
  toggle: (el, ev) => { ev.stopPropagation(); send("act", { act: "toggle", arg: el.dataset.arg }); },
  new: () => {
    selected = "new-" + Date.now();
    draft = { id: selected, name: "Neue Automation", enabled: true, trigger: { type: "agent.finished", params: {} }, conditions: [], actions: [{ type: "notify", params: { message: "{{title}}: {{status}}" } }] };
    dirty = true; render();
  },
  "cond.add": () => { pull(); draft!.conditions.push({ field: "ticket", op: "contains", value: "" }); dirty = true; render(); },
  "cond.del": (el) => { pull(); draft!.conditions.splice(+el.dataset.arg!, 1); dirty = true; render(); },
  "act.add": () => { pull(); draft!.actions.push({ type: "notify", params: {} }); dirty = true; render(); },
  "act.del": (el) => { pull(); draft!.actions.splice(+el.dataset.arg!, 1); dirty = true; render(); },
  "act.up": (el) => { pull(); const i = +el.dataset.arg!; if (i > 0) [draft!.actions[i - 1], draft!.actions[i]] = [draft!.actions[i], draft!.actions[i - 1]]; dirty = true; render(); },
  "act.down": (el) => { pull(); const i = +el.dataset.arg!; if (i < draft!.actions.length - 1) [draft!.actions[i + 1], draft!.actions[i]] = [draft!.actions[i], draft!.actions[i + 1]]; dirty = true; render(); },
  save: () => { pull(); if (draft!.id.startsWith("new-")) draft!.id = draft!.id.slice(4); selected = draft!.id; dirty = false; send("act", { act: "save", value: JSON.stringify(draft) }); },
  test: () => { pull(); send("act", { act: "test", value: JSON.stringify(draft) }); },
  delete: () => { if (draft && confirm(`"${draft.name}" löschen?`)) { send("act", { act: "delete", arg: draft.id }); draft = undefined; selected = undefined; dirty = false; render(); } },
});
document.addEventListener("change", (e) => {
  const t = e.target as HTMLElement;
  if (t.dataset.f) { pull(); dirty = true; if (t.tagName === "SELECT") render(); }
});
document.addEventListener("input", (e) => { if ((e.target as HTMLElement).dataset.f) dirty = true; });

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.split { flex: 1; display: grid; grid-template-columns: 360px 1fr; min-height: 0; }
.list { border-right: 1px solid var(--line-soft); }
.item { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid var(--line-soft); cursor: pointer; }
.item:hover { background: #1B1714; } .item.sel { background: var(--hover); box-shadow: inset 2px 0 0 var(--accent); }
.item .nm { font-weight: 600; font-size: 13px; } .item .nm.dim { color: var(--muted); }
.item .cap { margin-top: 3px; }
.st { width: 18px; display: grid; place-items: center; }
.add { color: var(--green); } .del { color: var(--red); } .acc { color: var(--accent); }
.right { padding: 18px 22px 40px; }
.ed { display: flex; flex-direction: column; gap: 16px; max-width: 820px; }
.name { font-size: 15px; font-weight: 700; max-width: 420px; }
.step { display: grid; grid-template-columns: 110px 1fr; gap: 14px; border-top: 1px solid var(--line-soft); padding-top: 14px; }
.lbl { display: flex; align-items: center; gap: 8px; align-self: start; padding-top: 7px; }
.body2 { display: flex; flex-direction: column; gap: 10px; }
.cond select { width: 150px; }
.action { border: 1px solid var(--line-soft); background: var(--panel); padding: 10px; display: flex; flex-direction: column; gap: 10px; }
.action select { width: 260px; }
.params { display: grid; gap: 10px; }
button.add { align-self: flex-start; }
.vars { border-top: 1px solid var(--line-soft); padding-top: 12px; line-height: 2; }
.logh { margin-top: 6px; }
.logr { padding: 3px 0; border-bottom: 1px solid var(--line-soft); }
`;
document.head.appendChild(css);
