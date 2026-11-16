import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { buildClaudeArgs, buildCopilotArgs, parseClaudeLine, toCopilotTool } from "../src/agents/cli";
import { extractQuestion, patternForDenial, patternsForDenial } from "../src/agents/manager";
import { defaultCopitRules, describeRule, flagsFor, matrixLevel, setMatrixLevel, PermissionRule } from "../src/permissions";
import { matches, render, triggerMatches } from "../src/automation";
import { budgetCovers, evaluate, newThresholds, overview, periodRange, UsageRecord } from "../src/budgets";
import { buildActivity, ClaudeTranscripts, prettyModel, segmentsOf, unionMs, usageRecords } from "../src/activity";
import { costOf } from "../src/pricing";
import * as fs from "node:fs";
import * as os from "node:os";
import { isNewer } from "../src/updater";
import { projectPathFromRemote } from "../src/git";
import { CopitDb } from "../src/db";
import { Agent, Automation, Budget } from "../src/model";

test("Claude stream: init, text, tool_use mit Sub-Agent, result mit Denials", () => {
  assert.deepEqual(parseClaudeLine('{"type":"system","subtype":"init","session_id":"s1","model":"opus"}'), [{ t: "init", sessionId: "s1", model: "opus" }]);
  const ev = parseClaudeLine('{"type":"assistant","parent_tool_use_id":"task1","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"a.ts"}}]}}');
  assert.equal(ev[0].t, "tool_use");
  assert.equal((ev[0] as any).parent, "task1");
  const r = parseClaudeLine('{"type":"result","subtype":"success","is_error":false,"result":"ok","total_cost_usd":0.5,"duration_ms":1000,"num_turns":2,"permission_denials":[{"tool_name":"Bash","tool_input":{"command":"git push"}}]}')[0] as any;
  assert.equal(r.cost, 0.5);
  assert.equal(r.denials[0].tool, "Bash");
  assert.deepEqual(parseClaudeLine("kein json"), []);
});

test("Delta und Usage", () => {
  assert.equal((parseClaudeLine('{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hi"}}}')[0] as any).text, "Hi");
  const u = parseClaudeLine('{"type":"assistant","message":{"content":[],"usage":{"input_tokens":10,"cache_read_input_tokens":90,"output_tokens":5}}}')[0] as any;
  assert.equal(u.context, 100);
});

test("CLI-Argumente", () => {
  const a = buildClaudeArgs({ provider: "claude", prompt: "x", cwd: ".", mode: "edit", resumeSessionId: "s1", allowed: ["Bash(git status:*)"], disallowed: ["Bash(git push:*)"] });
  assert.ok(a.includes("acceptEdits"));
  assert.equal(a[a.indexOf("--resume") + 1], "s1");
  assert.equal(a[a.indexOf("--disallowedTools") + 1], "Bash(git push:*)");
  const c = buildCopilotArgs({ provider: "copilot", prompt: "x", cwd: ".", mode: "edit", allowed: ["Bash(git commit:*)"], disallowed: ["Bash(git push:*)"] });
  assert.ok(c.includes("shell(git commit)"));
  assert.ok(c.includes("write"));
  assert.equal(c[c.indexOf("--deny-tool") + 1], "shell(git push)");
  assert.equal(toCopilotTool("WebFetch"), undefined);
});

test("Rückfragen und Freigabe-Muster", () => {
  const q = extractQuestion("Text\n<rueckfrage>\nFrage: Welche DB?\n- Postgres\n- SQLite\n</rueckfrage>");
  assert.equal(q.question, "Welche DB?");
  assert.deepEqual(q.options, ["Postgres", "SQLite"]);
  assert.equal(q.clean, "Text");
  assert.equal(patternForDenial("Bash", JSON.stringify({ command: "git push origin main" })), "Bash(git push:*)");
  assert.equal(patternForDenial("Edit", "{}"), "Edit");
  const cmd = JSON.stringify({ command: 'cd "C:\\tmp a" && git add -A && git commit -m "x; y" | tee log.txt' });
  assert.deepEqual(patternsForDenial("Bash", cmd), ["Bash(git add:*)", "Bash(git commit:*)", "Bash(tee:*)"]);
});

