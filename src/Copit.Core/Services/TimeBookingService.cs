using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Copit.Core.Ai;
using Copit.Core.Models;

namespace Copit.Core.Services;

public class DayContext
{
    public DateOnly Date { get; set; }
    public double TargetHours { get; set; }
    public List<JiraWorklog> ExistingWorklogs { get; set; } = [];
    public List<JiraIssue> Issues { get; set; } = [];
    public List<GitLabEvent> GitLabEvents { get; set; } = [];
    public Dictionary<long, string> GitLabProjects { get; set; } = [];
    public List<GitCommit> Commits { get; set; } = [];
    public string DayNote { get; set; } = "";
    public List<string> ProjectNotes { get; set; } = [];
    public List<string> Sessions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public double BookedHours => ExistingWorklogs.Sum(w => w.TimeSpentSeconds) / 3600.0;
    public double RemainingHours => Math.Max(0, TargetHours - BookedHours);
}

public class BookingPlan
{
    public string Summary { get; set; } = "";
    public List<BookingSuggestion> Bookings { get; set; } = [];
}

public partial class TimeBookingService(ConnectionHub hub, DataService data, SettingsService settings, GitService git, AiService ai)
{
    private TimeSettings T => settings.Settings.Time;

    public bool IsWorkDay(DateOnly d) => T.WorkDays.Contains(d.DayOfWeek);

    public double TargetFor(DateOnly d) => data.PeekDay(d)?.TargetHours ?? (IsWorkDay(d) ? T.MinHoursPerDay : 0);

