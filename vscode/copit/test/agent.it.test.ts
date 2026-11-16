/**
 * Integrationstests mit der echten Claude CLI (nur mit COPIT_IT=1, kostet kleine KI-Aufrufe).
 * Legen ein temporäres Git-Repo an und prüfen Agent-Lauf, Worktree, Status-Maschine,
 * Zeitmessung, Freigabe-Flow, Automationen und Budget-Stopp.
 */
import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { execFileSync } from "node:child_process";
import { CopitDb } from "../src/db";
import { AgentManager, patternsForDenial } from "../src/agents/manager";
import { AutomationEngine } from "../src/automation";
import { defaultCopitRules, PermissionRule } from "../src/permissions";
import { Budget, newId } from "../src/model";

const enabled = process.env.COPIT_IT === "1";
const MODEL = process.env.COPIT_IT_MODEL ?? "haiku";

function makeRepo(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "copit-it-"));
  const g = (...a: string[]) => execFileSync("git", a, { cwd: dir });
  g("init", "-b", "main");
  g("config", "user.email", "test@copit.local");
  g("config", "user.name", "Copit Test");
  fs.writeFileSync(path.join(dir, "README.md"), "# Testprojekt\n");
  g("add", "-A");
  g("commit", "-m", "init");
  return dir;
}

async function setup(rules: PermissionRule[], budgets: Budget[] = []) {
  const db = await CopitDb.memory(path.join(__dirname, "..", "node_modules", "sql.js", "dist"));
  const notes: string[] = [];
  const manager = new AgentManager(db, () => ({ instructions: "Antworte extrem kurz.", useWorktrees: true, claudeModel: MODEL }), () => rules, () => budgets);
  const repo = makeRepo();
  const engine = new AutomationEngine({ db, agents: manager, jira: () => undefined, gitlab: () => undefined, notify: (m) => notes.push(m), repos: () => [repo], pollSeconds: () => 999 });
  return { db, manager, engine, repo, notes };
}

test("Agent-Lauf: Worktree, Datei anlegen, Review-Status, Zeiten, Kosten", { skip: !enabled, timeout: 300000 }, async () => {
  const { db, manager, repo } = await setup(defaultCopitRules());
  const statuses: string[] = [];
  manager.on("changed", (a) => { if (statuses[statuses.length - 1] !== a.status) statuses.push(a.status); });

  const a = await manager.create({
    title: "Datei anlegen", repo, branch: "feature/it-test", provider: "claude", mode: "edit", model: MODEL, start: false,
    prompt: "Lege die Datei hallo.txt mit dem Inhalt 'Hallo Copit' an. Keine weiteren Schritte, keine Rückfragen.",
  });
  await manager.start(a.id);
  await new Promise((r) => setTimeout(r, 1500)); // finish() ist async

  const done = manager.get(a.id)!;
  const events = db.events(a.id);
  const runs = db.runs(0, Date.now() + 1);
  console.log("Status-Verlauf:", statuses.join(" → "), "| Endstatus:", done.status, "| Fehler:", done.error ?? "-");
  console.log("Zeiten:", { agentMs: done.agentMs, aiMs: done.aiMs, toolMs: done.toolMs, cost: done.costUsd, turns: done.turns, ctx: done.contextTokens });

  assert.ok(done.worktree, "Worktree sollte angelegt sein");
  assert.ok(done.cwd.includes(path.join(".copit", "worktrees")), "cwd im Worktree");
  assert.ok(fs.existsSync(path.join(done.cwd, "hallo.txt")), "hallo.txt im Worktree");
  assert.ok(!fs.existsSync(path.join(repo, "hallo.txt")), "Haupt-Checkout unberührt");
  assert.equal(execFileSync("git", ["rev-parse", "--abbrev-ref", "HEAD"], { cwd: done.cwd }).toString().trim(), "feature/it-test");
  assert.ok(statuses.includes("in_progress"));
  assert.equal(done.status, "review");
  assert.ok(done.linesAdded >= 1 && done.filesChanged >= 1, "Änderungen gezählt");
  assert.ok(events.some((e) => e.kind === "tool_use" && (e.name === "Write" || e.name === "Edit")), "Write-Tool im Verlauf");
  assert.ok(events.some((e) => e.kind === "result"));
  assert.ok(done.sessionId, "CLI-Session-ID gespeichert");
  assert.ok(done.agentMs > 0 && done.aiMs > 0 && done.aiMs <= done.agentMs, "AI-Zeit ≤ Agent-Zeit");
  assert.equal(runs.length, 1);
  assert.ok(runs[0].endedAt! > runs[0].startedAt);

  // Fortsetzen mit --resume
  await manager.reply(a.id, "Welche Datei hast du gerade angelegt? Antworte nur mit dem Dateinamen.");
  await new Promise((r) => setTimeout(r, 1500));
  const texts = db.events(a.id).filter((e) => e.kind === "text").map((e) => e.text ?? "");
  console.log("Antwort nach Resume:", texts[texts.length - 1]);
  assert.match(texts[texts.length - 1], /hallo\.txt/i);
  assert.equal(db.runs(0, Date.now() + 1).length, 2);

  await manager.remove(a.id);
  assert.ok(!fs.existsSync(done.cwd), "Worktree entfernt");
});