test("Permissions: Matrix und Flags (deny schlägt allow)", () => {
  let rules: PermissionRule[] = defaultCopitRules();
  assert.equal(matrixLevel(rules, "git.forcepush"), "deny");
  assert.equal(matrixLevel(rules, "git.push"), "ask");
  rules = setMatrixLevel(rules, "git.push", "allow", "C:/repo");
  assert.equal(matrixLevel(rules, "git.push", "C:/repo"), "allow");
  assert.equal(matrixLevel(rules, "git.push"), "ask");
  const f = flagsFor(rules, "C:/repo", false);
  assert.ok(f.allowed.includes("Bash(git push:*)"));
  assert.ok(f.disallowed.includes("Bash(git push --force:*)"));
  assert.ok(!flagsFor(rules, "C:/other", false).allowed.includes("Bash(git push:*)"));
  rules.push({ id: "x", pattern: "Bash(git push:*)", level: "deny", source: "copit", enabled: true });
  assert.ok(!flagsFor(rules, "C:/repo", false).allowed.includes("Bash(git push:*)"));
  assert.deepEqual(describeRule("Bash(git push:*)"), { title: "git push *", tool: "Bash" });
});

test("Automation: Trigger, Bedingungen, Templates", () => {
  const a: Automation = { id: "1", name: "x", enabled: true, trigger: { type: "agent.moved", params: { to: "in_progress" } }, conditions: [{ field: "ticket", op: "contains", value: "HAW" }], actions: [] };
  assert.ok(triggerMatches(a, { type: "agent.moved", vars: { to: "in_progress", ticket: "HAW-1" } }));
  assert.ok(!triggerMatches(a, { type: "agent.moved", vars: { to: "review", ticket: "HAW-1" } }));
  assert.ok(!triggerMatches(a, { type: "agent.moved", vars: { to: "in_progress", ticket: "SHOP-1" } }));
  assert.ok(!triggerMatches({ ...a, enabled: false }, { type: "agent.moved", vars: { to: "in_progress", ticket: "HAW-1" } }));
  assert.equal(render("{{ticket}}: {{title}} {{missing}}", { ticket: "A-1", title: "Fix" }), "A-1: Fix ");
  assert.ok(matches({ field: "branch", op: "matches", value: "^feature/" }, { branch: "feature/x" }));
});

test("Budgets: Woche, Ordner-Filter, Schwellen, Übersicht", () => {
  const now = new Date(2026, 10, 18, 12); // Mi 18.11.2026
  const w = periodRange("week", now);
  assert.equal(new Date(w.from).getDay(), 1);
  const h = 3600000, t = now.getTime();
  const recs: UsageRecord[] = [
    { start: t - h, end: t, costUsd: 40, path: "C:\\GIT\\Shop", project: "Shop", provider: "Desktop app" },
    { start: t - h, end: t, costUsd: 99, path: "C:\\GIT\\Other", project: "Other", provider: "CLI" },
    { start: t - 2 * h, end: t - h, costUsd: 5, path: "C:\\GIT\\Shop\\sub", project: "Shop", provider: "CLI" },
    { start: t - 10 * 86400000, end: t - 10 * 86400000 + 1000, costUsd: 500, path: "C:\\GIT\\Shop", project: "Shop", provider: "CLI" },
  ];
  const b: Budget = { id: "x", name: "Shop", type: "cost", amount: 50, period: "week", folders: ["C:\\GIT\\Shop"], warnAt: [50, 80, 100], hardStop: true };
  const s = evaluate(b, recs, now);
  assert.equal(s.used, 45); // inkl. Unterordner, ohne alte Woche und ohne Other
  assert.deepEqual(newThresholds(s), [50, 80]);
  assert.deepEqual(newThresholds({ ...s, budget: { ...b, notified: [`${s.periodKey}:50`] } }), [80]);
  assert.ok(!budgetCovers(b, "C:\\GIT\\Shopware"), "Präfix ohne Trenner zählt nicht");
  assert.equal(evaluate({ ...b, type: "hours", amount: 2 }, recs, now).used, 2);
  const o = overview(recs, now);
  assert.equal(o.week.cost, 144);
  assert.equal(o.byProject[0].project, "Shop"); // 40 + 5 + 500 (gleicher Monat)
  assert.equal(o.month.cost, 644);
  assert.equal(o.days.length, 14);
});

