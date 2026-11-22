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
    public List<DaySession> Sessions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<TopicMapping> Topics { get; set; } = [];
    public List<TimeFact> Facts { get; set; } = [];
    public double BookedHours => ExistingWorklogs.Sum(w => w.TimeSpentSeconds) / 3600.0;
    public double RemainingHours => Math.Max(0, TargetHours - BookedHours);
}

public class BookingPlan
{
    public string Summary { get; set; } = "";
    public List<BookingSuggestion> Bookings { get; set; } = [];
    public List<TimeQuestion> Questions { get; set; } = [];
}

/// <summary>Stunden eines Tages: mit Account, ohne Account, Account unbekannt (Tempo nicht erreichbar).</summary>
public record DayHours(double WithAccount, double WithoutAccount, double Unknown)
{
    public double Total => WithAccount + WithoutAccount + Unknown;
}

public partial class TimeBookingService(ConnectionHub hub, DataService data, SettingsService settings, GitService git, AiService ai)
{
    private TimeSettings T => settings.Settings.Time;

    public bool IsWorkDay(DateOnly d) => T.WorkDays.Contains(d.DayOfWeek);

    public double TargetFor(DateOnly d) => data.PeekDay(d)?.TargetHours ?? (IsWorkDay(d) ? T.MinHoursPerDay : 0);

    /// <summary>Ohne Jira bucht Copit intern (lokal gespeichert, gleiche Ansicht).</summary>
    public bool LocalMode => hub.Jira == null;

