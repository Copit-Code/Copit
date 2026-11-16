import * as os from "node:os";
import { ActSession, PAUSE_MS } from "./activity";
import { runCli } from "./agents/cli";
import { Provider } from "./model";
import { GitLab, Jira, JiraIssueFull, JiraWorklog } from "./integrations";
import * as G from "./git";

// ------------------------------------------------------------------ Typen

export interface TimeRule { id: string; name: string; text: string; enabled: boolean }

export interface TimeSettings {
  minHours: number;
  workDays: number[];           // 0 = So … 6 = Sa
  dayStart: string;             // "08:00"
  rounding: number;             // Minuten
  accountField?: string;        // z. B. customfield_10100
  useTempo: boolean;
  tempoAttribute: string;       // "_Account_"
}

export interface Suggestion {
  issueKey: string; issueSummary: string; hours: number; comment: string; reason: string; confidence: number;
  /** Kurzes Thema der Arbeit (für das Themen-Gedächtnis) */
  topic?: string;
  /** Repository/Projekt, aus dem die Arbeit stammt */
  repo?: string;
  account?: string; accountFromMemory?: boolean; booked?: boolean; error?: string;
}

/** Rückfrage der KI zu einem unklaren Punkt beim Buchen. */
export interface TimeQuestion { id: string; question: string; options: string[]; answer?: string }

/** Gelerntes Wissen aus beantworteten Rückfragen – geht in jede künftige Analyse ein. */
export interface TimeFact { id: string; question: string; answer: string; createdAt: number }

export interface DayEntry { note: string; target?: number; suggestions: Suggestion[]; summary: string; questions?: TimeQuestion[] }

export interface AccountMemory { issues: Record<string, string>; projects: Record<string, string>; known: string[] }

/** Gelernte bzw. manuelle Zuordnung: Thema oder Repo → Ticket (z. B. Weiterentwicklungsprojekte). */
export interface TopicMapping { id: string; topic: string; ticket: string; repo?: string; hits: number; lastUsed: number; manual?: boolean }

/** Welche Quellen fließen in die Analyse ein? */
export interface Sources { tickets: boolean; commits: boolean; gitlab: boolean; sessions: boolean; note: boolean }
export const ALL_SOURCES: Sources = { tickets: true, commits: true, gitlab: true, sessions: true, note: true };

export interface DayContext {
  date: Date;
  target: number;
  worklogs: JiraWorklog[];
  issues: JiraIssueFull[];
  events: Awaited<ReturnType<GitLab["myEvents"]>>;
  commits: { ts: number; repo: string; subject: string }[];
  topics: TopicMapping[];
  /** Gelerntes Wissen + Antworten dieser Analyse */
  facts?: TimeFact[];
  sessions: { title: string; project: string; branch?: string; minutes: number; ticket?: string }[];
  note: string;
  warnings: string[];
}

export const DEFAULT_RULES: TimeRule[] = [
  { id: "r1", name: "Buchungstexte", enabled: true, text: "Buchungstexte sind kurz, fachlich und im Präteritum, z. B. 'Login-Fehler im Token-Refresh analysiert und behoben'. Keine Ticketnummern im Text." },
  { id: "r2", name: "Keine Mini-Buchungen", enabled: true, text: "Buche nie weniger als 0,25 h auf ein Ticket. Fasse Kleinkram auf dem passendsten Ticket zusammen." },
];

export const KEY_RE = /\b[A-Z][A-Z0-9_]{1,15}-\d{1,6}\b/g;
export const projectKey = (k: string) => k.includes("-") ? k.slice(0, k.lastIndexOf("-")) : k;
export const bookedHours = (c: Pick<DayContext, "worklogs">) => c.worklogs.reduce((s, w) => s + w.seconds, 0) / 3600;

// ------------------------------------------------------------------ Sammeln

