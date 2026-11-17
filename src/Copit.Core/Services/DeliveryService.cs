using Copit.Core.Models;

namespace Copit.Core.Services;

public enum StepState { None, Ok, Running, Warn, Fail }

/// <summary>Ein Schritt auf dem Weg eines Tickets: Commit → Push → Merge Request → Pipeline → Jira.</summary>
public record DeliveryStep(string Key, string Label, StepState State, string Detail, string? Url = null);

/// <summary>Lieferstatus eines Tickets/Agents: Was ist committet, gepusht, gemergt, läuft die Pipeline, ist das Ticket fertig?</summary>
public record TicketDelivery(
    string Branch,
    IReadOnlyList<DeliveryStep> Steps,
    StepState Overall,
    string Headline,
    GitLabPipeline? Pipeline,
    IReadOnlyList<GitLabJob> Jobs,
    string? GitLabProject,
    DateTime CheckedAt)
{
    public DeliveryStep? Step(string key) => Steps.FirstOrDefault(s => s.Key == key);
    /// <summary>Weiter beobachten, solange etwas läuft.</summary>
    public bool IsPending => Steps.Any(s => s.State == StepState.Running);
}

/// <summary>Ermittelt den Lieferstatus eines Agents aus Git, GitLab und Jira.</summary>
public class DeliveryService(GitService git, ConnectionHub hub)
{
    public async Task<TicketDelivery?> CheckAsync(ChatSession agent, string? repoDir, string? gitLabProject, CancellationToken ct = default)
    {
        var dir = agent.Cwd is { } c && Directory.Exists(c) && GitService.IsRepo(c) ? c : repoDir;
        if (dir == null || !GitService.IsRepo(dir)) return null;

        var steps = new List<DeliveryStep>();
        var branch = agent.Branch;
        var status = await git.GetStatusAsync(dir, ct);
        // Ohne eigenen Branch: aktueller Branch des Ordners
        if (string.IsNullOrWhiteSpace(branch)) branch = status.Branch;
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var checkedOutHere = string.Equals(status.Branch, branch, StringComparison.Ordinal);

        // 1) Lokale Änderungen
        if (checkedOutHere)
        {
            var n = status.Changes.Count;
            steps.Add(n == 0
                ? new DeliveryStep("commit", "Commit", StepState.Ok, "alles committet")
                : new DeliveryStep("commit", "Commit", StepState.Warn, $"{n} Änderung{(n == 1 ? "" : "en")} nicht committet"));
        }

        // 2) Push
        steps.Add(await PushStepAsync(dir, branch, agent.BaseSha, ct));

        // 3) Merge Request + 4) Pipeline (GitLab)
        GitLabPipeline? pipeline = null;
        List<GitLabJob> jobs = [];
        var gl = hub.GitLab;
        gitLabProject ??= GitLabClient.ProjectPathFromRemote(await git.GetRemoteUrlAsync(dir, ct) ?? "");
        if (gl != null && !string.IsNullOrWhiteSpace(gitLabProject))
        {
            try
            {
                var mr = (await gl.GetMergeRequestsAsync(gitLabProject, "opened", branch, ct)).FirstOrDefault()
                         ?? (await gl.GetMergeRequestsAsync(gitLabProject, "merged", branch, ct)).FirstOrDefault();
                steps.Add(mr == null
                    ? new DeliveryStep("mr", "Merge Request", StepState.None, "kein Merge Request")
                    : mr.State == "merged"
                        ? new DeliveryStep("mr", "Merge Request", StepState.Ok, $"!{mr.Iid} gemergt nach {mr.TargetBranch}", mr.WebUrl)
                        : new DeliveryStep("mr", "Merge Request", StepState.Running, $"!{mr.Iid} offen{(mr.Draft ? " (Entwurf)" : "")} → {mr.TargetBranch}", mr.WebUrl));

                pipeline = (await gl.GetPipelinesAsync(gitLabProject, branch, 1, ct)).FirstOrDefault();
                if (pipeline == null) steps.Add(new DeliveryStep("pipeline", "Pipeline", StepState.None, "keine Pipeline"));
                else
                {
                    jobs = await gl.GetJobsAsync(gitLabProject, pipeline.Id, ct);
                    steps.Add(PipelineStep(pipeline, jobs));
                }
            }
            catch (Exception e)
            {
                steps.Add(new DeliveryStep("pipeline", "Pipeline", StepState.Warn, "GitLab nicht erreichbar: " + Short(e.Message)));
            }
        }

        // 5) Jira
        if (!string.IsNullOrWhiteSpace(agent.Ticket) && hub.Jira is { } jira)
        {
            try
            {
                var issue = await jira.GetIssueAsync(agent.Ticket, ct);
                if (issue != null)
                    steps.Add(new DeliveryStep("jira", "Jira", issue.StatusCategory == "done" ? StepState.Ok : issue.StatusCategory == "indeterminate" ? StepState.Running : StepState.None,
                        $"{agent.Ticket}: {issue.Status}", issue.Url));
            }
            catch (Exception e) { steps.Add(new DeliveryStep("jira", "Jira", StepState.Warn, "Jira nicht erreichbar: " + Short(e.Message))); }
        }

        var (overall, headline) = Summarize(steps);
        return new TicketDelivery(branch, steps, overall, headline, pipeline, jobs, gitLabProject, DateTime.Now);
    }

