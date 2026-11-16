using System.Text;
using System.Text.Json.Nodes;
using Copit.Core.Ai;
using Copit.Core.Models;

namespace Copit.Core.Services;

public record TicketDraft(string Summary, string Description, string IssueType, double EstimateHours, string EstimateReason, List<string> Subtasks);

public record ReviewFinding(string Severity, string File, int Line, string Title, string Detail, string Suggestion);

public record ReviewResult(string Summary, string Verdict, List<ReviewFinding> Findings);

/// <summary>KI-Helfer: Ticket-Schätzung, Code-Review, Commit-Messages, Standup, Changelog.</summary>
public class AssistService(AiService ai, GitService git)
{
    public async Task<TicketDraft> DraftTicketAsync(CopitProject project, string idea, List<JiraIssue> epicIssues, AiProvider provider,
        Action<string> progress, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Du hilfst beim Anlegen eines Jira-Tickets und schätzt den Aufwand realistisch (in Stunden).");
        sb.AppendLine($"Projekt: {project.Name}, Epic: {project.EpicKey}");
        if (!string.IsNullOrWhiteSpace(project.Instructions)) sb.AppendLine($"Projekt-Hinweise: {project.Instructions}");
        sb.AppendLine("Bestehende Tickets im Epic (zur Orientierung bei Schätzungen):");
        foreach (var i in epicIssues.Where(i => i.OriginalEstimateSeconds > 0 || i.TimeSpentSeconds > 0).Take(25))
            sb.AppendLine($"- {i.Key} {i.Summary} | Schätzung {i.OriginalEstimateSeconds / 3600.0:0.#}h | gebucht {i.TimeSpentSeconds / 3600.0:0.#}h");
        sb.AppendLine();
        sb.AppendLine("Idee / Beschreibung des Nutzers:");
        sb.AppendLine(idea);
        sb.AppendLine();
        sb.AppendLine("Du darfst dir den Quellcode im aktuellen Ordner ansehen (nur lesen), um die Schätzung zu verbessern.");
        sb.AppendLine("""
            Antworte NUR mit JSON:
            ```json
            {"summary":"Kurzer Ticket-Titel","description":"Jira-Beschreibung mit h3. Kontext, h3. Akzeptanzkriterien (* Liste), h3. Technische Hinweise","issueType":"Task|Story|Bug","estimateHours":4,"estimateReason":"Begründung der Schätzung","subtasks":["optionale Teilschritte"]}
            ```
            """);
        progress("KI analysiert Idee und Code");
        var cwd = Directory.Exists(project.SourceFolder) ? project.SourceFolder : null;
        var text = await ai.AskAsync(sb.ToString(), provider, cwd, progress, ct);
        var n = JsonExtract.Find(text) ?? throw new FormatException("Keine gültige Antwort der KI.");
        return new TicketDraft(n.Str("summary"), n.Str("description"), n.Str("issueType"), JsonExtract.Num(n, "estimateHours"),
            n.Str("estimateReason"), n["subtasks"]?.AsArray().Select(s => s?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? []);
    }

    public async Task<ReviewResult> ReviewAsync(string diff, string context, string? workingDir, AiProvider provider,
        Action<string> progress, CancellationToken ct = default)
    {
        var prompt = $$"""
            Du bist ein erfahrener Senior-Entwickler und machst ein gründliches Code-Review.
            Kontext: {{context}}
            Prüfe auf: Bugs, Sicherheitsprobleme, Performance, Fehlerbehandlung, Lesbarkeit, fehlende Tests.
            Sei konkret, nenne Datei und Zeile. Keine Kleinigkeiten zu Formatierung.
            Antworte NUR mit JSON:
            ```json
            {"summary":"Gesamteindruck in 2 Sätzen","verdict":"approve|comment|request_changes","findings":[{"severity":"critical|major|minor|info","file":"pfad","line":12,"title":"Kurztitel","detail":"Erklärung","suggestion":"Konkreter Verbesserungsvorschlag"}]}
            ```

            DIFF:
            {{diff}}
            """;
        progress("KI liest den Diff");
        var text = await ai.AskAsync(prompt, provider, workingDir, progress, ct);
        var n = JsonExtract.Find(text) ?? throw new FormatException("Keine gültige Review-Antwort.");
        var findings = (n["findings"]?.AsArray() ?? []).Select(f => new ReviewFinding(
            f.Str("severity"), f.Str("file"), (int)JsonExtract.Num(f, "line"), f.Str("title"), f.Str("detail"), f.Str("suggestion"))).ToList();
        return new ReviewResult(n.Str("summary"), n.Str("verdict"), findings);
    }

    public static string ReviewToMarkdown(ReviewResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Copit Code-Review");
        sb.AppendLine(r.Summary).AppendLine();
        foreach (var f in r.Findings)
        {
            var icon = $"`{f.Severity.ToUpperInvariant()}`";
            sb.AppendLine($"{icon} **{f.Title}** — `{f.File}{(f.Line > 0 ? ":" + f.Line : "")}`");
            sb.AppendLine($"  {f.Detail}");
            if (!string.IsNullOrWhiteSpace(f.Suggestion)) sb.AppendLine($"  Vorschlag: {f.Suggestion}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public async Task<string> CommitMessageAsync(string dir, AiProvider provider, string? issueKey, Action<string> progress, CancellationToken ct = default)
    {
        var diff = await git.GetFullDiffAsync(dir, 40_000, ct);
        if (string.IsNullOrWhiteSpace(diff)) throw new InvalidOperationException("Keine Änderungen vorhanden.");
        var prompt = $"""
            Schreibe eine prägnante Git-Commit-Message (Conventional Commits, Englisch oder Deutsch passend zum Repo-Stil).
            Erste Zeile max. 72 Zeichen{(issueKey != null ? $", beginne mit \"{issueKey}: \"" : "")}. Danach optional eine Leerzeile und 1–4 Bullet-Points.
            Antworte NUR mit der Commit-Message, ohne Codeblock.

            {diff}
            """;
        var text = await ai.AskAsync(prompt, provider, dir, progress, ct);
        return text.Trim().Trim('`').Trim();
    }

    public async Task<(string Title, string Description)> MergeRequestTextAsync(string dir, string target, AiProvider provider, Action<string> progress, CancellationToken ct = default)
    {
        var log = await git.GetLogRangeAsync(dir, "origin/" + target, "HEAD", ct);
        var diff = await git.GetBranchDiffAsync(dir, "origin/" + target, 40_000, ct);
        var prompt = $$"""
            Erstelle Titel und Beschreibung für einen GitLab Merge Request.
            Beschreibung in Markdown mit Abschnitten: ## Was, ## Warum, ## Wie testen.
            Antworte NUR mit JSON: {"title":"...","description":"..."}

            Commits:
            {{log}}

            Diff:
            {{diff}}
            """;
        var text = await ai.AskAsync(prompt, provider, dir, progress, ct);
        var n = JsonExtract.Find(text) ?? throw new FormatException("Keine gültige Antwort.");
        return (n.Str("title"), n.Str("description"));
    }

    public async Task<string> ExplainPipelineFailureAsync(string log, string jobName, AiProvider provider, string? dir, Action<string> progress, CancellationToken ct = default)
    {
        var prompt = $"""
            Der GitLab CI Job "{jobName}" ist fehlgeschlagen. Analysiere das Log, nenne die wahrscheinliche Ursache
            in 1–3 Sätzen und den konkreten Fix (Datei/Befehl). Kurz und präzise, Markdown.

            LOG (Ende):
            {log}
            """;
        return await ai.AskAsync(prompt, provider, dir, progress, ct);
    }

    public async Task<string> StandupAsync(DayContext yesterday, IEnumerable<JiraIssue> inProgress, AiProvider provider, Action<string> progress, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Erstelle ein kurzes Daily-Standup auf Deutsch (Markdown) mit den Abschnitten **Gestern**, **Heute**, **Blocker**.");
        sb.AppendLine("Max. 3 Bullet-Points je Abschnitt, mit Ticket-Keys. Blocker nur wenn aus den Daten ersichtlich, sonst 'Keine'.");
        sb.AppendLine($"\nGestern ({yesterday.Date:dd.MM.}):");
        foreach (var w in yesterday.ExistingWorklogs) sb.AppendLine($"- Gebucht {w.IssueKey}: {w.Comment}");
        foreach (var c in yesterday.Commits.Take(30)) sb.AppendLine($"- Commit [{c.Repo}] {c.Subject}");
        foreach (var e in yesterday.GitLabEvents.Take(30)) sb.AppendLine($"- GitLab {e.Action} {e.TargetTitle} {e.CommitTitle}");
        if (!string.IsNullOrWhiteSpace(yesterday.DayNote)) sb.AppendLine($"- Notiz: {yesterday.DayNote}");
        sb.AppendLine("\nTickets in Arbeit:");
        foreach (var i in inProgress.Take(15)) sb.AppendLine($"- {i.Key} [{i.Status}] {i.Summary}");
        return await ai.AskAsync(sb.ToString(), provider, null, progress, ct);
    }

    public async Task<string> ChangelogAsync(string dir, string fromRef, AiProvider provider, Action<string> progress, CancellationToken ct = default)
    {
        var log = await git.GetLogRangeAsync(dir, fromRef, "HEAD", ct);
        if (string.IsNullOrWhiteSpace(log)) throw new InvalidOperationException("Keine Commits im Bereich.");
        var prompt = $"""
            Erstelle aus diesen Commits Release Notes in Markdown, gruppiert nach Features, Fixes, Technisch. Keine Emojis.
            Formuliere verständlich für Product Owner. Ticket-Keys beibehalten.

            {log}
            """;
        return await ai.AskAsync(prompt, provider, dir, progress, ct);
    }
}