    /// <summary>Gebuchte Stunden pro Tag im Zeitraum (aus Jira).</summary>
    public async Task<Dictionary<DateOnly, double>> GetBookedHoursAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var jira = hub.RequireJira;
        var logs = await jira.GetMyWorklogsAsync(from, to, ct);
        return logs.GroupBy(l => DateOnly.FromDateTime(l.Started))
                   .ToDictionary(g => g.Key, g => g.Sum(x => x.TimeSpentSeconds) / 3600.0);
    }

    // ------------------------------------------------------------------ Sammeln

    public async Task<DayContext> GatherAsync(DateOnly day, double targetHours, IEnumerable<string> sessionInfos,
        Action<string> progress, CancellationToken ct = default)
    {
        var ctx = new DayContext { Date = day, TargetHours = targetHours };
        var entry = data.PeekDay(day);
        ctx.DayNote = entry?.Note ?? "";
        ctx.ProjectNotes = data.NotesOn(day)
            .Select(n => $"[{data.Project(n.ProjectId)?.Name ?? "?"}] {n.Text}").ToList();
        ctx.Sessions = sessionInfos.ToList();

        var jira = hub.Jira;
        if (jira != null)
        {
            progress("Jira · lese bereits gebuchte Zeiten");
            try { ctx.ExistingWorklogs = await jira.GetMyWorklogsAsync(day, day, ct); }
            catch (Exception e) { ctx.Warnings.Add("Worklogs: " + e.Message); }

            progress("Jira · suche bearbeitete Tickets");
            var d0 = day.ToString("yyyy-MM-dd"); var d1 = day.AddDays(1).ToString("yyyy-MM-dd");
            var queries = new[]
            {
                $"(assignee = currentUser() OR reporter = currentUser()) AND updated >= \"{d0}\" AND updated < \"{d1}\"",
                $"status CHANGED BY currentUser() DURING (\"{d0}\", \"{d1}\")",
                $"issueFunction in commented(\"by currentUser() on {d0}\")",
                "assignee = currentUser() AND statusCategory = \"In Progress\"",
            };
            foreach (var q in queries)
            {
                try { ctx.Issues.AddRange(await jira.SearchAsync(q, 60, ct)); }
                catch { /* manche JQL-Funktionen (ScriptRunner) sind nicht überall verfügbar */ }
            }
        }
        else ctx.Warnings.Add("Jira ist nicht konfiguriert.");

        var gl = hub.GitLab;
        if (gl != null)
        {
            progress("GitLab · lade Aktivitäten (Pushes, MRs, Kommentare)");
            try
            {
                ctx.GitLabEvents = await gl.GetMyEventsAsync(day, ct);
                foreach (var pid in ctx.GitLabEvents.Select(e => e.ProjectId).Distinct())
                    ctx.GitLabProjects[pid] = await gl.GetProjectNameAsync(pid, ct);
            }
            catch (Exception e) { ctx.Warnings.Add("GitLab: " + e.Message); }
        }

        progress("Git · durchsuche lokale Repositories");
        foreach (var p in data.Projects.Value.Projects.Where(p => GitService.IsRepo(p.SourceFolder)))
        {
            try
            {
                var email = await git.GetUserEmailAsync(p.SourceFolder, ct);
                var start = day.ToDateTime(TimeOnly.MinValue);
                ctx.Commits.AddRange(await git.GetCommitsAsync(p.SourceFolder, start, start.AddDays(1), email, ct));
            }
            catch (Exception e) { ctx.Warnings.Add($"Git {p.Name}: {e.Message}"); }
        }

        // Ticketnummern aus Commits / Branches / Events nachladen
        if (jira != null)
        {
            var text = string.Join(" ", ctx.Commits.Select(c => c.Subject))
                       + " " + string.Join(" ", ctx.GitLabEvents.Select(e => $"{e.Ref} {e.CommitTitle} {e.TargetTitle}"))
                       + " " + ctx.DayNote + " " + string.Join(" ", ctx.ProjectNotes) + " " + string.Join(" ", ctx.Sessions);
            var known = ctx.Issues.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keys = IssueKeyRegex().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().Where(k => !known.Contains(k)).Take(40).ToList();
            if (keys.Count > 0)
            {
                progress($"Jira · lade {keys.Count} erwähnte Tickets");
                foreach (var k in keys)
                {
                    var i = await SafeGetIssue(jira, k, ct);
                    if (i != null) ctx.Issues.Add(i);
                }
            }
            foreach (var w in ctx.ExistingWorklogs.Where(w => !ctx.Issues.Any(i => i.Key == w.IssueKey)))
                ctx.Issues.Add(new JiraIssue { Key = w.IssueKey, Summary = w.IssueSummary });
        }
        ctx.Issues = ctx.Issues.DistinctBy(i => i.Key).ToList();
        return ctx;
    }

    private static async Task<JiraIssue?> SafeGetIssue(JiraClient jira, string key, CancellationToken ct)
    {
        try { return await jira.GetIssueAsync(key, ct); } catch { return null; }
    }

    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]{1,15}-\d{1,6}\b", RegexOptions.IgnoreCase)]
    public static partial Regex IssueKeyRegex();

    // ------------------------------------------------------------------ Prompt

    public string BuildPrompt(DayContext ctx)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Du bist ein Assistent für Zeiterfassung in Jira. Erstelle Buchungsvorschläge für EINEN Arbeitstag.");
        sb.AppendLine($"Datum: {ctx.Date:dddd, dd.MM.yyyy}");
        sb.AppendLine($"Soll-Stunden: {ctx.TargetHours.ToString("0.##", inv)}h · bereits gebucht: {ctx.BookedHours.ToString("0.##", inv)}h · ZU VERTEILEN: {ctx.RemainingHours.ToString("0.##", inv)}h");
        sb.AppendLine($"Rundung: Vielfache von {T.RoundingMinutes} Minuten. Die Summe der Vorschläge MUSS exakt den zu verteilenden Stunden entsprechen.");
        sb.AppendLine();
        var rules = T.RuleSets.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Text)).ToList();
        if (rules.Count > 0)
        {
            sb.AppendLine("## Regeln (unbedingt einhalten)");
            foreach (var r in rules) sb.AppendLine($"- {r.Name}: {r.Text}");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(ctx.DayNote))
            sb.AppendLine("## Notiz des Nutzers zu diesem Tag (höchste Priorität)").AppendLine(ctx.DayNote).AppendLine();
        if (ctx.ProjectNotes.Count > 0)
        {
            sb.AppendLine("## Projekt-Notizen dieses Tages");
            ctx.ProjectNotes.ForEach(n => sb.AppendLine("- " + n));
            sb.AppendLine();
        }
        if (ctx.ExistingWorklogs.Count > 0)
        {
            sb.AppendLine("## Bereits gebucht (nicht erneut buchen)");
            foreach (var w in ctx.ExistingWorklogs)
                sb.AppendLine($"- {w.IssueKey}: {(w.TimeSpentSeconds / 3600.0).ToString("0.##", inv)}h – {w.Comment}");
            sb.AppendLine();
        }
        sb.AppendLine("## Jira-Tickets (Kandidaten)");
        foreach (var i in ctx.Issues.Take(60))
            sb.AppendLine($"- {i.Key} [{i.Status}] {i.Summary}{(i.Updated?.Date == ctx.Date.ToDateTime(TimeOnly.MinValue) ? " (heute aktualisiert)" : "")}");
        if (ctx.Issues.Count == 0) sb.AppendLine("- (keine gefunden)");
        sb.AppendLine();
        if (ctx.GitLabEvents.Count > 0)
        {
            sb.AppendLine("## GitLab-Aktivität");
            foreach (var e in ctx.GitLabEvents.Take(80))
            {
                var proj = ctx.GitLabProjects.GetValueOrDefault(e.ProjectId, "");
                var detail = e.CommitCount > 0 ? $"{e.CommitCount} Commit(s) auf {e.Ref}: {e.CommitTitle}" : $"{e.TargetType} {e.TargetTitle}";
                sb.AppendLine($"- {e.CreatedAt:HH:mm} {e.Action} [{proj}] {detail}");
            }
            sb.AppendLine();
        }
        if (ctx.Commits.Count > 0)
        {
            sb.AppendLine("## Lokale Commits");
            foreach (var c in ctx.Commits.OrderBy(c => c.Date).Take(80))
                sb.AppendLine($"- {c.Date:HH:mm} [{c.Repo}] {c.Subject}");
            sb.AppendLine();
        }
        if (ctx.Sessions.Count > 0)
        {
            sb.AppendLine("## KI-Sessions in Copit an diesem Tag");
            ctx.Sessions.ForEach(s => sb.AppendLine("- " + s));
            sb.AppendLine();
        }
        sb.AppendLine("""
            ## Aufgabe
            Verteile die Stunden sinnvoll auf die passendsten Tickets. Nutze nur Ticket-Keys aus den Daten oben
            (oder aus der Notiz des Nutzers). Schreibe pro Buchung einen guten, konkreten Buchungstext auf Deutsch
            (1–2 Sätze, was fachlich gemacht wurde). Begründe kurz, warum die Zeit auf dieses Ticket gehört.
            Antworte AUSSCHLIESSLICH mit JSON in diesem Format:
            ```json
            {"summary":"Ein Satz zum Tag","bookings":[{"issueKey":"ABC-123","hours":2.5,"comment":"Buchungstext","reason":"Begründung","confidence":0.8}]}
            ```
            """);
        return sb.ToString();
    }

    public static BookingPlan ParsePlan(string aiText)
    {
        var node = JsonExtract.Find(aiText) ?? throw new FormatException("Die KI hat kein gültiges JSON geliefert.");
        var plan = new BookingPlan();
        JsonArray? arr = node as JsonArray ?? node["bookings"] as JsonArray;
        if (node is JsonObject) plan.Summary = node.Str("summary");
        foreach (var b in arr ?? [])
        {
            var key = b.Str("issueKey");
            if (string.IsNullOrWhiteSpace(key)) key = b.Str("key");
            var hours = JsonExtract.Num(b, "hours");
            if (string.IsNullOrWhiteSpace(key) || hours <= 0) continue;
            plan.Bookings.Add(new BookingSuggestion
            {
                IssueKey = key.Trim().ToUpperInvariant(),
                Hours = hours,
                Comment = b.Str("comment"),
                Reason = b.Str("reason"),
                Confidence = JsonExtract.Num(b, "confidence", 0.7)
            });
        }
        return plan;
    }

    /// <summary>Rundet auf das Raster und gleicht die Summe exakt auf das Ziel ab.</summary>
    public static void Normalize(List<BookingSuggestion> items, double targetHours, int roundingMinutes)
    {
        if (items.Count == 0 || targetHours <= 0) return;
        var step = Math.Max(1, roundingMinutes) / 60.0;
        var sum = items.Sum(i => i.Hours);
        if (sum <= 0) return;
        foreach (var i in items)
            i.Hours = Math.Max(step, Math.Round(i.Hours / sum * targetHours / step) * step);
        var diff = Math.Round((targetHours - items.Sum(i => i.Hours)) / step) * step;
        var biggest = items.OrderByDescending(i => i.Hours).First();
        biggest.Hours = Math.Max(step, Math.Round((biggest.Hours + diff) / step) * step);
    }

    /// <summary>Fallback ohne KI: verteilt nach Aktivität.</summary>
    public BookingPlan HeuristicPlan(DayContext ctx)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var comments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string text, double w)
        {
            foreach (Match m in IssueKeyRegex().Matches(text))
            {
                var k = m.Value.ToUpperInvariant();
                if (!ctx.Issues.Any(i => i.Key.Equals(k, StringComparison.OrdinalIgnoreCase))) continue;
                scores[k] = scores.GetValueOrDefault(k) + w;
                if (!comments.TryGetValue(k, out var l)) comments[k] = l = [];
                var t = IssueKeyRegex().Replace(text, "").Trim(' ', ':', '-', '[', ']');
                if (t.Length > 3 && l.Count < 3 && !l.Contains(t)) l.Add(t);
            }
        }
        foreach (var c in ctx.Commits) Add(c.Subject, 2);
        foreach (var e in ctx.GitLabEvents) Add($"{e.Ref} {e.CommitTitle} {e.TargetTitle}", 1.5);
        Add(ctx.DayNote, 3);
        foreach (var i in ctx.Issues.Where(i => i.Updated.HasValue && DateOnly.FromDateTime(i.Updated.Value) == ctx.Date))
            scores[i.Key] = scores.GetValueOrDefault(i.Key) + 1;
        if (scores.Count == 0)
            foreach (var i in ctx.Issues.Where(i => i.Bucket == IssueBucket.InProgress).Take(3)) scores[i.Key] = 1;

        var plan = new BookingPlan { Summary = "Heuristischer Vorschlag (ohne KI) basierend auf Commits und Ticket-Aktivität." };
        foreach (var (key, score) in scores.OrderByDescending(s => s.Value).Take(6))
        {
            var issue = ctx.Issues.First(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var text = comments.TryGetValue(key, out var l) && l.Count > 0 ? string.Join("; ", l) : $"Arbeit an: {issue.Summary}";
            plan.Bookings.Add(new BookingSuggestion { IssueKey = key, IssueSummary = issue.Summary, Hours = score, Comment = text, Reason = $"Aktivitäts-Score {score:0.#}", Confidence = 0.5 });
        }
        Normalize(plan.Bookings, ctx.RemainingHours, T.RoundingMinutes);
        return plan;
    }

    public async Task<BookingPlan> SuggestAsync(DayContext ctx, AiProvider provider, Action<string> progress, CancellationToken ct = default)
    {
        if (ctx.RemainingHours <= 0) return new BookingPlan { Summary = "Der Tag ist bereits vollständig gebucht." };
        progress($"KI · {provider} formuliert Buchungsvorschläge");
        var text = await ai.AskAsync(BuildPrompt(ctx), provider, null, s => progress($"KI · {s}"), ct);
        var plan = ParsePlan(text);
        foreach (var b in plan.Bookings)
            b.IssueSummary = ctx.Issues.FirstOrDefault(i => i.Key.Equals(b.IssueKey, StringComparison.OrdinalIgnoreCase))?.Summary ?? b.IssueSummary;
        Normalize(plan.Bookings, ctx.RemainingHours, T.RoundingMinutes);
        await ResolveAccountsAsync(plan.Bookings, ctx.Issues, progress, ct);
        return plan;
    }

    // ------------------------------------------------------------------ Accounts

    public async Task ResolveAccountsAsync(List<BookingSuggestion> items, List<JiraIssue> known, Action<string> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Settings.Jira.AccountFieldId) && !settings.Settings.Jira.UseTempo) return;
        progress("Jira · prüfe Accounts der Tickets");
        var mem = data.Accounts.Value;
        foreach (var b in items)
        {
            var issue = known.FirstOrDefault(i => i.Key.Equals(b.IssueKey, StringComparison.OrdinalIgnoreCase));
            if (issue == null && hub.Jira != null) issue = await SafeGetIssue(hub.Jira, b.IssueKey, ct);
            if (issue != null && string.IsNullOrWhiteSpace(b.IssueSummary)) b.IssueSummary = issue.Summary;
            if (!string.IsNullOrWhiteSpace(issue?.Account)) { b.Account = issue!.Account; b.AccountFromMemory = false; continue; }
            if (mem.IssueAccounts.TryGetValue(b.IssueKey, out var a) || mem.ProjectAccounts.TryGetValue(ProjectKey(b.IssueKey), out a))
            {
                b.Account = a; b.AccountFromMemory = true;
            }
            else b.Account = null;
        }
    }

    public static string ProjectKey(string issueKey) => issueKey.Contains('-') ? issueKey[..issueKey.LastIndexOf('-')] : issueKey;

    public async Task RememberAccountAsync(string issueKey, string account, bool forWholeProject)
    {
        var mem = data.Accounts.Value;
        mem.IssueAccounts[issueKey] = account;
        if (forWholeProject) mem.ProjectAccounts[ProjectKey(issueKey)] = account;
        if (!mem.KnownAccounts.Contains(account)) mem.KnownAccounts.Insert(0, account);
        await data.Accounts.SaveAsync();
    }

    public bool AccountRequired => settings.Settings.Jira.UseTempo || !string.IsNullOrWhiteSpace(settings.Settings.Jira.AccountFieldId);

    // ------------------------------------------------------------------ Buchen

    public async Task<List<(BookingSuggestion Item, string? Error)>> BookAsync(DateOnly day, IEnumerable<BookingSuggestion> items,
        IEnumerable<JiraWorklog> existing, Action<string> progress, CancellationToken ct = default)
    {
        var jira = hub.RequireJira;
        var start = day.ToDateTime(TimeOnly.TryParse(T.DayStart, out var t) ? t : new TimeOnly(8, 0));
        var lastEnd = existing.Select(w => w.Started.AddSeconds(w.TimeSpentSeconds)).DefaultIfEmpty(start).Max();
        if (lastEnd > start && DateOnly.FromDateTime(lastEnd) == day) start = lastEnd;
        var results = new List<(BookingSuggestion, string?)>();
        foreach (var b in items.Where(i => !i.Booked && i.Hours > 0))
        {
            progress($"Buche {b.Hours:0.##}h auf {b.IssueKey}");
            var seconds = (long)Math.Round(b.Hours * 3600);
            try
            {
                await jira.AddWorklogAsync(b.IssueKey, start, seconds, b.Comment, b.Account, ct);
                b.Booked = true;
                results.Add((b, null));
                start = start.AddSeconds(seconds);
            }
            catch (Exception e) { results.Add((b, e.Message)); }
        }
        return results;
    }
}
