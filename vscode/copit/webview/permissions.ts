import { delegate, esc, icon, mount, onState, seg, send } from "./ui";

interface Rule { id: string; pattern: string; level: "deny" | "ask" | "allow"; source: "global" | "project" | "copit"; repo?: string; repoName?: string; enabled: boolean; title: string; tool: string }
interface Op { id: string; label: string; group: string; patterns: string[]; risky?: boolean }
interface State {
  rules: Rule[];
  repos: { path: string; name: string }[];
  ops: Op[];
  matrix: Record<string, Record<string, "deny" | "ask" | "allow">>;
  settings: Record<string, any>;
  secrets: Record<string, boolean>;
  tests: Record<string, string>;
  version: string;
}

const root = document.getElementById("app")!;
let state: State | undefined;
let tab = "permissions";
let query = "";
let source = "";
let adding = false;
const collapsed = new Set<string>();
const LEVELS = [{ value: "deny", label: "Deny" }, { value: "ask", label: "Ask" }, { value: "allow", label: "Allow" }];

function ruleRow(r: Rule) {
  const ic = r.level === "deny" ? icon("ban", 15, "del") : r.level === "ask" ? icon("question", 15, "amb") : icon("check-circle", 15, "add");
  const badge = r.source === "global" ? "GLOBAL" : r.source === "project" ? (r.repoName ?? "PROJEKT").toUpperCase() : r.repo ? `COPIT · ${(r.repoName ?? "").toUpperCase()}` : "COPIT";
  return `<div class="rule ${r.enabled ? "" : "off"}">
    <span class="ricon">${ic}</span>
    <div class="grow">
      <div class="row"><b class="mono rt">${esc(r.title)}</b><span class="pill">${esc(badge)}</span></div>
      <div class="mono muted small">${esc(r.tool)} · ${esc(r.pattern)}</div>
    </div>
    <div class="seg deny">${LEVELS.map((l) => `<button class="${l.value === r.level ? "on" : ""}" data-act="rule.level" data-arg="${esc(r.id)}" data-value="${l.value}">${l.label}</button>`).join("")}</div>
    <button class="toggle ${r.enabled ? "on" : ""}" data-act="rule.toggle" data-arg="${esc(r.id)}" title="Regel pausieren"></button>
    <button class="icon" data-act="rule.delete" data-arg="${esc(r.id)}" title="Löschen">${icon("trash", 13)}</button>
  </div>`;
}

function permissions(s: State) {
  const q = query.toLowerCase();
  const rules = s.rules.filter((r) => (!source || r.source === source) && (!q || `${r.pattern} ${r.title}`.toLowerCase().includes(q)));
  const groups: [string, string, Rule[]][] = [
    ["deny", "Blocked", rules.filter((r) => r.level === "deny")],
    ["ask", "Ask", rules.filter((r) => r.level === "ask")],
    ["allow", "Allowed", rules.filter((r) => r.level === "allow")],
  ];
  return `
  <p class="text2">Permissions legen fest, welche Tools Agents ohne Rückfrage nutzen dürfen, nur nach Rückfrage, oder nie.</p>
  <p class="muted small">Claude prüft diese Regeln vor jedem Tool-Aufruf: Deny schlägt Ask schlägt Allow. GLOBAL-Regeln (~/.claude/settings.json) gelten in jedem Projekt und auch im Terminal,
  Projekt-Regeln stehen in &lt;repo&gt;/.claude/settings.json, COPIT-Regeln gibt Copit beim Start mit. Der Schalter pausiert eine Regel, ohne sie zu löschen.</p>
  <div class="row sect"><span class="cap">Permissions ${rules.length}</span><span class="spacer"></span>
    <button data-act="add">${icon("plus", 12)} Regel hinzufügen</button></div>
  ${adding ? `<div class="addform row">
      <input id="np" data-keep placeholder="Bash(git push:*)  ·  Edit  ·  WebFetch" class="grow">
      <select id="nl" data-keep>${LEVELS.map((l) => `<option value="${l.value}">${l.label}</option>`).join("")}</select>
      <select id="ns" data-keep><option value="copit|">Copit (alle Projekte)</option><option value="global|">Global (~/.claude)</option>
        ${s.repos.map((r) => `<option value="project|${esc(r.path)}">Projekt-Datei: ${esc(r.name)}</option><option value="copit|${esc(r.path)}">Copit: ${esc(r.name)}</option>`).join("")}</select>
      <button class="primary" data-act="add.save">Hinzufügen</button><button class="ghost" data-act="add">Abbrechen</button>
    </div>` : ""}
  ${groups.map(([k, label, list]) => `
    <div class="grp row click" data-act="collapse" data-arg="${k}">${icon(collapsed.has(k) ? "chevr" : "chevd", 11)}<span class="cap">${label} ${list.length}</span></div>
    ${collapsed.has(k) ? "" : list.map(ruleRow).join("") || `<div class="muted small pad">Keine Regeln</div>`}`).join("")}`;
}