    /// <summary>Eigene Buchungen im Zeitraum – aus Jira oder intern.</summary>
    public async Task<List<JiraWorklog>> GetWorklogsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (hub.Jira is { } jira) return await jira.GetMyWorklogsAsync(from, to, ct);
        return data.LocalWorklogs.Value.Worklogs.Where(w => DateOnly.FromDateTime(w.Started) >= from && DateOnly.FromDateTime(w.Started) <= to).OrderBy(w => w.Started).ToList();
    }

    public async Task DeleteLocalAsync(JiraWorklog w)
    {
        data.LocalWorklogs.Value.Worklogs.RemoveAll(x => x.Id == w.Id);
        await data.LocalWorklogs.SaveAsync();
    }

    /// <summary>Gebuchte Stunden eines Tages aufgeteilt nach Account (für den Kalenderbalken).</summary>
    public static DayHours Split(IEnumerable<JiraWorklog> logs)
    {
        double with = 0, without = 0, unknown = 0;
        foreach (var l in logs)
        {
            if (!l.AccountChecked) unknown += l.Hours;
            else if (l.HasAccount) with += l.Hours;
            else without += l.Hours;
        }
        return new DayHours(with, without, unknown);
    }

    /// <summary>Gebuchte Stunden pro Tag, aufgeteilt nach mit / ohne / unbekanntem Account.</summary>
    public async Task<Dictionary<DateOnly, DayHours>> GetBookedSplitAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var logs = await GetWorklogsAsync(from, to, ct);
        return logs.GroupBy(l => DateOnly.FromDateTime(l.Started)).ToDictionary(g => g.Key, g => Split(g));
    }

    /// <summary>Account an einer bestehenden Buchung nachtragen (Tempo) und für das Ticket merken.</summary>
    public async Task SetWorklogAccountAsync(JiraWorklog w, string account, bool rememberForProject, CancellationToken ct = default)
    {
        var jira = hub.Jira ?? throw new InvalidOperationException("Jira ist nicht verbunden.");
        await jira.SetWorklogAccountAsync(w, account, ct);
        await RememberAccountAsync(w.IssueKey, account.Trim(), rememberForProject);
    }

    /// <summary>Gebuchte Stunden pro Tag im Zeitraum (Jira oder intern).</summary>
    public async Task<Dictionary<DateOnly, double>> GetBookedHoursAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var logs = await GetWorklogsAsync(from, to, ct);
        return logs.GroupBy(l => DateOnly.FromDateTime(l.Started))
                   .ToDictionary(g => g.Key, g => g.Sum(x => x.TimeSpentSeconds) / 3600.0);
    }

    // ------------------------------------------------------------------ Sammeln

    public async Task<DayContext> GatherAsync(DateOnly day, double targetHours, IEnumerable<DaySession> sessions,
        Action<string> progress, CancellationToken ct = default, BookingSources? sources = null)
    {
        var src = sources ?? new BookingSources();
        var ctx = new DayContext { Date = day, TargetHours = targetHours };
        var entry = data.PeekDay(day);
        ctx.DayNote = src.Note ? entry?.Note ?? "" : "";
        ctx.ProjectNotes = src.Note ? data.NotesOn(day)
            .Select(n => $"[{data.Project(n.ProjectId)?.Name ?? "?"}] {n.Text}").ToList() : [];
        ctx.Sessions = src.Sessions ? sessions.Where(s => s.Minutes > 0).ToList() : [];
        ctx.Topics = data.TimeMemory.Value.Topics.ToList();
        ctx.Facts = data.TimeMemory.Value.Facts.ToList();

        var jira = hub.Jira;
        if (jira == null) ctx.ExistingWorklogs = await GetWorklogsAsync(day, day, ct);
        if (jira != null)
        {
            progress("Jira · lese bereits gebuchte Zeiten");
            try { ctx.ExistingWorklogs = await jira.GetMyWorklogsAsync(day, day, ct); }
            catch (Exception e) { ctx.Warnings.Add("Worklogs: " + e.Message); }

            if (src.Tickets) progress("Jira · suche bearbeitete Tickets");
            var d0 = day.ToString("yyyy-MM-dd"); var d1 = day.AddDays(1).ToString("yyyy-MM-dd");
            var queries = new[]
            {
                $"(assignee = currentUser() OR reporter = currentUser()) AND updated >= \"{d0}\" AND updated < \"{d1}\"",
                $"status CHANGED BY currentUser() DURING (\"{d0}\", \"{d1}\")",
                $"issueFunction in commented(\"by currentUser() on {d0}\")",
                "assignee = currentUser() AND statusCategory = \"In Progress\"",
            };
            foreach (var q in src.Tickets ? queries : [])
            {
                try { ctx.Issues.AddRange(await jira.SearchAsync(q, 60, ct)); }
                catch { /* manche JQL-Funktionen (ScriptRunner) sind nicht überall verfügbar */ }
            }
        }
        else ctx.Warnings.Add("Ohne Jira: Vorschläge aus Commits, Sessions und Notizen – gebucht wird intern.");

        var gl = hub.GitLab;
        if (gl != null && src.GitLab)
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

        if (src.Commits) progress("Git · durchsuche lokale Repositories");
        foreach (var p in data.Projects.Value.Projects.Where(p => src.Commits && GitService.IsRepo(p.SourceFolder)))
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
                       + " " + ctx.DayNote + " " + string.Join(" ", ctx.ProjectNotes)
                       + " " + string.Join(" ", ctx.Sessions.Select(s => $"{s.Title} {s.Branch} {s.Ticket}"))
                       + " " + string.Join(" ", ctx.Topics.Select(t => t.Ticket));
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
        sb.AppendLine($"Rundung: Vielfache von {T.RoundingMinutes} Minuten.");
        if (T.HumanEffort)
        {
            sb.AppendLine($"""
                AUFWAND: Gebucht wird, was eine erfahrene Person OHNE KI-Unterstützung für die tatsächlich erledigte Arbeit gebraucht hätte –
                lieber etwas weniger als zu viel. Schätze pro Arbeitsblock diesen Personen-Aufwand ("humanHours") anhand von Umfang der Änderungen,
                Commits, Tickets und KI-Session-Zeit (KI-Zeit ist NICHT Personenzeit). "hours" = humanHours × {T.EffortFactor.ToString("0.##", inv)}.
                Die Summe darf die zu verteilenden {ctx.RemainingHours.ToString("0.##", inv)}h NICHT überschreiten, muss sie aber nicht auffüllen.
                """);
        }
        else sb.AppendLine("Die Summe der Vorschläge MUSS exakt den zu verteilenden Stunden entsprechen.");
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
        var facts = ctx.Facts.TakeLast(60).ToList();
        if (facts.Count > 0)
        {
            sb.AppendLine("## Gelerntes Wissen aus früheren Rückfragen (verbindlich anwenden)");
            facts.ForEach(f => sb.AppendLine($"- F: {f.Question} → A: {f.Answer}"));
            sb.AppendLine();
        }
        var topics = ctx.Topics.Where(t => t.Ticket.Length > 0).OrderByDescending(t => t.Manual).ThenByDescending(t => t.Hits).Take(40).ToList();
        if (topics.Count > 0)
        {
            sb.AppendLine("## Bekannte Zuordnungen Thema/Repo → Ticket (Weiterentwicklung u. ä. – gelernt aus früheren Buchungen)");
            topics.ForEach(t => sb.AppendLine($"- {(t.Repo != null ? $"[{t.Repo}] " : "")}{t.Topic} → {t.Ticket}{(t.Manual ? " (fest)" : $" ({t.Hits}× gebucht)")}"));
            sb.AppendLine("Arbeit OHNE Ticketnummer (Commits, Sessions, Notiz) ordnest du über diese Zuordnungen dem passenden Ticket zu. Feste Zuordnungen haben Vorrang.");
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
            sb.AppendLine("## KI-Agent-Sessions an diesem Tag (aktive Zeit – starkes Signal für die Verteilung)");
            ctx.Sessions.ForEach(s => sb.AppendLine($"- {s.Minutes} min · [{s.Project}{(s.Branch != null ? " · " + s.Branch : "")}] {(s.Ticket != null ? s.Ticket + " " : "")}{s.Title}"));
            sb.AppendLine();
        }
        sb.AppendLine("""
            ## Aufgabe
            Verteile die Stunden auf die passendsten Tickets. Nutze nur Ticket-Keys aus den Daten oben (oder aus der Notiz).
            Gibt es für einen Arbeitsblock KEIN passendes Ticket, lass issueKey leer (""), gib aber Thema, Repo, Stunden und
            Buchungstext trotzdem an – der Nutzer ordnet das Ticket dann zu.
            Schreibe pro Buchung einen konkreten Buchungstext auf Deutsch (1–2 Sätze, was fachlich gemacht wurde) und eine kurze Begründung.
            Gib pro Buchung zusätzlich ein kurzes Thema (2–4 Wörter, z. B. "Checkout Performance") und das Repository/Projekt an.
            RÜCKFRAGEN: Ist etwas wirklich unklar (z. B. Meeting ohne Ticket, Arbeit passt zu mehreren Tickets, unbekanntes Thema) und
            NICHT durch das gelernte Wissen oder die Zuordnungen beantwortet, stelle bis zu 3 kurze Rückfragen mit 2–4 konkreten
            Antwortoptionen (z. B. Ticket-Keys). Formuliere sie allgemeingültig, damit die Antwort auch für künftige Tage gilt.
            Mache trotzdem schon Vorschläge nach bestem Wissen. Keine Rückfragen zu Dingen, die eindeutig sind.
            Antworte AUSSCHLIESSLICH mit JSON:
            ```json
            {"summary":"Ein Satz zum Tag","bookings":[{"issueKey":"ABC-123","humanHours":3,"hours":2.5,"comment":"Buchungstext","reason":"Begründung","confidence":0.8,"topic":"Thema","repo":"repo-name"}],"questions":[{"question":"Auf welches Ticket gehören Daily-Meetings?","options":["ORG-1","HAW-12"]}]}
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
            var topic = b.Str("topic");
            if ((string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(topic)) || hours <= 0) continue;
            plan.Bookings.Add(new BookingSuggestion
            {
                IssueKey = (key ?? "").Trim().ToUpperInvariant(),
                Topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
                Repo = b.Str("repo") is { Length: > 0 } repo ? repo : null,
                Hours = hours,
                Comment = b.Str("comment"),
                Reason = b.Str("reason"),
                HumanHours = JsonExtract.Num(b, "humanHours"),
                Confidence = JsonExtract.Num(b, "confidence", 0.7)
            });
        }
        if (node is JsonObject && node["questions"] is JsonArray qs)
            foreach (var q in qs.Take(3))
            {
                var text = q is JsonValue ? q.ToString() : q.Str("question");
                if (string.IsNullOrWhiteSpace(text)) continue;
                List<string> opts = q is JsonObject && q["options"] is JsonArray oa ? oa.Select(o => o?.ToString() ?? "").Where(o => o.Length > 0).Take(5).ToList() : [];
                plan.Questions.Add(new TimeQuestion { Question = text.Trim(), Options = opts });
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

    /// <summary>Personen-Aufwand: nur aufs Raster runden und höchstens bis zum Ziel kürzen – nie auffüllen.</summary>
    public static void Cap(List<BookingSuggestion> items, double maxHours, int roundingMinutes)
    {
        if (items.Count == 0) return;
        var step = Math.Max(1, roundingMinutes) / 60.0;
        foreach (var i in items) i.Hours = Math.Max(step, Math.Round(i.Hours / step) * step);
        if (maxHours > 0 && items.Sum(i => i.Hours) > maxHours + 0.001) Normalize(items, maxHours, roundingMinutes);
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
        string? ByRepo(string repo) => ctx.Topics.Where(t => t.Repo != null && t.Ticket.Length > 0 && repo.Contains(t.Repo, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Manual).ThenByDescending(t => t.Hits).FirstOrDefault()?.Ticket;
        bool HasKey(string t) => IssueKeyRegex().Matches(t).Any(m => ctx.Issues.Any(i => i.Key.Equals(m.Value, StringComparison.OrdinalIgnoreCase)));
        string ViaTopic(string text, string repo) => HasKey(text) ? text : $"{text} {ByRepo(repo)}";
        foreach (var s in ctx.Sessions) Add(ViaTopic($"{s.Ticket} {s.Title} {s.Branch}", s.Project), s.Minutes / 15.0);
        foreach (var c in ctx.Commits) Add(ViaTopic(c.Subject, c.Repo), 2);
        foreach (var e in ctx.GitLabEvents) Add($"{e.Ref} {e.CommitTitle} {e.TargetTitle}", 1.5);
        Add(ctx.DayNote, 3);
        foreach (var i in ctx.Issues.Where(i => i.Updated.HasValue && DateOnly.FromDateTime(i.Updated.Value) == ctx.Date))
            scores[i.Key] = scores.GetValueOrDefault(i.Key) + 1;
        // Arbeit ohne bekanntes Ticket: pro Repo als offener Block (Ticket wählt der Nutzer, Copit lernt es)
        var open = new Dictionary<string, (double Score, List<string> Notes)>(StringComparer.OrdinalIgnoreCase);
        void AddOpen(string repo, double w, string note)
        {
            if (string.IsNullOrWhiteSpace(repo) || ByRepo(repo) != null) return;
            var o = open.TryGetValue(repo, out var x) ? x : (0, new List<string>());
            if (o.Item2.Count < 3 && note.Length > 0 && !o.Item2.Contains(note)) o.Item2.Add(note);
            open[repo] = (o.Item1 + w, o.Item2);
        }
        foreach (var s in ctx.Sessions.Where(s => !HasKey($"{s.Ticket} {s.Title} {s.Branch}"))) AddOpen(s.Project, s.Minutes / 15.0, s.Title);
        foreach (var c in ctx.Commits.Where(c => !HasKey(c.Subject))) AddOpen(c.Repo, 2, c.Subject);
        if (scores.Count == 0 && open.Count == 0)
            foreach (var i in ctx.Issues.Where(i => i.Bucket == IssueBucket.InProgress).Take(3)) scores[i.Key] = 1;

        var plan = new BookingPlan { Summary = "Heuristischer Vorschlag (ohne KI) basierend auf Commits und Ticket-Aktivität." };
        foreach (var (key, score) in scores.OrderByDescending(s => s.Value).Take(6))
        {
            var issue = ctx.Issues.First(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var text = comments.TryGetValue(key, out var l) && l.Count > 0 ? string.Join("; ", l) : $"Arbeit an: {issue.Summary}";
            plan.Bookings.Add(new BookingSuggestion { IssueKey = key, IssueSummary = issue.Summary, Hours = score, Comment = text, Reason = $"Aktivitäts-Score {score:0.#}", Confidence = 0.5 });
        }
        foreach (var (repo, o) in open.OrderByDescending(x => x.Value.Score).Take(4))
            plan.Bookings.Add(new BookingSuggestion { IssueKey = "", Hours = o.Score, Comment = string.Join("; ", o.Notes), Reason = "kein Ticket gefunden – bitte zuordnen", Confidence = 0.4, Topic = $"Arbeit in {repo}", Repo = repo });
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
        if (T.HumanEffort) Cap(plan.Bookings, ctx.RemainingHours, T.RoundingMinutes);
        else Normalize(plan.Bookings, ctx.RemainingHours, T.RoundingMinutes);
        await ResolveAccountsAsync(plan.Bookings, ctx.Issues, progress, ct);
        return plan;
    }

    // ------------------------------------------------------------------ Accounts

    public async Task ResolveAccountsAsync(List<BookingSuggestion> items, List<JiraIssue> known, Action<string> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Settings.Jira.AccountFieldId) && !settings.Settings.Jira.UseTempo) return;
        progress("Jira · prüfe Accounts der Tickets");
        var mem = data.Accounts.Value;
        foreach (var b in items.Where(i => i.IssueKey.Length > 0))
        {
            var issue = known.FirstOrDefault(i => i.Key.Equals(b.IssueKey, StringComparison.OrdinalIgnoreCase));
            if (issue == null && hub.Jira != null) issue = await SafeGetIssue(hub.Jira, b.IssueKey, ct);
            if (issue != null && string.IsNullOrWhiteSpace(b.IssueSummary)) b.IssueSummary = issue.Summary;
            if (!string.IsNullOrWhiteSpace(issue?.Account)) { b.Account = issue!.Account; b.AccountFromMemory = false; b.AccountFromIssue = true; continue; }
            b.AccountFromIssue = false;
            if (mem.IssueAccounts.TryGetValue(b.IssueKey, out var a) || mem.ProjectAccounts.TryGetValue(ProjectKey(b.IssueKey), out a))
            {
                b.Account = a; b.AccountFromMemory = true;
            }
            else { b.Account = null; b.AccountFromMemory = false; }
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

    public bool AccountRequired => !LocalMode && (settings.Settings.Jira.UseTempo || !string.IsNullOrWhiteSpace(settings.Settings.Jira.AccountFieldId));

    // ------------------------------------------------------------------ Buchen

    public async Task<List<(BookingSuggestion Item, string? Error)>> BookAsync(DateOnly day, IEnumerable<BookingSuggestion> items,
        IEnumerable<JiraWorklog> existing, Action<string> progress, CancellationToken ct = default)
    {
        var jira = hub.Jira;
        var start = day.ToDateTime(TimeOnly.TryParse(T.DayStart, out var t) ? t : new TimeOnly(8, 0));
        var lastEnd = existing.Select(w => w.Started.AddSeconds(w.TimeSpentSeconds)).DefaultIfEmpty(start).Max();
        if (lastEnd > start && DateOnly.FromDateTime(lastEnd) == day) start = lastEnd;
        var results = new List<(BookingSuggestion, string?)>();
        foreach (var b in items.Where(i => !i.Booked && i.Hours > 0 && (i.IssueKey.Length > 0 || jira == null)))
        {
            progress($"Buche {b.Hours:0.##}h auf {b.IssueKey}{(string.IsNullOrWhiteSpace(b.Account) ? "" : $" · Account {b.Account}")}");
            var seconds = (long)Math.Round(b.Hours * 3600);
            if (AccountRequired && string.IsNullOrWhiteSpace(b.Account))
            {
                results.Add((b, "Kein Account hinterlegt – nicht gebucht."));
                continue;
            }
            if (jira == null)
            {
                // Intern buchen: auch ohne Ticket (Thema statt Key)
                data.LocalWorklogs.Value.Worklogs.Add(new JiraWorklog
                {
                    Id = "local-" + Guid.NewGuid().ToString("N")[..10], IssueKey = b.IssueKey, IssueSummary = b.IssueKey.Length > 0 ? b.IssueSummary : b.Topic ?? b.Repo ?? "Ohne Ticket",
                    Started = start, TimeSpentSeconds = seconds, Comment = b.Comment,
                });
                b.Booked = true;
                if (b.IssueKey.Length > 0) LearnTopic(data.TimeMemory.Value.Topics, b.Topic, b.Repo, b.IssueKey);
                results.Add((b, null));
                start = start.AddSeconds(seconds);
                continue;
            }
            try
            {
                b.BookedAccount = await jira.AddWorklogAsync(b.IssueKey, start, seconds, b.Comment, b.Account, ct);
                b.Booked = true;
                LearnTopic(data.TimeMemory.Value.Topics, b.Topic, b.Repo, b.IssueKey);
                results.Add((b, null));
                start = start.AddSeconds(seconds);
            }
            catch (Exception e) { results.Add((b, e.Message)); }
        }
        await data.TimeMemory.SaveAsync();
        if (jira == null) await data.LocalWorklogs.SaveAsync();
        return results;
    }

    // ------------------------------------------------------------------ Task im Epic

    /// <summary>Account eines Epics: Account-Feld des Epics, sonst gemerkt (Ticket/Projekt).</summary>
    public async Task<(string? Account, string Source)> EpicAccountAsync(string epicKey, CancellationToken ct = default)
    {
        if (hub.Jira is { } j)
        {
            try { if ((await j.GetIssueAsync(epicKey, ct))?.Account is { Length: > 0 } a) return (a, "aus dem Epic"); } catch { }
        }
        var mem = data.Accounts.Value;
        if (mem.IssueAccounts.TryGetValue(epicKey, out var m)) return (m, "gemerkt fürs Epic");
        if (mem.ProjectAccounts.TryGetValue(ProjectKey(epicKey), out m)) return (m, "gemerkt fürs Projekt");
        return (null, "");
    }

    /// <summary>Legt im Epic einen Task an – mit Account – und merkt sich den Account für das neue Ticket.</summary>
    /// <param name="project">Projekt, zu dem der Task gehört (bei geteiltem Epic: Component/Label setzen und zuordnen).</param>
    public async Task<JiraIssue> CreateTaskInEpicAsync(string epicKey, string summary, string description, double estimateHours, string? account, CancellationToken ct = default, CopitProject? project = null)
    {
        var jira = hub.RequireJira;
        epicKey = epicKey.Trim().ToUpperInvariant();
        if (project != null && !string.Equals(project.EpicKey.Trim(), epicKey, StringComparison.OrdinalIgnoreCase)) project = null;
        project ??= data.Projects.Value.Projects.FirstOrDefault(p => string.Equals(p.EpicKey, epicKey, StringComparison.OrdinalIgnoreCase));
        var type = project?.IssueType;
        if (string.IsNullOrWhiteSpace(type)) type = string.IsNullOrWhiteSpace(settings.Settings.Jira.DefaultIssueType) ? "Task" : settings.Settings.Jira.DefaultIssueType;
        var extra = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
        if (project != null) TicketScope.AddCreateFields(project, extra);
        var key = await jira.CreateIssueAsync(ProjectKey(epicKey), summary.Trim(), description, type, estimateHours, epicKey, ct, account, extra);
        if (project != null && TicketScope.NeedsScoping(project, TicketScope.Siblings(data.Projects.Value.Projects, project)))
        {
            data.Projects.Value.TicketAssignments[key.ToUpperInvariant()] = project.Id;
            await data.Projects.SaveAsync();
        }
        if (!string.IsNullOrWhiteSpace(account)) await RememberAccountAsync(key, account, false);
        return await jira.GetIssueAsync(key, ct) ?? new JiraIssue { Key = key, Summary = summary, Account = account };
    }

    // ------------------------------------------------------------------ Gedächtnis

    /// <summary>Merkt sich Thema/Repo → Ticket. weight: 1 = Buchung, 3 = manuelle Korrektur.</summary>
    public static void LearnTopic(List<TopicMapping> map, string? topic, string? repo, string ticket, int weight = 1)
    {
        topic = (topic ?? "").Trim();
        if ((topic.Length == 0 && string.IsNullOrWhiteSpace(repo)) || string.IsNullOrWhiteSpace(ticket)) return;
        var t = topic.Length > 0 ? topic : $"Arbeit in {repo}";
        var hit = map.FirstOrDefault(m => string.Equals(m.Topic.Trim(), t, StringComparison.OrdinalIgnoreCase) && (m.Repo ?? "") == (repo ?? ""));
        if (hit != null)
        {
            if (hit.Ticket != ticket) { if (hit.Manual && weight < 3) return; hit.Ticket = ticket; hit.Hits = weight; }
            else hit.Hits += weight;
            hit.LastUsed = DateTime.Now;
            if (weight >= 3) hit.Manual = true;
        }
        else map.Add(new TopicMapping { Topic = t, Ticket = ticket, Repo = repo, Hits = weight, Manual = weight >= 3 });
        if (map.Count > 200) { var keep = map.OrderByDescending(m => m.LastUsed).Take(200).ToList(); map.Clear(); map.AddRange(keep); }
    }

    /// <summary>Beantwortete Rückfrage als Wissen speichern (gleiche Frage → Antwort ersetzt).</summary>
    public static void LearnFact(List<TimeFact> facts, string question, string answer)
    {
        question = question.Trim(); answer = answer.Trim();
        if (question.Length == 0 || answer.Length == 0) return;
        facts.RemoveAll(f => string.Equals(f.Question.Trim(), question, StringComparison.OrdinalIgnoreCase));
        facts.Add(new TimeFact { Question = question, Answer = answer });
        if (facts.Count > 150) facts.RemoveRange(0, facts.Count - 150);
    }

    public async Task CorrectTicketAsync(BookingSuggestion b, string ticket)
    {
        b.IssueKey = ticket.Trim().ToUpperInvariant();
        LearnTopic(data.TimeMemory.Value.Topics, b.Topic, b.Repo, b.IssueKey, 3);
        await data.TimeMemory.SaveAsync();
    }

    public async Task AnswerAsync(TimeQuestion q, string answer)
    {
        q.Answer = answer;
        LearnFact(data.TimeMemory.Value.Facts, q.Question, answer);
        await data.TimeMemory.SaveAsync();
    }
}
