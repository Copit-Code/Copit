import { test } from "node:test";
import * as assert from "node:assert/strict";
import { buildPrompt, DayContext, DEFAULT_RULES, heuristicPlan, learnTopic, nextStart, normalize, parsePlan, remember, resolveAccounts, Suggestion, TopicMapping } from "../src/timebooking";
import { readAccount } from "../src/integrations";

const ctx = (): DayContext => ({
  date: new Date(2026, 10, 16), target: 8,
  worklogs: [{ id: "1", issueKey: "HAW-1", issueSummary: "Meeting", started: new Date(2026, 10, 16, 8, 0).getTime(), seconds: 3600, comment: "Daily" }],
  issues: [
    { key: "HAW-1", summary: "Meeting", status: "Open", statusCategory: "new", updated: 0 },
    { key: "HAW-178", summary: "Ticket-Automation", status: "In Arbeit", statusCategory: "indeterminate", updated: 0, account: "ACC-A" },
    { key: "SHOP-42", summary: "Login Fix", status: "In Arbeit", statusCategory: "indeterminate", updated: 0 },
  ],
  events: [], commits: [{ ts: 0, repo: "shop", subject: "SHOP-42: Token-Refresh repariert" }],
  sessions: [{ title: "HAW-178 Automatisierung", project: "magento2", branch: "feature/HAW-178", minutes: 240, ticket: "HAW-178" }],
  note: "1h Kundentermin zu SHOP-42", warnings: [], topics: [],
});

test("Plan aus KI-Antwort parsen (Codeblock, Komma-Zahlen, Kleinbuchstaben)", () => {
  const p = parsePlan('Hier:\n```json\n{"summary":"Tag","bookings":[{"issueKey":"haw-178","hours":"4,5","comment":"x"},{"issueKey":"SHOP-42","hours":2.5}]}\n```');
  assert.equal(p.summary, "Tag");
  assert.deepEqual(p.bookings.map((b) => [b.issueKey, b.hours]), [["HAW-178", 4.5], ["SHOP-42", 2.5]]);
  assert.equal(parsePlan('[{"issueKey":"X-1","hours":1}] fertig').bookings.length, 1);
  assert.throws(() => parsePlan("kein json"));
});

test("Normalisieren trifft das Ziel exakt im 15-Minuten-Raster", () => {
  const items = [3.1, 2.2, 1.3].map((hours) => ({ hours } as Suggestion));
  normalize(items, 7, 15);
  assert.equal(items.reduce((s, i) => s + i.hours, 0), 7);
  assert.ok(items.every((i) => Math.abs(i.hours * 4 - Math.round(i.hours * 4)) < 1e-9));
});

test("Heuristik nutzt Agent-Zeit, Commits und Notiz; verteilt nur den Rest", () => {
  const p = heuristicPlan(ctx(), 15);
  assert.ok(p.bookings.length >= 2);
  assert.equal(p.bookings[0].issueKey, "HAW-178", "240 min Agent-Zeit wiegt am meisten");
  assert.equal(p.bookings.reduce((s, b) => s + b.hours, 0), 7, "8 h Soll − 1 h gebucht");
});

test("Prompt enthält Regeln, Notiz, Sessions, Gebuchtes und Restzeit", () => {
  const p = buildPrompt(ctx(), DEFAULT_RULES, 15);
  assert.match(p, /ZU VERTEILEN: 7 h/);
  assert.match(p, /Kundentermin zu SHOP-42/);
  assert.match(p, /240 min .*HAW-178/);
  assert.match(p, /Bereits gebucht[\s\S]*HAW-1: 1 h/);
  assert.match(p, /Keine Mini-Buchungen/);
});