function matrix(s: State) {
  const scopes = [{ key: "", name: "Standard" }, ...s.repos.map((r) => ({ key: r.path, name: r.name }))];
  const groups = [...new Set(s.ops.map((o) => o.group))];
  return `
  <p class="text2">Git-Matrix: Was darf ein Agent pro Projekt? Ein Klick setzt die passenden Copit-Regeln (z. B. <code>Bash(git push:*)</code>).
  „Ask“ heißt: der Agent stoppt und fragt dich auf dem Board (Spalte Need Input).</p>
  <div class="mtx scroll"><table>
    <thead><tr><th></th>${scopes.map((sc) => `<th class="cap">${esc(sc.name)}</th>`).join("")}</tr></thead>
    <tbody>${groups.map((g) => `<tr class="gh"><td colspan="${scopes.length + 1}" class="cap">${esc(g)}</td></tr>` +
      s.ops.filter((o) => o.group === g).map((o) => `<tr><td><div class="row">${o.risky ? icon("alert", 12, "amb") : ""}<span>${esc(o.label)}</span></div>
        <div class="mono muted small">${esc(o.patterns.slice(0, 2).join(", "))}${o.patterns.length > 2 ? " …" : ""}</div></td>
        ${scopes.map((sc) => `<td>${seg(LEVELS, s.matrix[o.id]?.[sc.key] ?? "ask", "matrix.set", `${o.id}|${sc.key}`).replace('class="seg"', 'class="seg deny"')}</td>`).join("")}</tr>`).join("")).join("")}
    </tbody></table></div>`;
}

function field(id: string, label: string, value: unknown, placeholder = "", hint = "") {
  return `<label class="field"><span>${label}</span><input id="${id}" data-keep value="${esc(value ?? "")}" placeholder="${esc(placeholder)}">${hint ? `<span class="hint">${hint}</span>` : ""}</label>`;
}

function secretRow(key: string, label: string, s: State) {
  return `<div class="row secret"><span class="cap grow">${label}</span><span class="mono ${s.secrets[key] ? "add" : "muted"}">${s.secrets[key] ? "hinterlegt" : "fehlt"}</span>
    <button data-act="secret" data-arg="${key}">${s.secrets[key] ? "Ändern" : "Hinterlegen"}</button></div>`;
}

function integrations(s: State) {
  const st = s.settings;
  return `<div class="cols">
    <div class="form card2">
      <div class="row">${icon("ticket", 16, "acc")}<span class="h2">Jira</span></div>
      ${field("jira.url", "URL", st["jira.url"], "https://jira.firma.local")}
      <label class="field"><span>Authentifizierung</span>${seg([{ value: "bearer", label: "Bearer (PAT)" }, { value: "basic", label: "Basic" }], st["jira.authMode"], "set", "jira.authMode")}</label>
      ${st["jira.authMode"] === "basic" ? field("jira.user", "Benutzer", st["jira.user"]) : ""}
      ${secretRow("jira", "Personal Access Token", s)}
      <div class="row"><button data-act="test" data-arg="jira">Verbindung testen</button><span class="mono small">${esc(s.tests.jira ?? "")}</span></div>
    </div>
    <div class="form card2">
      <div class="row">${icon("branch", 16, "acc")}<span class="h2">GitLab</span></div>
      ${field("gitlab.url", "URL", st["gitlab.url"], "https://gitlab.firma.local")}
      ${secretRow("gitlab", "Private Access Token (Scope api)", s)}
      <div class="row"><button data-act="test" data-arg="gitlab">Verbindung testen</button><span class="mono small">${esc(s.tests.gitlab ?? "")}</span></div>
      <label class="field"><span>Interne Zertifikate</span><div class="row"><button class="toggle ${st.ignoreCertificateErrors ? "on" : ""}" data-act="flip" data-arg="ignoreCertificateErrors"></button><span class="hint">Zertifikatsfehler ignorieren</span></div></label>
    </div>
    <div class="form card2">
      <div class="row">${icon("refresh", 16, "acc")}<span class="h2">Updates (GitHub)</span></div>
      ${field("update.repo", "Repository", st["update.repo"], "owner/copit", "Privates Repo mit Releases (.vsix)")}
      ${secretRow("github", "GitHub-Token (leer = gh CLI)", s)}
      <label class="field"><span>Automatisch</span><div class="row"><button class="toggle ${st["update.auto"] ? "on" : ""}" data-act="flip" data-arg="update.auto"></button><span class="hint">Beim Start und stündlich prüfen</span></div></label>
      <div class="row"><button data-act="update">Jetzt prüfen</button><span class="mono small">v${esc(s.version)} ${esc(s.tests.update ?? "")}</span></div>
    </div>
  </div>
  <div class="row savebar"><span class="spacer"></span><button class="primary" data-act="save">Speichern</button></div>`;
}

