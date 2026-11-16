import * as path from "node:path";
import { Budget, BudgetStatus } from "./model";

/** Ein Stück Nutzung: aktive Zeitspanne mit Kosten, Projektpfad und Herkunft. */
export interface UsageRecord { start: number; end: number; costUsd: number; path: string; project: string; provider: string }

export function periodRange(period: Budget["period"], now = new Date()): { from: number; to: number; label: string; key: string } {
  if (period === "week") {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const dow = (d.getDay() + 6) % 7;
    const from = new Date(d.getFullYear(), d.getMonth(), d.getDate() - dow);
    const to = new Date(from.getFullYear(), from.getMonth(), from.getDate() + 7);
    const kw = isoWeek(from);
    return { from: from.getTime(), to: to.getTime(), label: `KW ${kw}`, key: `${from.getFullYear()}-W${kw}` };
  }
  if (period === "month") {
    const from = new Date(now.getFullYear(), now.getMonth(), 1);
    const to = new Date(now.getFullYear(), now.getMonth() + 1, 1);
    return { from: from.getTime(), to: to.getTime(), label: from.toLocaleDateString("de-DE", { month: "long", year: "numeric" }), key: `${from.getFullYear()}-${from.getMonth() + 1}` };
  }
  return { from: 0, to: 8640000000000000, label: "Gesamt", key: "total" };
}

export function isoWeek(d: Date): number {
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  const day = t.getUTCDay() || 7;
  t.setUTCDate(t.getUTCDate() + 4 - day);
  const y = new Date(Date.UTC(t.getUTCFullYear(), 0, 1));
  return Math.ceil(((t.getTime() - y.getTime()) / 86400000 + 1) / 7);
}

const norm = (p: string) => path.resolve(p).toLowerCase().replace(/[\\/]+$/, "");

/** Zählt ein Pfad zu einem Budget? (Ordner selbst oder darunter; leere Liste = alle) */
export function budgetCovers(b: Budget, p: string | undefined): boolean {
  if (!b.folders.length) return true;
  if (!p) return false;
  const x = norm(p);
  return b.folders.some((f) => { const n = norm(f); return x === n || x.startsWith(n + path.sep); });
}

export function evaluate(b: Budget, records: UsageRecord[], now = new Date()): BudgetStatus {
  const p = periodRange(b.period, now);
  let used = 0;
  for (const r of records) {
    if (r.start < p.from || r.start >= p.to || !budgetCovers(b, r.path)) continue;
    used += b.type === "cost" ? r.costUsd : (r.end - r.start) / 3600000;
  }
  return { budget: b, used, fraction: b.amount > 0 ? used / b.amount : 0, periodLabel: p.label, periodKey: p.key };
}

/** Neue Schwellen, die seit der letzten Meldung überschritten wurden. */
export function newThresholds(s: BudgetStatus): number[] {
  const done = new Set(s.budget.notified ?? []);
  return s.budget.warnAt.filter((pct) => s.fraction * 100 >= pct && !done.has(`${s.periodKey}:${pct}`));
}

/** Frühester Zeitpunkt, den die Budgets brauchen (für das Einlesen der Nutzung). */
export function earliestNeeded(budgets: Budget[], now = new Date()): number {
  let from = periodRange("month", now).from;
  for (const b of budgets) from = Math.min(from, periodRange(b.period, now).from);
  return Math.min(from, periodRange("week", now).from, now.getTime() - 14 * 86400000);
}

export interface BudgetOverview {
  week: { cost: number; hours: number };
  month: { cost: number; hours: number };
  byProject: { project: string; cost: number; hours: number }[];
  byProvider: { provider: string; cost: number; hours: number }[];
  days: { day: string; cost: number; hours: number }[];
}

export function overview(records: UsageRecord[], now = new Date()): BudgetOverview {
  const w = periodRange("week", now), m = periodRange("month", now);
  const o: BudgetOverview = { week: { cost: 0, hours: 0 }, month: { cost: 0, hours: 0 }, byProject: [], byProvider: [], days: [] };
  const proj = new Map<string, { cost: number; hours: number }>();
  const prov = new Map<string, { cost: number; hours: number }>();
  const days = new Map<string, { cost: number; hours: number }>();
  for (let i = 13; i >= 0; i--) days.set(dayKey(new Date(now.getFullYear(), now.getMonth(), now.getDate() - i)), { cost: 0, hours: 0 });
  for (const r of records) {
    const h = (r.end - r.start) / 3600000;
    if (r.start >= w.from && r.start < w.to) { o.week.cost += r.costUsd; o.week.hours += h; }
    if (r.start >= m.from && r.start < m.to) {
      o.month.cost += r.costUsd; o.month.hours += h;
      const pe = proj.get(r.project) ?? { cost: 0, hours: 0 }; pe.cost += r.costUsd; pe.hours += h; proj.set(r.project, pe);
      const ve = prov.get(r.provider) ?? { cost: 0, hours: 0 }; ve.cost += r.costUsd; ve.hours += h; prov.set(r.provider, ve);
    }
    const de = days.get(dayKey(new Date(r.start)));
    if (de) { de.cost += r.costUsd; de.hours += h; }
  }
  o.byProject = [...proj].map(([project, v]) => ({ project, ...v })).sort((a, b) => b.cost - a.cost);
  o.byProvider = [...prov].map(([provider, v]) => ({ provider, ...v })).sort((a, b) => b.cost - a.cost);
  o.days = [...days].map(([day, v]) => ({ day, ...v }));
  return o;
}

export function dayKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}
