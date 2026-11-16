// Räumt das Copit-VS-Code-Profil auf: In der Aktivitätsleiste bleibt nur Copit,
// Chat-/Copilot-Leiste und Panel-Ballast werden ausgeblendet. Läuft nur bei geschlossenem Copit-Fenster.
// Aufruf: node clean-profile.js <sql-wasm.js> <state.vscdb>
const fs = require("fs");
const initSqlJs = require(process.argv[2]);
const file = process.argv[3];

initSqlJs().then((SQL) => {
  const db = new SQL.Database(fs.existsSync(file) ? fs.readFileSync(file) : undefined);
  db.run("CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)");
  const get = (k) => { const r = db.exec("SELECT value FROM ItemTable WHERE key = ?", [k]); return r[0] ? JSON.parse(String(r[0].values[0][0])) : undefined; };
  const set = (k, v) => db.run("INSERT INTO ItemTable (key, value) VALUES (?, ?)", [k, typeof v === "string" ? v : JSON.stringify(v)]);

  const KEEP = "workbench.view.extension.copit";
  const builtins = ["workbench.view.explorer", "workbench.view.search", "workbench.view.scm", "workbench.view.debug", "workbench.view.remote",
    "workbench.view.extensions", "workbench.view.extension.test", "workbench.view.extension.references-view", "workbench.view.sync", "workbench.view.editSessions"];
  const viewlets = get("workbench.activity.pinnedViewlets2") ?? [];
  for (const id of builtins) if (!viewlets.some((v) => v.id === id)) viewlets.push({ id, pinned: false, visible: false });
  for (const v of viewlets) v.pinned = v.id === KEEP;
  if (!viewlets.some((v) => v.id === KEEP)) viewlets.push({ id: KEEP, pinned: true, visible: true, order: 0 });
  set("workbench.activity.pinnedViewlets2", viewlets);
  set("workbench.sidebar.activeviewletid", KEEP);

  const aux = get("workbench.auxiliarybar.pinnedPanels") ?? [{ id: "workbench.panel.chat", pinned: true, visible: false }];
  for (const p of aux) p.pinned = false;
  set("workbench.auxiliarybar.pinnedPanels", aux);
  set("workbench.auxiliaryBar.hidden", "true");

  // Panel unten: nur Terminal und Probleme behalten
  const panels = get("workbench.panel.pinnedPanels") ?? [];
  for (const p of panels) p.pinned = ["terminal", "workbench.panel.markers"].includes(p.id);
  set("workbench.panel.pinnedPanels", panels);

  fs.writeFileSync(file, Buffer.from(db.export()));
  console.log("Profil aufgeräumt:", viewlets.filter((v) => v.pinned).map((v) => v.id).join(", "));
});