export async function gather(o: {
  date: Date; hoursToBook: number; jira?: Jira; gitlab?: GitLab; repos: string[]; sessions: ActSession[]; note: string; accountField?: string;
  progress: (s: string) => void; sources?: Sources; topics?: TopicMapping[];
}): Promise<DayContext> {
  const src = o.sources ?? ALL_SOURCES;
  const ctx: DayContext = { date: o.date, target: 0, worklogs: [], issues: [], events: [], commits: [], sessions: [], note: src.note ? o.note : "", warnings: [], topics: o.topics ?? [] };
  const d0 = new Date(o.date.getFullYear(), o.date.getMonth(), o.date.getDate());
  const d1 = new Date(d0.getTime() + 86400000);
  const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

  if (o.jira) {
    o.progress("Jira · lese bereits gebuchte Zeiten");
    try { ctx.worklogs = await o.jira.myWorklogs(d0, d0); } catch (e: any) { ctx.warnings.push("Worklogs: " + e.message); }
    if (src.tickets) o.progress("Jira · suche bearbeitete Tickets");
    if (src.tickets) for (const jql of [
      `(assignee = currentUser() OR reporter = currentUser()) AND updated >= "${ymd(d0)}" AND updated < "${ymd(d1)}"`,
      `status CHANGED BY currentUser() DURING ("${ymd(d0)}", "${ymd(d1)}")`,
      `assignee = currentUser() AND statusCategory = "In Progress"`,
    ]) {
      try { ctx.issues.push(...await o.jira.searchFull(jql, 60, o.accountField)); } catch { /* JQL-Funktion evtl. nicht verfügbar */ }
    }
  } else ctx.warnings.push("Jira nicht konfiguriert");

  if (o.gitlab && src.gitlab) {
    o.progress("GitLab · lade Pushes, MRs und Kommentare");
    try { ctx.events = await o.gitlab.myEvents(d0); } catch (e: any) { ctx.warnings.push("GitLab: " + e.message); }
  }

  if (src.commits) o.progress("Git · durchsuche lokale Commits");
  if (src.commits) for (const repo of o.repos) {
    const email = (await G.git(repo, "config", "user.email")).out.trim();
    const r = await G.git(repo, "log", "--all", "--no-merges", `--since=${ymd(d0)} 00:00`, `--until=${ymd(d1)} 00:00`, ...(email ? [`--author=${email}`] : []), "--pretty=format:%aI\x1f%s");
    for (const l of r.out.split("\n").filter(Boolean)) {
      const [ts, subject] = l.split("\x1f");
      ctx.commits.push({ ts: Date.parse(ts), repo: repo.split(/[\\/]/).pop()!, subject });
    }
  }

  if (src.sessions) o.progress("Copit · werte Agent-Sessions des Tages aus");
  if (src.sessions) for (const s of o.sessions) {
    const ms = s.segments.reduce((t, [a, b]) => t + Math.max(0, Math.min(b, d1.getTime()) - Math.max(a, d0.getTime())), 0);
    if (ms <= 0) continue;
    ctx.sessions.push({ title: s.title, project: s.project, branch: s.branch, minutes: Math.round(ms / 60000), ticket: s.ticket });
  }

  // Erwähnte Tickets nachladen (Commits, Branches, Notiz, Sessions)
  if (o.jira) {
    const text = [...ctx.commits.map((c) => c.subject), ...ctx.events.map((e) => `${e.ref} ${e.commitTitle} ${e.target}`), ctx.note,
      ...ctx.sessions.map((s) => `${s.title} ${s.branch ?? ""} ${s.ticket ?? ""}`), ...ctx.topics.map((t) => t.ticket)].join(" ");
    const known = new Set(ctx.issues.map((i) => i.key));
    const keys = [...new Set((text.toUpperCase().match(KEY_RE) ?? []))].filter((k) => !known.has(k)).slice(0, 40);
    if (keys.length) {
      o.progress(`Jira · lade ${keys.length} erwähnte Tickets`);
      try { ctx.issues.push(...await o.jira.searchFull(`key in (${keys.map((k) => `"${k}"`).join(",")})`, 40, o.accountField)); }
      catch { for (const k of keys) { const i = await o.jira.issueFull(k, o.accountField); if (i) ctx.issues.push(i); } }
    }
    for (const w of ctx.worklogs) if (!ctx.issues.some((i) => i.key === w.issueKey))
      ctx.issues.push({ key: w.issueKey, summary: w.issueSummary, status: "", statusCategory: "", updated: 0 });
  }
  const seen = new Set<string>();
  ctx.issues = ctx.issues.filter((i) => !seen.has(i.key) && !!seen.add(i.key));
  ctx.target = bookedHours(ctx) + o.hoursToBook;
  return ctx;
}