    private async Task<DeliveryStep> PushStepAsync(string dir, string branch, string? baseSha, CancellationToken ct)
    {
        var local = await git.RunAsync(dir, ct, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch);
        if (!local.Ok) return new DeliveryStep("push", "Push", StepState.None, $"Branch {branch} gibt es lokal nicht");
        var remote = await git.RunAsync(dir, ct, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/" + branch);
        if (!remote.Ok)
        {
            var count = baseSha is { Length: > 0 } ? await CountAsync(dir, $"{baseSha}..refs/heads/{branch}", ct) : -1;
            return new DeliveryStep("push", "Push", count == 0 ? StepState.None : StepState.Warn,
                count > 0 ? $"noch nicht gepusht ({count} Commit{(count == 1 ? "" : "s")})" : count == 0 ? "noch keine Commits" : "noch nicht gepusht");
        }
        var ahead = await CountAsync(dir, $"refs/remotes/origin/{branch}..refs/heads/{branch}", ct);
        var behind = await CountAsync(dir, $"refs/heads/{branch}..refs/remotes/origin/{branch}", ct);
        if (ahead > 0) return new DeliveryStep("push", "Push", StepState.Warn, $"{ahead} Commit{(ahead == 1 ? "" : "s")} nicht gepusht");
        return new DeliveryStep("push", "Push", StepState.Ok, behind > 0 ? $"gepusht · {behind} neuer auf origin" : "gepusht");
    }

    private async Task<int> CountAsync(string dir, string range, CancellationToken ct)
    {
        var r = await git.RunAsync(dir, ct, "rev-list", "--count", range);
        return r.Ok && int.TryParse(r.StdOut.Trim(), out var n) ? n : -1;
    }

    public static DeliveryStep PipelineStep(GitLabPipeline p, IReadOnlyList<GitLabJob> jobs)
    {
        var failed = jobs.Where(j => j.Status == "failed" && !j.AllowFailure).Select(j => j.Name).ToList();
        var done = jobs.Count(j => j.Status is "success" or "failed" or "skipped" or "canceled");
        return p.Status switch
        {
            "success" => new("pipeline", "Pipeline", StepState.Ok, $"#{p.Id} erfolgreich ({jobs.Count} Jobs)", p.WebUrl),
            "failed" => new("pipeline", "Pipeline", StepState.Fail, failed.Count > 0 ? $"#{p.Id} fehlgeschlagen: {string.Join(", ", failed)}" : $"#{p.Id} fehlgeschlagen", p.WebUrl),
            "running" or "pending" or "created" or "preparing" or "waiting_for_resource" or "scheduled"
                => new("pipeline", "Pipeline", StepState.Running, $"#{p.Id} läuft ({done}/{jobs.Count} Jobs fertig)", p.WebUrl),
            "manual" => new("pipeline", "Pipeline", StepState.Warn, $"#{p.Id} wartet auf manuellen Start", p.WebUrl),
            _ => new("pipeline", "Pipeline", StepState.Warn, $"#{p.Id} {p.Status}", p.WebUrl),
        };
    }

    /// <summary>Gesamturteil in einem Satz – damit man nicht in Git/GitLab nachsehen muss.</summary>
    public static (StepState, string) Summarize(IReadOnlyList<DeliveryStep> steps)
    {
        DeliveryStep? S(string k) => steps.FirstOrDefault(s => s.Key == k);
        var commit = S("commit"); var push = S("push"); var mr = S("mr"); var pipe = S("pipeline"); var jira = S("jira");
        if (pipe?.State == StepState.Fail) return (StepState.Fail, "Pipeline fehlgeschlagen");
        if (commit?.State == StepState.Warn) return (StepState.Warn, "Nicht committet – " + commit.Detail);
        if (push?.State == StepState.Warn) return (StepState.Warn, "Nicht gepusht – " + push.Detail);
        if (pipe?.State == StepState.Running) return (StepState.Running, "Gepusht · Pipeline läuft");
        var pipeOk = pipe == null || pipe.State is StepState.Ok or StepState.None;
        if (mr?.State == StepState.Ok && pipeOk) return (StepState.Ok, jira?.State == StepState.Ok ? "Fertig – gemergt, Ticket erledigt" : "Fertig – gemergt");
        if (push?.State == StepState.Ok && pipe?.State == StepState.Ok)
            return (StepState.Ok, mr?.State == StepState.Running ? "Gepusht · Pipeline grün · MR offen" : "Gepusht · Pipeline grün");
        if (push?.State == StepState.Ok) return (StepState.Ok, mr?.State == StepState.Running ? "Gepusht · MR offen" : "Gepusht");
        if (push?.State == StepState.None) return (StepState.None, push.Detail);
        return (StepState.None, "Noch nichts geliefert");
    }

    /// <summary>Ende des Job-Logs (für die Anzeige im Ticket).</summary>
    public Task<string> JobLogAsync(string project, long jobId, CancellationToken ct = default) =>
        (hub.GitLab ?? throw new InvalidOperationException("GitLab nicht verbunden")).GetJobTraceAsync(project, jobId, 200, ct);

    public Task RetryPipelineAsync(string project, long pipelineId, CancellationToken ct = default) =>
        (hub.GitLab ?? throw new InvalidOperationException("GitLab nicht verbunden")).RetryPipelineAsync(project, pipelineId, ct);

    private static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;
}