function agents(s: State) {
  const st = s.settings;
  return `<div class="cols">
    <div class="form card2">
      <div class="row">${icon("bot", 16, "acc")}<span class="h2">Claude CLI</span></div>
      ${field("claudePath", "Pfad (leer = automatisch)", st.claudePath, "", esc(s.tests.claude ?? ""))}
      ${field("claudeModel", "Modell", st.claudeModel, "opus · sonnet · haiku")}
      <div class="row"><button data-act="test" data-arg="claude">Testen</button></div>
    </div>
    <div class="form card2">
      <div class="row">${icon("bot", 16, "acc")}<span class="h2">Copilot CLI</span></div>
      ${field("copilotPath", "Pfad (leer = automatisch)", st.copilotPath, "", esc(s.tests.copilot ?? ""))}
      <div class="row"><button data-act="test" data-arg="copilot">Testen</button></div>
    </div>
    <div class="form card2 wide">
      <label class="field"><span>Standard-Anbieter</span>${seg([{ value: "claude", label: "Claude" }, { value: "copilot", label: "Copilot" }], st.defaultProvider, "set", "defaultProvider")}</label>
      <label class="field"><span>Globale Anweisungen</span><textarea id="globalInstructions" data-keep>${esc(st.globalInstructions)}</textarea></label>
      <label class="field"><span>Worktrees</span><div class="row"><button class="toggle ${st.useWorktrees ? "on" : ""}" data-act="flip" data-arg="useWorktrees"></button><span class="hint">Agents mit eigenem Branch laufen parallel in eigenen Git-Worktrees</span></div></label>
      ${field("pollSeconds", "Abfrageintervall Jira/GitLab (s)", st.pollSeconds)}
    </div>
  </div>
  <div class="row savebar"><span class="spacer"></span><button class="primary" data-act="save">Speichern</button></div>`;
}

function render() {
  if (!state) return;
  const s = state;
  const tabs = [
    ["permissions", "shield", `Permissions ${s.rules.length}`],
    ["matrix", "grid", "Git-Matrix"],
    ["integrations", "link", "Integrationen"],
    ["agents", "bot", "Agents"],
  ];
  mount(root, `
  <div class="hdr"><span class="logo"></span><div><div class="title">Customize</div><div class="sub">Was deine Agents nutzen und tun dürfen</div></div>
    <span class="spacer"></span><button class="ghost" data-act="refresh">${icon("refresh")}</button></div>
  <div class="scroll body" id="body" data-scroll>
    <div class="wrap">
      <div class="row">
        <div class="search grow row">${icon("search", 13, "muted")}<input id="q" data-keep placeholder="Regeln, Muster, Tools durchsuchen…" value="${esc(query)}"></div>
        <select id="src" data-keep><option value="">Quelle: Alle</option><option value="global" ${source === "global" ? "selected" : ""}>Global</option><option value="project" ${source === "project" ? "selected" : ""}>Projekt</option><option value="copit" ${source === "copit" ? "selected" : ""}>Copit</option></select>
      </div>
      <div class="tabs">${tabs.map(([k, ic, l]) => `<button class="tab ${tab === k ? "on" : ""}" data-act="tab" data-value="${k}">${icon(ic, 12)} ${l}</button>`).join("")}</div>
      ${tab === "permissions" ? permissions(s) : tab === "matrix" ? matrix(s) : tab === "integrations" ? integrations(s) : agents(s)}
    </div>
  </div>`);
}