// ------------------------------------------------------------------ Prompt / Plan

export function buildPrompt(c: DayContext, rules: TimeRule[], rounding: number): string {
  const topics = [...c.topics].sort((a, b) => Number(!!b.manual) - Number(!!a.manual) || b.hits - a.hits).slice(0, 40);
  const f = (n: number) => n.toLocaleString("de-DE", { maximumFractionDigits: 2 });
  const remaining = Math.max(0, c.target - bookedHours(c));
  const L: string[] = [];
  L.push("Du bist ein Assistent für Zeiterfassung in Jira. Erstelle Buchungsvorschläge für EINEN Arbeitstag.");
  L.push(`Datum: ${c.date.toLocaleDateString("de-DE", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" })}`);
  L.push(`Bereits gebucht: ${f(bookedHours(c))} h · ZU VERTEILEN: ${f(remaining)} h. Rundung: Vielfache von ${rounding} Minuten. Die Summe MUSS exakt ${f(remaining)} h ergeben.`, "");
  const active = rules.filter((r) => r.enabled && r.text.trim());
  if (active.length) { L.push("## Regeln (unbedingt einhalten)"); active.forEach((r) => L.push(`- ${r.name}: ${r.text}`)); L.push(""); }
  if (c.note.trim()) L.push("## Notiz des Nutzers zu diesem Tag (höchste Priorität)", c.note, "");
  if (c.worklogs.length) { L.push("## Bereits gebucht (nicht erneut buchen)"); c.worklogs.forEach((w) => L.push(`- ${w.issueKey}: ${f(w.seconds / 3600)} h – ${w.comment}`)); L.push(""); }
  const facts = (c.facts ?? []).slice(-60);
  if (facts.length) {
    L.push("## Gelerntes Wissen aus früheren Rückfragen (verbindlich anwenden)");
    facts.forEach((f) => L.push(`- F: ${f.question} → A: ${f.answer}`));
    L.push("");
  }
  if (topics.length) {
    L.push("## Bekannte Zuordnungen Thema/Repo → Ticket (Weiterentwicklung u. ä. – gelernt aus früheren Buchungen)");
    topics.forEach((t) => L.push(`- ${t.repo ? `[${t.repo}] ` : ""}${t.topic} → ${t.ticket}${t.manual ? " (fest)" : ` (${t.hits}× gebucht)`}`));
    L.push("Arbeit OHNE Ticketnummer (Commits, Sessions, Notiz) ordnest du über diese Zuordnungen dem passenden Ticket zu. Feste Zuordnungen haben Vorrang.", "");
  }
  L.push("## Jira-Tickets (Kandidaten)");
  c.issues.slice(0, 60).forEach((i) => L.push(`- ${i.key} [${i.status}] ${i.summary}`));
  if (!c.issues.length) L.push("- (keine gefunden)");
  L.push("");
  if (c.sessions.length) {
    L.push("## KI-Agent-Sessions an diesem Tag (aktive Zeit – starkes Signal für die Verteilung)");
    c.sessions.forEach((s) => L.push(`- ${s.minutes} min · [${s.project}${s.branch ? " · " + s.branch : ""}] ${s.ticket ? s.ticket + " " : ""}${s.title}`));
    L.push("");
  }
  if (c.events.length) {
    L.push("## GitLab-Aktivität");
    c.events.slice(0, 80).forEach((e) => L.push(`- ${new Date(e.ts).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" })} ${e.action} [${e.project}] ${e.commits ? `${e.commits} Commit(s) auf ${e.ref}: ${e.commitTitle}` : e.target}`));
    L.push("");
  }
  if (c.commits.length) {
    L.push("## Lokale Commits");
    c.commits.sort((a, b) => a.ts - b.ts).slice(0, 80).forEach((x) => L.push(`- ${new Date(x.ts).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" })} [${x.repo}] ${x.subject}`));
    L.push("");
  }
  L.push("## Aufgabe",
    "Verteile die Stunden auf die passendsten Tickets. Nutze nur Ticket-Keys aus den Daten oben (oder aus der Notiz).",
    "Gibt es für einen Arbeitsblock KEIN passendes Ticket, lass issueKey leer (\"\"), gib aber Thema, Repo, Stunden und Buchungstext trotzdem an – der Nutzer ordnet das Ticket dann zu.",
    "Schreibe pro Buchung einen konkreten Buchungstext auf Deutsch (1–2 Sätze, was fachlich gemacht wurde) und eine kurze Begründung.",
    "Antworte AUSSCHLIESSLICH mit JSON:",
    "Gib pro Buchung zusätzlich ein kurzes Thema (2–4 Wörter, z. B. \"Checkout Performance\") und das Repository/Projekt an, aus dem die Arbeit stammt.",
    "RÜCKFRAGEN: Ist etwas wirklich unklar (z. B. Meeting ohne Ticket, Arbeit passt zu mehreren Tickets, unbekanntes Thema) und NICHT durch das gelernte Wissen oder die Zuordnungen beantwortet,",
    "stelle bis zu 3 kurze Rückfragen mit 2–4 konkreten Antwortoptionen (z. B. Ticket-Keys). Formuliere sie allgemeingültig, damit die Antwort auch für künftige Tage gilt.",
    "Mache trotzdem schon Vorschläge nach bestem Wissen. Keine Rückfragen zu Dingen, die eindeutig sind.",
    '{"summary":"Ein Satz zum Tag","bookings":[{"issueKey":"ABC-123","hours":2.5,"comment":"Buchungstext","reason":"Begründung","confidence":0.8,"topic":"Thema","repo":"repo-name"}],"questions":[{"question":"Auf welches Ticket gehören Daily-Meetings?","options":["ORG-1","HAW-12"]}]}');
  return L.join("\n");
}

