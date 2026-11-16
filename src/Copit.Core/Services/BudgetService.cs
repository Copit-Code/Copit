namespace Copit.Core.Services;

public enum BudgetType { Cost, Hours }
public enum BudgetPeriod { Week, Month, Total }

public class Budget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public BudgetType Type { get; set; } = BudgetType.Cost;
    public double Amount { get; set; } = 50;
    public BudgetPeriod Period { get; set; } = BudgetPeriod.Week;
    /// <summary>Projektordner – leer = alle.</summary>
    public List<string> Folders { get; set; } = [];
    public List<int> WarnAt { get; set; } = [80, 100];
    /// <summary>Bei 100 % keine neuen Agent-Läufe starten.</summary>
    public bool HardStop { get; set; }
    public List<string> Notified { get; set; } = [];
}

public record BudgetStatus(Budget Budget, double Used, double Fraction, string PeriodLabel, string PeriodKey);

public record BudgetOverview(double WeekCost, double WeekHours, double MonthCost, double MonthHours,
    List<(string Project, double Cost, double Hours)> ByProject, List<(string Provider, double Cost, double Hours)> ByProvider,
    List<(DateTime Day, double Cost, double Hours)> Days);

public static class BudgetService
{
    public static (DateTime From, DateTime To, string Label, string Key) Range(BudgetPeriod p, DateTime now)
    {
        if (p == BudgetPeriod.Week)
        {
            var d = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var kw = System.Globalization.ISOWeek.GetWeekOfYear(d);
            return (d, d.AddDays(7), $"KW {kw}", $"{d:yyyy}-W{kw}");
        }
        if (p == BudgetPeriod.Month)
        {
            var d = new DateTime(now.Year, now.Month, 1);
            return (d, d.AddMonths(1), d.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("de-DE")), $"{d:yyyy-MM}");
        }
        return (DateTime.MinValue, DateTime.MaxValue, "Gesamt", "total");
    }

/// <summary>Pfade betriebssystemunabhängig vergleichen (Windows-Pfade auch auf der Linux-CI korrekt).</summary>
    private static string Norm(string p)
    {
        var abs = System.Text.RegularExpressions.Regex.IsMatch(p, @"^[a-zA-Z]:[\\/]") || p.StartsWith('/') || p.StartsWith(@"\\") ? p : Path.GetFullPath(p);
        return abs.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    public static bool Covers(Budget b, string? path)
    {
        if (b.Folders.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var x = Norm(path);
        return b.Folders.Any(f => { var n = Norm(f); return x == n || x.StartsWith(n + "/"); });
    }

    public static BudgetStatus Evaluate(Budget b, IEnumerable<UsageRecord> records, DateTime? now = null)
    {
        var r = Range(b.Period, now ?? DateTime.Now);
        var used = records.Where(x => x.Start >= r.From && x.Start < r.To && Covers(b, x.Path))
            .Sum(x => b.Type == BudgetType.Cost ? x.CostUsd : (x.End - x.Start).TotalHours);
        return new BudgetStatus(b, used, b.Amount > 0 ? used / b.Amount : 0, r.Label, r.Key);
    }

    public static List<int> NewThresholds(BudgetStatus s) =>
        s.Budget.WarnAt.Where(p => s.Fraction * 100 >= p && !s.Budget.Notified.Contains($"{s.PeriodKey}:{p}")).ToList();

    public static DateTime EarliestNeeded(IEnumerable<Budget> budgets, DateTime now)
    {
        var from = new[] { Range(BudgetPeriod.Month, now).From, Range(BudgetPeriod.Week, now).From, now.Date.AddDays(-14) }.Min();
        foreach (var b in budgets) { var f = Range(b.Period, now).From; if (f < from) from = f; }
        return from == DateTime.MinValue ? DateTime.Now.AddYears(-2) : from;
    }

    public static BudgetOverview Overview(IEnumerable<UsageRecord> records, DateTime? nowArg = null)
    {
        var now = nowArg ?? DateTime.Now;
        var w = Range(BudgetPeriod.Week, now); var m = Range(BudgetPeriod.Month, now);
        double wc = 0, wh = 0, mc = 0, mh = 0;
        var proj = new Dictionary<string, (double, double)>();
        var prov = new Dictionary<string, (double, double)>();
        var days = Enumerable.Range(0, 14).Select(i => now.Date.AddDays(i - 13)).ToDictionary(d => d, _ => (0d, 0d));
        foreach (var r in records)
        {
            var h = (r.End - r.Start).TotalHours;
            if (r.Start >= w.From && r.Start < w.To) { wc += r.CostUsd; wh += h; }
            if (r.Start >= m.From && r.Start < m.To)
            {
                mc += r.CostUsd; mh += h;
                var pe = proj.GetValueOrDefault(r.Project); proj[r.Project] = (pe.Item1 + r.CostUsd, pe.Item2 + h);
                var ve = prov.GetValueOrDefault(r.Provider); prov[r.Provider] = (ve.Item1 + r.CostUsd, ve.Item2 + h);
            }
            if (days.TryGetValue(r.Start.Date, out var de)) days[r.Start.Date] = (de.Item1 + r.CostUsd, de.Item2 + h);
        }
        return new BudgetOverview(wc, wh, mc, mh,
            proj.Select(x => (x.Key, x.Value.Item1, x.Value.Item2)).OrderByDescending(x => x.Item2).ToList(),
            prov.Select(x => (x.Key, x.Value.Item1, x.Value.Item2)).OrderByDescending(x => x.Item2).ToList(),
            days.Select(x => (x.Key, x.Value.Item1, x.Value.Item2)).ToList());
    }
}
