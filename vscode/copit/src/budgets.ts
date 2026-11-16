import { Agent, Budget, BudgetStatus, Run } from "./model";
import { samePath } from "./permissions";

export function periodRange(period: Budget["period"], now = new Date()): { from: number; to: number; label: string; key: string } {
  if (period === "week") {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const dow = (d.getDay() + 6) % 7;
    const from = new Date(d.getTime() - dow * 86400000);
    const to = new Date(from.getTime() + 7 * 86400000);
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

export function runHours(r: Run): number {
  return ((r.endedAt ?? Date.now()) - r.startedAt) / 3600000;
}

export function budgetCovers(b: Budget, agent: Agent | undefined): boolean {
  if (!b.folders.length) return true;
  if (!agent) return false;
  return b.folders.some((f) => samePath(f, agent.repo) || agent.repo.toLowerCase().startsWith(f.toLowerCase()));
}

export function evaluate(b: Budget, runs: Run[], agents: Map<string, Agent>, now = new Date()): BudgetStatus {
  const p = periodRange(b.period, now);
  let used = 0;
  for (const r of runs) {
    if (r.startedAt < p.from || r.startedAt >= p.to) continue;
    if (!budgetCovers(b, agents.get(r.agentId))) continue;
    used += b.type === "cost" ? r.costUsd : runHours(r);
  }
  return { budget: b, used, fraction: b.amount > 0 ? used / b.amount : 0, periodLabel: p.label, periodKey: p.key };
}

/** Neue Schwellen, die seit der letzten Meldung überschritten wurden. */
export function newThresholds(s: BudgetStatus): number[] {
  const done = new Set(s.budget.notified ?? []);
  return s.budget.warnAt.filter((pct) => s.fraction * 100 >= pct && !done.has(`${s.periodKey}:${pct}`));
}

export interface BudgetOverview {
  week: { cost: number; hours: number };
  month: { cost: number; hours: number };
  byProject: { project: string; cost: number; hours: number }[];
  byProvider: { provider: string; cost: number; hours: number }[];
  days: { day: string; cost: number; hours: number }[];
}

export function overview(runs: Run[], agents: Map<string, Agent>, now = new Date()): BudgetOverview {
  const w = periodRange("week", now), m = periodRange("month", now);
  const o: BudgetOverview = { week: { cost: 0, hours: 0 }, month: { cost: 0, hours: 0 }, byProject: [], byProvider: [], days: [] };
  const proj = new Map<string, { cost: number; hours: number }>();
  const prov = new Map<string, { cost: number; hours: number }>();
  const days = new Map<string, { cost: number; hours: number }>();
  for (let i = 13; i >= 0; i--) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - i);
    days.set(dayKey(d), { cost: 0, hours: 0 });
  }
  for (const r of runs) {
    const h = runHours(r);
    if (r.startedAt >= w.from && r.startedAt < w.to) { o.week.cost += r.costUsd; o.week.hours += h; }
    if (r.startedAt >= m.from && r.startedAt < m.to) {
      o.month.cost += r.costUsd; o.month.hours += h;
      const a = agents.get(r.agentId);
      const pk = a?.project ?? "?";
      const pv = a?.provider ?? "?";
      const pe = proj.get(pk) ?? { cost: 0, hours: 0 }; pe.cost += r.costUsd; pe.hours += h; proj.set(pk, pe);
      const ve = prov.get(pv) ?? { cost: 0, hours: 0 }; ve.cost += r.costUsd; ve.hours += h; prov.set(pv, ve);
    }
    const de = days.get(dayKey(new Date(r.startedAt)));
    if (de) { de.cost += r.costUsd; de.hours += h; }
  }
  o.byProject = [...proj].map(([project, v]) => ({ project, ...v })).sort((a, b) => b.cost - a.cost);
  o.byProvider = [...prov].map(([provider, v]) => ({ provider, ...v }));
  o.days = [...days].map(([day, v]) => ({ day, ...v }));
  return o;
}

export function dayKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}