/** Erstes JSON-Objekt/Array aus einer KI-Antwort holen (```json-Block oder balancierte Klammern). */
export function findJson(text: string): any {
  for (const m of text.matchAll(/```(?:json)?\s*([\s\S]*?)```/g)) { try { return JSON.parse(m[1]); } catch { /* weiter */ } }
  for (let i = 0; i < text.length; i++) {
    if (text[i] !== "{" && text[i] !== "[") continue;
    let depth = 0, str = false, esc = false;
    for (let j = i; j < text.length; j++) {
      const c = text[j];
      if (str) { if (esc) esc = false; else if (c === "\\") esc = true; else if (c === '"') str = false; continue; }
      if (c === '"') str = true;
      else if (c === "{" || c === "[") depth++;
      else if (c === "}" || c === "]") { depth--; if (depth === 0) { try { return JSON.parse(text.slice(i, j + 1)); } catch { break; } } }
    }
  }
  return undefined;
}

const num = (v: any, fb = 0) => { const n = typeof v === "number" ? v : parseFloat(String(v ?? "").replace(",", ".")); return isNaN(n) ? fb : n; };

export function parsePlan(text: string): { summary: string; bookings: Suggestion[]; questions: TimeQuestion[] } {
  const j = findJson(text);
  if (!j) throw new Error("Die KI hat kein gültiges JSON geliefert.");
  const arr = Array.isArray(j) ? j : j.bookings ?? [];
  const questions: TimeQuestion[] = (Array.isArray(j) ? [] : j.questions ?? []).slice(0, 3)
    .map((q: any, i: number) => ({ id: `q${Date.now().toString(36)}${i}`, question: String(q?.question ?? q ?? "").trim(), options: (Array.isArray(q?.options) ? q.options : []).map(String).slice(0, 5) }))
    .filter((q: TimeQuestion) => q.question);
  return {
    questions,
    summary: Array.isArray(j) ? "" : String(j.summary ?? ""),
    bookings: arr.map((b: any) => ({
      issueKey: String(b.issueKey ?? b.key ?? "").trim().toUpperCase(), issueSummary: "", hours: num(b.hours),
      comment: String(b.comment ?? ""), reason: String(b.reason ?? ""), confidence: num(b.confidence, 0.7),
      topic: b.topic ? String(b.topic) : undefined, repo: b.repo ? String(b.repo) : undefined,
    })).filter((b: Suggestion) => (b.issueKey || b.topic) && b.hours > 0),
  };
}