function collectSettings() {
  const out: Record<string, unknown> = {};
  document.querySelectorAll<HTMLInputElement>("input[id],textarea[id]").forEach((el) => {
    if (["q", "np"].includes(el.id)) return;
    out[el.id] = el.id === "pollSeconds" ? Number(el.value) || 60 : el.value;
  });
  return out;
}

onState<State>((s) => { state = s; render(); });
delegate({
  tab: (el) => { tab = el.dataset.value!; render(); },
  add: () => { adding = !adding; render(); },
  collapse: (el) => { const k = el.dataset.arg!; collapsed.has(k) ? collapsed.delete(k) : collapsed.add(k); render(); },
  "add.save": () => {
    const p = (document.getElementById("np") as HTMLInputElement).value.trim();
    if (!p) return;
    const [src, repo] = (document.getElementById("ns") as HTMLSelectElement).value.split("|");
    send("act", { act: "rule.add", value: JSON.stringify({ pattern: p, level: (document.getElementById("nl") as HTMLSelectElement).value, source: src, repo: repo || undefined }) });
    adding = false;
  },
  set: (el) => { if (state) { state.settings[el.dataset.arg!] = el.dataset.value; render(); } },
  flip: (el) => { if (state) { state.settings[el.dataset.arg!] = !state.settings[el.dataset.arg!]; render(); } },
  save: () => send("act", { act: "save", value: JSON.stringify({ ...state?.settings, ...collectSettings() }) }),
});
document.addEventListener("input", (e) => { const t = e.target as HTMLInputElement; if (t.id === "q") { query = t.value; render(); } });
document.addEventListener("change", (e) => { const t = e.target as HTMLSelectElement; if (t.id === "src") { source = t.value; render(); } });

const css = document.createElement("style");
css.textContent = `
.logo { width: 22px; height: 22px; background: var(--accent); }
.body { flex: 1; }
.wrap { max-width: 980px; margin: 0 auto; padding: 18px 20px 40px; display: flex; flex-direction: column; gap: 12px; }
.search { border: 1px solid var(--line); background: var(--bg2); padding: 0 10px; }
.search input { border: none; background: transparent; }
#src { width: 160px; }
.tabs { display: flex; gap: 2px; border-bottom: 1px solid var(--line-soft); flex-wrap: wrap; }
.tab { border: none; border-bottom: 2px solid transparent; font-family: var(--mono); font-size: 10.5px; letter-spacing: 1.2px; text-transform: uppercase; color: var(--muted); padding: 7px 10px; }
.tab.on { color: var(--text); border-bottom-color: var(--accent); background: transparent; }
.sect { margin-top: 10px; }
.grp { padding: 10px 0 6px; gap: 6px; color: var(--muted); }
.rule { display: flex; align-items: center; gap: 12px; padding: 9px 4px; border-bottom: 1px solid var(--line-soft); }
.rule.off { opacity: .45; }
.ricon { width: 26px; height: 26px; border: 1px solid var(--line); display: grid; place-items: center; }
.rt { font-size: 12.5px; }
.small { font-size: 11px; }
.pad { padding: 6px 4px; }
.add { color: var(--green); } .del { color: var(--red); } .amb { color: var(--amber); } .acc { color: var(--accent); }
.addform { gap: 8px; padding: 10px; border: 1px solid var(--line); background: var(--bg2); }
.addform select { width: 220px; }
.mtx table { border-collapse: collapse; width: 100%; }
.mtx th, .mtx td { border-bottom: 1px solid var(--line-soft); padding: 8px 10px; text-align: left; vertical-align: middle; }
.mtx tr.gh td { padding-top: 16px; color: var(--accent); }
.cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 14px; }
.card2 { border: 1px solid var(--line-soft); background: var(--panel); padding: 16px; }
.wide { grid-column: 1 / -1; }
.secret { gap: 10px; }
.savebar { padding-top: 8px; }
`;
document.head.appendChild(css);