test("Activity: Pausenregel, Vereinigung, Transkript-Parsing mit Streaming-Dedupe", async () => {
  const m = 60000;
  assert.deepEqual(segmentsOf([0, 5 * m, 9 * m, 30 * m]), [[0, 9 * m], [30 * m, 30 * m + 30000]]);
  assert.equal(unionMs([[0, 10], [5, 20], [30, 40]]), 30);
  assert.ok(costOf({ input: 1e6, output: 0, cacheRead: 0, cacheWrite: 0 }, "claude-opus-5-5") === 5);
  assert.equal(prettyModel("claude-opus-5-5"), "Opus 5.5");

  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "copit-tr-"));
  fs.mkdirSync(path.join(dir, "C--GIT-Shop"));
  const t0 = Date.parse("2026-11-16T17:00:00Z");
  const iso = (min: number) => new Date(t0 + min * m).toISOString();
  const usage = { input_tokens: 10, output_tokens: 100, cache_read_input_tokens: 1000, cache_creation_input_tokens: 0 };
  const lines = [
    { type: "user", sessionId: "s1", cwd: "C:\\GIT\\Shop", gitBranch: "feat/x", entrypoint: "claude-desktop", timestamp: iso(0), message: { content: "Baue Feature X" } },
    // zwei Zeilen derselben Nachricht (Streaming) → Usage nur einmal
    { type: "assistant", sessionId: "s1", cwd: "C:\\GIT\\Shop", timestamp: iso(1), message: { id: "m1", model: "claude-opus-5-5", usage, content: [{ type: "text", text: "ok" }] } },
    { type: "assistant", sessionId: "s1", cwd: "C:\\GIT\\Shop", timestamp: iso(2), message: { id: "m1", model: "claude-opus-5-5", usage, content: [{ type: "tool_use", name: "Read" }] } },
    { type: "assistant", sessionId: "s1", cwd: "C:\\GIT\\Shop", isSidechain: true, timestamp: iso(5), message: { id: "m2", model: "claude-haiku-5-5", usage, content: [{ type: "tool_use", name: "Bash" }] } },
    // nach 40 min Pause neue Aktivität
    { type: "user", sessionId: "s1", cwd: "C:\\GIT\\Shop", timestamp: iso(45), message: { content: "Weiter" } },
    { type: "assistant", sessionId: "s1", cwd: "C:\\GIT\\Shop", timestamp: iso(50), message: { id: "m3", model: "claude-opus-5-5", usage, content: [] } },
  ];
  lines.push({ type: "custom-title", customTitle: "Feature X bauen", sessionId: "s1" } as any);
  fs.writeFileSync(path.join(dir, "C--GIT-Shop", "s1.jsonl"), lines.map((l) => JSON.stringify(l)).join("\n"));
  const tr = new ClaudeTranscripts(dir);
  const raw = await tr.sessions(t0 - 3600000, t0 + 86400000);
  const acts = buildActivity({ from: t0 - 3600000, to: t0 + 86400000, transcripts: raw, agents: [], runs: [] });
  assert.equal(acts.length, 1);
  const a = acts[0];
  assert.equal(a.title, "Feature X bauen", "custom-title hat Vorrang vor dem ersten Prompt");
  assert.equal(a.branch, "feat/x");
  assert.equal(a.entry, "Desktop app");
  assert.equal(a.prompts, 2);
  assert.equal(a.toolCalls, 2);
  assert.equal(a.usage.output, 300, "m1 nur einmal gezählt (m1, m2, m3)");
  assert.equal(a.segments.length, 2, "Pause > 10 min trennt");
  assert.equal(unionMs(a.segments), 5 * m + 5 * m);
  assert.deepEqual(a.models.sort(), ["Haiku 5.5", "Opus 5.5"]);
  assert.ok(a.costUsd > 0 && !a.costExact);
  assert.equal(usageRecords(acts).length, 2);
});
test("Updater-Versionen und Remote-Pfade", () => {
  assert.ok(isNewer("v0.2.0", "0.1.9"));
  assert.ok(isNewer("0.1.10", "0.1.9"));
  assert.ok(!isNewer("0.1.0", "0.1.0"));
  assert.equal(projectPathFromRemote("git@gitlab.local:team/shop.git"), "team/shop");
  assert.equal(projectPathFromRemote("https://gitlab.local/a/b/c.git"), "a/b/c");
});

test("SQLite (sql.js): Agents, Events, Runs, KV", async () => {
  const db = await CopitDb.memory(path.join(__dirname, "..", "node_modules", "sql.js", "dist"));
  const a = { id: "a1", title: "T", status: "inbox", repo: "r" } as Agent;
  db.saveAgent(a);
  assert.equal(db.agents().length, 1);
  const id = db.addEvent({ agentId: "a1", runId: "r1", ts: 1, kind: "tool_use", toolId: "t1", name: "Read" });
  db.completeTool("a1", "t1", 250, false);
  db.updateEventText(id, "hallo");
  const ev = db.events("a1")[0];
  assert.equal(ev.durationMs, 250);
  assert.equal(ev.text, "hallo");
  db.saveRun({ id: "r1", agentId: "a1", startedAt: 100, endedAt: 200, aiMs: 50, toolMs: 50, costUsd: 1, turns: 1, prompt: "p", ok: true });
  assert.equal(db.runs(0, 1000)[0].costUsd, 1);
  db.set("k", { x: 1 });
  assert.deepEqual(db.get("k", null), { x: 1 });
  db.deleteAgent("a1");
  assert.equal(db.events("a1").length, 0);
});