/** Rundet auf das Raster und gleicht die Summe exakt auf das Ziel ab. */
export function normalize(items: Suggestion[], target: number, rounding: number) {
  if (!items.length || target <= 0) return;
  const step = Math.max(1, rounding) / 60;
  const sum = items.reduce((s, i) => s + i.hours, 0);
  if (sum <= 0) return;
  for (const i of items) i.hours = Math.max(step, Math.round((i.hours / sum) * target / step) * step);
  const diff = Math.round((target - items.reduce((s, i) => s + i.hours, 0)) / step) * step;
  const big = items.reduce((a, b) => (b.hours > a.hours ? b : a));
  big.hours = Math.max(step, Math.round((big.hours + diff) / step) * step);
  for (const i of items) i.hours = Math.round(i.hours * 100) / 100;
}

/** Fallback ohne KI: verteilt nach Agent-Zeit, Commits und Notiz. */
export function heuristicPlan(c: DayContext, rounding: number): { summary: string; bookings: Suggestion[]; questions: TimeQuestion[] } {
  const score = new Map<string, number>();
  const notes = new Map<string, string[]>();
  const known = new Set(c.issues.map((i) => i.key));
  const add = (text: string, w: number, comment?: string) => {
    for (const k of new Set(text.toUpperCase().match(KEY_RE) ?? [])) {
      if (!known.has(k)) continue;
      score.set(k, (score.get(k) ?? 0) + w);
      if (comment) { const l = notes.get(k) ?? []; if (l.length < 3 && !l.includes(comment)) l.push(comment); notes.set(k, l); }
    }
  };
  const byRepo = (repo: string) => [...c.topics].filter((t) => t.repo && repo.toLowerCase().includes(t.repo.toLowerCase())).sort((a, b) => Number(!!b.manual) - Number(!!a.manual) || b.hits - a.hits)[0]?.ticket;
  const viaTopic = (text: string, repo: string) => (text.toUpperCase().match(KEY_RE) ?? []).some((k) => known.has(k)) ? text : `${text} ${byRepo(repo) ?? ""}`;
  for (const s of c.sessions) add(viaTopic(`${s.ticket ?? ""} ${s.title} ${s.branch ?? ""}`, s.project), s.minutes / 15, s.title);
  for (const x of c.commits) add(viaTopic(x.subject, x.repo), 2, x.subject.replace(KEY_RE, "").replace(/^[\s:\-[\]]+/, ""));
  for (const e of c.events) add(`${e.ref} ${e.commitTitle} ${e.target}`, 1.5);
  add(c.note, 3);
  // Arbeit ohne bekanntes Ticket: pro Projekt/Repo als offener Block (Ticket wählt der Nutzer, Copit lernt es)
  const open = new Map<string, { score: number; repo: string; notes: string[] }>();
  const hasKey = (t: string) => (t.toUpperCase().match(KEY_RE) ?? []).some((k) => known.has(k));
  const addOpen = (repo: string, w: number, note: string) => {
    if (byRepo(repo)) return;
    const o = open.get(repo) ?? { score: 0, repo, notes: [] };
    o.score += w; if (o.notes.length < 3 && note && !o.notes.includes(note)) o.notes.push(note);
    open.set(repo, o);
  };
  for (const s of c.sessions) if (!hasKey(`${s.ticket ?? ""} ${s.title} ${s.branch ?? ""}`)) addOpen(s.project, s.minutes / 15, s.title);
  for (const x of c.commits) if (!hasKey(x.subject)) addOpen(x.repo, 2, x.subject);
  if (!score.size && !open.size) for (const i of c.issues.filter((i) => i.statusCategory === "indeterminate").slice(0, 3)) score.set(i.key, 1);
  const bookings: Suggestion[] = [...score].sort((a, b) => b[1] - a[1]).slice(0, 6).map(([k, s]) => {
    const i = c.issues.find((x) => x.key === k)!;
    return { issueKey: k, issueSummary: i.summary, hours: s, comment: (notes.get(k) ?? []).join("; ") || `Arbeit an: ${i.summary}`, reason: `Aktivitäts-Score ${s.toFixed(1)}`, confidence: 0.5 };
  });
  for (const o of [...open.values()].sort((a, b) => b.score - a.score).slice(0, 4))
    bookings.push({ issueKey: "", issueSummary: "", hours: o.score, comment: o.notes.join("; "), reason: "kein Ticket gefunden – bitte zuordnen", confidence: 0.4, topic: `Arbeit in ${o.repo}`, repo: o.repo });
  normalize(bookings, Math.max(0, c.target - bookedHours(c)), rounding);
  return { summary: "Vorschlag ohne KI – verteilt nach Agent-Zeit, Commits und Notiz.", bookings, questions: [] };
}