test("Freigabe-Flow: blockierter git commit → Need Input → erlauben → fertig", { skip: !enabled, timeout: 300000 }, async () => {
  // Nur Lesen + Dateien schreiben erlaubt; git commit ist NICHT erlaubt → CLI verweigert
  const rules = defaultCopitRules().filter((r) => !r.pattern.startsWith("Bash(git add"));
  const { db, manager, repo } = await setup(rules);
  fs.writeFileSync(path.join(repo, "a.txt"), "x\n");
  const a = await manager.create({
    title: "Commit", repo, provider: "claude", mode: "ask", model: MODEL, start: false,
    prompt: "Führe genau diesen Befehl aus: git add -A && git commit -m \"it-test\". Wenn er blockiert ist, versuche nichts anderes.",
  });
  await manager.start(a.id);
  await new Promise((r) => setTimeout(r, 1500));
  let s = manager.get(a.id)!;
  console.log("Nach Lauf 1:", s.status, s.pendingQuestion, JSON.stringify(s.pendingPermissions));
  assert.equal(s.status, "need_input");
  assert.ok((s.pendingPermissions ?? []).length > 0, "Freigabe angefragt");

  const patterns = [...new Set(s.pendingPermissions!.flatMap((p) => patternsForDenial(p.tool, p.input)))];
  console.log("Freizugebende Muster:", patterns);
  await manager.approve(a.id, patterns);
  await new Promise((r) => setTimeout(r, 1500));
  s = manager.get(a.id)!;
  const log = execFileSync("git", ["log", "--oneline", "-1"], { cwd: repo }).toString();
  console.log("Nach Freigabe:", s.status, "| letzter Commit:", log.trim());
  assert.match(log, /it-test/);
  assert.notEqual(s.status, "failed");
  assert.equal(db.events(a.id).filter((e) => e.kind === "user").length, 2);
});

test("Automation: agent.finished → Shell-Aktion + Benachrichtigung; Budget-Stopp blockiert", { skip: !enabled, timeout: 300000 }, async () => {
  const budgets: Budget[] = [];
  const { db, manager, engine, repo, notes } = await setup(defaultCopitRules(), budgets);
  db.saveAutomation({ id: newId(), name: "Fertig → Marker", enabled: true, trigger: { type: "agent.finished", params: {} }, conditions: [{ field: "title", op: "contains", value: "Auto" }],
    actions: [{ type: "shell", params: { command: "echo {{title}}> automation-marker.txt" } }, { type: "notify", params: { message: "{{title}} fertig ({{status}})" } }] });
  const a = await manager.create({ title: "Auto-Test", repo, provider: "claude", mode: "ask", model: MODEL, start: false, prompt: "Antworte nur mit OK." });
  await manager.start(a.id);
  await new Promise((r) => setTimeout(r, 2500));
  const marker = path.join(repo, "automation-marker.txt");
  console.log("Benachrichtigungen:", notes, "| Log:", db.automationLog(5).map((l) => l.message));
  assert.ok(fs.existsSync(marker), "Shell-Aktion ausgeführt");
  assert.match(fs.readFileSync(marker, "utf8"), /Auto-Test/);
  assert.ok(notes.some((n) => n.includes("Auto-Test fertig (done)")));
  void engine;

  // Budget mit Hard-Stop, das schon überschritten ist (Kosten > 0.000001 USD)
  budgets.push({ id: "b", name: "Mini", type: "cost", amount: 0.000001, period: "week", folders: [repo], warnAt: [100], hardStop: true });
  const b = await manager.create({ title: "Blockiert", repo, provider: "claude", mode: "ask", model: MODEL, start: false, prompt: "Antworte nur mit OK." });
  await manager.start(b.id);
  const s = manager.get(b.id)!;
  console.log("Budget-Stopp:", s.status, s.pendingQuestion);
  assert.equal(s.status, "need_input");
  assert.match(s.pendingQuestion ?? "", /Budget "Mini"/);
  assert.equal(db.runs(0, Date.now() + 1).filter((r) => r.agentId === b.id).length, 0, "kein Lauf gestartet");
});