test("Accounts: Ticket-Feld, gemerkt pro Ticket und pro Projekt", () => {
  const mem = remember({ issues: {}, projects: {}, known: [] }, "SHOP-42", "ACC-SHOP", true);
  const items: Suggestion[] = [
    { issueKey: "HAW-178", issueSummary: "", hours: 1, comment: "", reason: "", confidence: 1 },
    { issueKey: "SHOP-99", issueSummary: "", hours: 1, comment: "", reason: "", confidence: 1 },
    { issueKey: "OTHER-1", issueSummary: "", hours: 1, comment: "", reason: "", confidence: 1 },
  ];
  resolveAccounts(items, ctx().issues, mem);
  assert.equal(items[0].account, "ACC-A");
  assert.equal(items[0].accountFromMemory, false);
  assert.equal(items[1].account, "ACC-SHOP", "über Projekt gemerkt");
  assert.equal(items[1].accountFromMemory, true);
  assert.equal(items[2].account, undefined);
  assert.deepEqual(mem.known, ["ACC-SHOP"]);
  assert.equal(readAccount({ id: 3, key: "ACC-1", value: "Kunde" }), "ACC-1");
  assert.equal(readAccount([{ value: "Opt" }]), "Opt");
  assert.equal(readAccount(null), undefined);
});

test("Startzeit nach letztem Worklog des Tages", () => {
  const c = ctx();
  assert.equal(nextStart(c.date, "08:00", c.worklogs).getHours(), 9);
  assert.equal(nextStart(c.date, "07:30", []).getMinutes(), 30);
});

test("Themen-Gedächtnis: lernen, Korrektur hat Vorrang, Prompt und Heuristik nutzen es", () => {
  let map: TopicMapping[] = [];
  map = learnTopic(map, { topic: "Checkout Performance", repo: "shop", issueKey: "SHOP-100" });
  map = learnTopic(map, { topic: "checkout performance", repo: "shop", issueKey: "SHOP-100" });
  assert.equal(map.length, 1);
  assert.equal(map[0].hits, 2);
  map = learnTopic(map, { topic: "Checkout Performance", repo: "shop", issueKey: "SHOP-200" }, 3);
  assert.equal(map[0].ticket, "SHOP-200");
  assert.ok(map[0].manual);
  map = learnTopic(map, { topic: "Checkout Performance", repo: "shop", issueKey: "SHOP-100" }, 1);
  assert.equal(map[0].ticket, "SHOP-200", "Buchung überschreibt feste Zuordnung nicht");

  const c = ctx();
  c.issues.push({ key: "SHOP-200", summary: "Weiterentwicklung Shop", status: "Offen", statusCategory: "new", updated: 0 });
  c.commits = [{ ts: 0, repo: "shop", subject: "Caching im Checkout verbessert" }];
  c.sessions = [];
  c.note = "";
  c.topics = map;
  const p = buildPrompt(c, [], 15);
  assert.match(p, /\[shop\] Checkout Performance → SHOP-200 \(fest\)/);
  assert.match(p, /"topic"/);
  const h = heuristicPlan(c, 15);
  assert.equal(h.bookings[0].issueKey, "SHOP-200", "Commit ohne Ticket über Repo-Zuordnung");
  assert.equal(parsePlan('{"bookings":[{"issueKey":"A-1","hours":1,"topic":"T","repo":"r"}]}').bookings[0].topic, "T");
});
test("Ohne passendes Ticket: offene Vorschläge mit Thema statt nichts", () => {
  const c = ctx();
  c.issues = []; c.worklogs = []; c.note = "";
  c.sessions = [{ title: "KI-Bildgenerierungs-API mit LoRAs", project: "backend/ai-backend", minutes: 120 }];
  c.commits = [{ ts: 0, repo: "copit", subject: "Activity-Ansicht" }];
  const h = heuristicPlan(c, 15);
  assert.equal(h.bookings.length, 2);
  assert.ok(h.bookings.every((b) => b.issueKey === "" && b.topic));
  assert.equal(h.bookings.reduce((s, b) => s + b.hours, 0), 8);
  const p = parsePlan('{"bookings":[{"issueKey":"","hours":3,"topic":"Copit IDE","repo":"copit"},{"issueKey":"","hours":2}]}');
  assert.equal(p.bookings.length, 1, "ohne Ticket UND ohne Thema wird verworfen");
  assert.match(buildPrompt(c, [], 15), /issueKey leer/);
});