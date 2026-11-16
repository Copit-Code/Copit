import { CopitDb } from "./db";
import { Agent, AgentStatus, newId } from "./model";

/** Beispieldaten für Screenshots/Demos (nur mit COPIT_DEMO=1). */
export function seedDemo(db: CopitDb, repo: string) {
  if (db.get("demoSeeded", false)) return;
  const now = Date.now();
  const mk = (title: string, ticket: string | undefined, status: AgentStatus, ageMin: number, extra: Partial<Agent> = {}): Agent => ({
    id: newId(), title, prompt: title, ticket, project: "magento2", repo, cwd: repo, branch: "dev", provider: "claude", mode: "edit", model: "opus",
    status, createdAt: now - ageMin * 60000, updatedAt: now - ageMin * 60000, aiMs: 0, agentMs: 0, toolMs: 0, costUsd: 0, contextTokens: 0, turns: 0,
    linesAdded: 0, linesRemoved: 0, filesChanged: 0, ...extra,
  });
  const agents = [
    mk("Claudia Ticket-Automation Freigabe", "HAW-178", "inbox", 10),
    mk("New Agent", undefined, "inbox", 12),
    mk("Jira-Ticket-Agent konfigurieren", "HAW-178", "inbox", 120),
    mk("HAW-178", "HAW-178", "in_progress", 130, { linesAdded: 495, linesRemoved: 18, filesChanged: 7, costUsd: 4.12, running: false }),
    mk("Stage-Abschlusstest und Agent-Automatisierung", "HAW-178", "waiting_subagent", 125, { costUsd: 2.4 }),
    mk("Login Token-Refresh fixen", "SHOP-42", "need_input", 45, { pendingQuestion: "Soll der Refresh im Hintergrund oder beim nächsten Request passieren?\n- Hintergrund-Timer\n- Beim nächsten Request", costUsd: 0.8 }),
    mk("Testbericht und Agent-Workflow", "HAW-178", "review", 180, { linesAdded: 4340, linesRemoved: 1, filesChanged: 42, costUsd: 42.47 }),
    mk("Release Notes 2.4", "SHOP-50", "done", 300, { costUsd: 1.1 }),
    mk("Pipeline fixen dev", undefined, "failed", 240, { error: "Gestoppt", costUsd: 0.3 }),
  ];
  for (const a of agents) db.saveAgent(a);

  // Flow-Beispiel für den Review-Agent
  const a = agents[6];
  const run = (ago: number, prompt: string, tools: [string, string, number][], sub?: { desc: string; tools: [string, string][] }, cost = 0, durMin = 2) => {
    const runId = newId();
    const t0 = now - ago * 60000;
    db.addEvent({ agentId: a.id, runId, ts: t0, kind: "user", text: prompt });
    let t = t0 + 4000;
    let toolMs = 0;
    for (const [name, input, ms] of tools) {
      const id = newId();
      db.addEvent({ agentId: a.id, runId, ts: t, kind: "tool_use", toolId: id, name, text: input, durationMs: ms });
      db.addEvent({ agentId: a.id, runId, ts: t + ms, kind: "tool_result", toolId: id, text: "ok", durationMs: ms });
      t += ms + 3000; toolMs += ms;
    }
    if (sub) {
      const tid = newId();
      db.addEvent({ agentId: a.id, runId, ts: t, kind: "tool_use", toolId: tid, name: "Task", text: JSON.stringify({ subagent_type: "testbericht", description: sub.desc }), durationMs: 94000 });
      for (const [name, input] of sub.tools) db.addEvent({ agentId: a.id, runId, ts: t + 2000, kind: "tool_use", toolId: newId(), name, text: input, parentToolId: tid, durationMs: 4000 });
      db.addEvent({ agentId: a.id, runId, ts: t + 94000, kind: "tool_result", toolId: tid, text: "Bericht erstellt", durationMs: 94000 });
      t += 96000; toolMs += 94000;
    }
    db.addEvent({ agentId: a.id, runId, ts: t, kind: "text", text: "Die Testberichte sind jetzt **kurz und knackig**:\n- Zusammenfassung oben\n- Details pro Stage\n- Screenshots als Anhang" });
    const end = t0 + durMin * 60000;
    db.addEvent({ agentId: a.id, runId, ts: end, kind: "result", durationMs: end - t0, text: JSON.stringify({ subtype: "success", cost, turns: 6, aiMs: end - t0 - toolMs, toolMs }) });
    db.saveRun({ id: runId, agentId: a.id, startedAt: t0, endedAt: end, aiMs: end - t0 - toolMs, toolMs, costUsd: cost, turns: 6, prompt, ok: true });
  };
  run(200, "Ich habe ja eine Automatisierung hier, wegen Umsetzung Branch testen und Stage-Deploy…",
    [["Read", '{"file_path":"app/code/Testbericht.php"}', 800], ["Edit", '{"file_path":"app/code/Testbericht.php"}', 400], ["Bash", '{"command":"vendor/bin/phpunit"}', 21000], ["Read", '{"file_path":"README.md"}', 300]], undefined, 39.54, 3);
  run(150, "Die Testberichte sollen kurz und knackig bleiben, damit der PM sieht, dass es funktioniert.",
    [["Edit", '{"file_path":"app/code/Report.php"}', 500], ["Read", '{"file_path":"app/code/Report.php"}', 300], ["Bash", '{"command":"git diff --stat"}', 900]], undefined, 1.66, 2);
  run(90, "Ja bitte, das soll immer so gemacht werden – ich will einfach meine Arbeitsweise automatisieren.",
    [["Edit", '{"file_path":"automations.json"}', 600]], { desc: "Stage-Abschlusstest HAW-178", tools: [["Bash", '{"command":"curl -s https://stage.local/health"}'], ["Read", '{"file_path":"tests/stage.md"}']] }, 0.93, 2);
  a.costUsd = 42.13; a.turns = 18; a.agentMs = 7 * 60000; a.aiMs = 4.5 * 60000; a.toolMs = 2.5 * 60000; a.contextTokens = 96400;
  db.saveAgent(a);

  // weitere Läufe für den Kalender
  for (const [i, ag] of agents.entries()) {
    if (i === 6) continue;
    for (let d = 0; d < 4; d++) {
      const start = new Date(now - d * 86400000); start.setHours(8 + ((i * 3 + d * 2) % 9), (i * 17) % 60, 0, 0);
      if (start.getTime() > now) continue;
      const len = (10 + ((i * 7 + d * 5) % 50)) * 60000;
      db.saveRun({ id: newId(), agentId: ag.id, startedAt: start.getTime(), endedAt: start.getTime() + len, aiMs: len * 0.6, toolMs: len * 0.4, costUsd: 0.4 + (i % 4) * 0.9, turns: 4, prompt: ag.title, ok: ag.status !== "failed" });
    }
  }
  db.set("budgets", [
    { id: "b1", name: "Magento 2 Projekt", type: "cost", amount: 120, period: "week", folders: [repo], warnAt: [80, 100], hardStop: false },
    { id: "b2", name: "Lernen & Experimente", type: "hours", amount: 10, period: "month", folders: [], warnAt: [80, 100], hardStop: false },
  ]);
  db.set("demoSeeded", true);
}