/** KI einmalig fragen (ohne Session), gibt den Antworttext zurück. */
export function askAi(prompt: string, provider: Provider, model: string | undefined, cliPath: string | undefined, progress: (s: string) => void, signal?: { stop?: () => void }): Promise<string> {
  return new Promise((resolve, reject) => {
    let text = "";
    const h = runCli({ provider, prompt, cwd: os.tmpdir(), mode: "ask", model, allowed: [], disallowed: [], cliPath }, (e) => {
      if (e.t === "delta") { text += e.text; progress("KI · schreibt Vorschläge…"); }
      else if (e.t === "thinking") progress("KI · denkt nach…");
      else if (e.t === "result") e.isError ? reject(new Error(e.subtype === "cancelled" ? "Abgebrochen" : e.text)) : resolve(e.text || text);
    });
    if (signal) signal.stop = () => h.stop();
  });
}

// ------------------------------------------------------------------ Accounts

export function resolveAccounts(items: Suggestion[], issues: JiraIssueFull[], mem: AccountMemory) {
  for (const b of items) {
    if (!b.issueKey) continue;
    const i = issues.find((x) => x.key === b.issueKey);
    if (i && !b.issueSummary) b.issueSummary = i.summary;
    if (i?.account) { b.account = i.account; b.accountFromMemory = false; continue; }
    const m = mem.issues[b.issueKey] ?? mem.projects[projectKey(b.issueKey)];
    b.account = m; b.accountFromMemory = !!m;
  }
}

export function remember(mem: AccountMemory, issueKey: string, account: string, project: boolean): AccountMemory {
  mem.issues[issueKey] = account;
  if (project) mem.projects[projectKey(issueKey)] = account;
  mem.known = [account, ...mem.known.filter((k) => k !== account)].slice(0, 30);
  return mem;
}

/** Startzeit für die nächste Buchung: Tagesbeginn bzw. nach dem letzten Worklog. */
export function nextStart(date: Date, dayStart: string, worklogs: JiraWorklog[]): Date {
  const [h, m] = dayStart.split(":").map(Number);
  let start = new Date(date.getFullYear(), date.getMonth(), date.getDate(), h || 8, m || 0);
  for (const w of worklogs) {
    const end = new Date(w.started + w.seconds * 1000);
    if (end > start && end.toDateString() === date.toDateString()) start = end;
  }
  return start;
}

export { PAUSE_MS };

// ------------------------------------------------------------------ Themen-Gedächtnis

const normTopic = (s: string) => s.trim().toLowerCase();

/** Merkt sich Thema/Repo → Ticket. weight: 1 = Buchung, 3 = manuelle Korrektur. */
export function learnTopic(map: TopicMapping[], s: Pick<Suggestion, "topic" | "repo" | "issueKey">, weight = 1): TopicMapping[] {
  const topic = (s.topic ?? "").trim();
  if (!topic && !s.repo) return map;
  const t = topic || `Arbeit in ${s.repo}`;
  const hit = map.find((m) => normTopic(m.topic) === normTopic(t) && (m.repo ?? "") === (s.repo ?? ""));
  if (hit) {
    if (hit.ticket !== s.issueKey) { if (hit.manual && weight < 3) return map; hit.ticket = s.issueKey; hit.hits = weight; }
    else hit.hits += weight;
    hit.lastUsed = Date.now();
    if (weight >= 3) hit.manual = true;
  } else map.push({ id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), topic: t, ticket: s.issueKey, repo: s.repo, hits: weight, lastUsed: Date.now(), manual: weight >= 3 });
  return map.sort((a, b) => b.lastUsed - a.lastUsed).slice(0, 200);
}

// ------------------------------------------------------------------ Wissen aus Rückfragen

/** Speichert eine beantwortete Rückfrage als Wissen (gleiche Frage → Antwort wird ersetzt). */
export function learnFact(facts: TimeFact[], question: string, answer: string): TimeFact[] {
  const q = question.trim(), a = answer.trim();
  if (!q || !a) return facts;
  const rest = facts.filter((f) => f.question.trim().toLowerCase() !== q.toLowerCase());
  return [...rest, { id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), question: q, answer: a, createdAt: Date.now() }].slice(-150);
}
