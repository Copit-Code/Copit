import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { buildClaudeArgs, buildCopilotArgs, parseClaudeLine, toCopilotTool } from "../src/agents/cli";
import { extractQuestion, patternForDenial } from "../src/agents/manager";
import { defaultCopitRules, describeRule, flagsFor, matrixLevel, setMatrixLevel, PermissionRule } from "../src/permissions";
import { matches, render, triggerMatches } from "../src/automation";
import { evaluate, newThresholds, overview, periodRange } from "../src/budgets";
import { isNewer } from "../src/updater";
import { projectPathFromRemote } from "../src/git";
import { CopitDb } from "../src/db";
import { Agent, Automation, Budget, Run } from "../src/model";

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
  const agent = { id: "a", repo: "C:\\GIT\\Shop", project: "Shop", provider: "claude" } as Agent;
  const other = { id: "b", repo: "C:\\GIT\\Other", project: "Other", provider: "copilot" } as Agent;
  const runs: Run[] = [
    { id: "1", agentId: "a", startedAt: now.getTime() - 3600000, endedAt: now.getTime(), aiMs: 0, toolMs: 0, costUsd: 40, turns: 1, prompt: "" },
    { id: "2", agentId: "b", startedAt: now.getTime() - 3600000, endedAt: now.getTime(), aiMs: 0, toolMs: 0, costUsd: 99, turns: 1, prompt: "" },
    { id: "3", agentId: "a", startedAt: now.getTime() - 10 * 86400000, endedAt: now.getTime() - 10 * 86400000 + 1000, aiMs: 0, toolMs: 0, costUsd: 500, turns: 1, prompt: "" },
  ];
  const agents = new Map([["a", agent], ["b", other]]);
  const b: Budget = { id: "x", name: "Shop", type: "cost", amount: 50, period: "week", folders: ["C:\\GIT\\Shop"], warnAt: [50, 80, 100], hardStop: true };
  const s = evaluate(b, runs, agents, now);
  assert.equal(s.used, 40);
  assert.deepEqual(newThresholds(s), [50, 80]);
  assert.deepEqual(newThresholds({ ...s, budget: { ...b, notified: [`${s.periodKey}:50`] } }), [80]);
  const h = evaluate({ ...b, type: "hours", amount: 2 }, runs, agents, now);
  assert.equal(Math.round(h.used * 10) / 10, 1);
  const o = overview(runs, agents, now);
  assert.equal(o.week.cost, 139);
  assert.equal(o.byProject[0].project, "Shop"); // 40 + 500 (gleicher Monat) > 99
  assert.equal(o.byProject[0].cost, 540);
  assert.equal(o.month.cost, 639);
  assert.equal(o.days.length, 14);
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
