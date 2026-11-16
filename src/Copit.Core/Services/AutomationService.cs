using System.Text.RegularExpressions;
using Copit.Core.Ai;
using Copit.Core.Models;

namespace Copit.Core.Services;

public enum NoticeLevel { Info, Success, Warning, Error }

public record Notice(string Title, string Message, NoticeLevel Level = NoticeLevel.Info, string? Action = null, string? ActionArg = null);

/// <summary>Ereignis, auf das Automationen reagieren (Agent, Git, GitLab, Jira, Zeitplan).</summary>
public record AutomationEvent(string Type, Dictionary<string, string> Vars, ChatSession? Agent = null);

/// <summary>Was der Motor mit Agents tun darf – implementiert vom SessionManager der App.</summary>
public interface IAgentOps
{
    IEnumerable<ChatSession> Agents { get; }
    string? RepoOf(ChatSession agent);
    Task<ChatSession> StartNewAsync(string? repo, string title, string prompt, string? ticket, string? branch, AgentMode mode, AiProvider provider);
    Task ContinueAsync(string agentId, string prompt);
    void SetStatus(string agentId, AgentStatus status);
}

public record ParamDef(string Key, string Label, string Placeholder = "", bool Multiline = false);
public record TriggerDef(string Type, string Label, string Group, ParamDef[] Params);
public record ActionDef(string Type, string Label, string Group, ParamDef[] Params);

/// <summary>Regelbasierte Automationen: WENN Trigger [UND Bedingungen] DANN Aktionen.</summary>
public class AutomationService(DataService data, ConnectionHub hub, TimeBookingService time, AiService ai, GitService git)
{
    public static readonly TriggerDef[] Triggers =
    [
        new("agent.started", "Agent startet", "Agent", []),
        new("agent.finished", "Agent fertig", "Agent", []),
        new("agent.needs_input", "Agent braucht Input", "Agent", []),
        new("agent.failed", "Agent fehlgeschlagen", "Agent", []),
        new("agent.stopped", "Agent gestoppt", "Agent", []),
        new("agent.moved", "Karte verschoben", "Agent", [new("to", "Nach Spalte", "InProgress")]),
        new("git.commit", "Neuer Commit", "Git", []),
        new("git.push", "Push", "Git", []),
        new("git.branch", "Neuer Branch", "Git", []),
        new("pipeline.failed", "Pipeline fehlgeschlagen", "GitLab", []),
        new("pipeline.success", "Pipeline erfolgreich", "GitLab", []),
        new("jira.transitioned", "Ticket-Status geändert", "Jira", [new("status", "Neuer Status", "In Arbeit"), new("jql", "JQL (optional)", "assignee = currentUser()")]),
        new("jira.assigned", "Ticket mir zugewiesen", "Jira", [new("jql", "JQL (optional)", "project = SHOP")]),
        new("schedule", "Zeitplan", "Zeit", [new("time", "Uhrzeit", "09:00"), new("days", "Tage", "Mo,Di,Mi,Do,Fr")]),
    ];

    public static readonly ActionDef[] Actions =
    [
        new("agent.set_status", "Status setzen", "Agent", [new("status", "Status", "Review")]),
        new("agent.start", "Neuen Agent starten", "Agent", [new("title", "Titel", "Fix {{ticket}}"), new("prompt", "Auftrag", "Arbeite an {{ticket}}: {{summary}}", true), new("branch", "Branch (optional)", "feature/{{ticket}}"), new("mode", "Modus", "Edit")]),
        new("agent.continue", "Agent fortsetzen", "Agent", [new("prompt", "Nachricht", "Bitte führe die Tests aus.", true)]),
        new("notify", "Benachrichtigen", "Allgemein", [new("message", "Nachricht", "{{title}} ist fertig")]),
        new("jira.comment", "Jira-Kommentar", "Jira", [new("issue", "Ticket", "{{ticket}}"), new("text", "Text", "Status: {{status}}\n{{summary}}", true)]),
        new("jira.transition", "Jira-Status ändern", "Jira", [new("issue", "Ticket", "{{ticket}}"), new("status", "Zielstatus / Übergang", "In Review")]),
        new("jira.worklog", "Zeit buchen", "Jira", [new("issue", "Ticket", "{{ticket}}"), new("minutes", "Minuten (leer = Agent-Zeit)"), new("comment", "Buchungstext", "{{title}}"), new("account", "Account (leer = vom Ticket / gemerkt)")]),
        new("time.check", "Stunden-Check", "Jira", []),
        new("git.commit", "Commit", "Git", [new("message", "Message", "{{ticket}}: {{title}}")]),
        new("git.push", "Push", "Git", []),
        new("git.branch", "Branch anlegen", "Git", [new("name", "Name", "feature/{{ticket}}")]),
        new("gitlab.mr", "Merge Request", "GitLab", [new("target", "Ziel-Branch (leer = Default)"), new("title", "Titel", "{{ticket}}: {{title}}")]),
        new("ai.prompt", "KI fragen", "Allgemein", [new("prompt", "Prompt", "Fasse die Änderungen in {{repo}} zusammen.", true)]),
        new("shell", "Befehl ausführen", "Allgemein", [new("command", "Befehl", "npm test")]),
    ];

    public static readonly string[] Fields = ["ticket", "title", "branch", "status", "project", "provider", "summary", "message", "author", "repo"];
    public static readonly string[] Ops = ["contains", "equals", "not_equals", "matches"];

    public IAgentOps? Agents { get; set; }
    public event Action<Notice>? Notified;
    public event Action? RunsChanged;

    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, string> _heads = [], _upstreams = [], _pipelines = [], _jiraStatus = [];
    private readonly Dictionary<string, HashSet<string>> _branches = [];
    private HashSet<string>? _jiraAssigned;
    private readonly HashSet<string> _scheduleDone = [];
    private int _depth;

    public List<Automation> All => data.Automations.Value.Automations;

    public void SeedDefaults()
    {
        if (data.Automations.Value.Seeded && data.Automations.Value.Automations.All(a => !string.IsNullOrEmpty(a.Trigger))) return;
        data.Automations.Value.Automations.RemoveAll(a => string.IsNullOrEmpty(a.Trigger));
        Automation Mk(string name, string trigger, RuleAction[] acts, bool enabled = true, Dictionary<string, string>? p = null) =>
            new() { Name = name, Trigger = trigger, Actions = [.. acts], Enabled = enabled, TriggerParams = p ?? [] };
        RuleAction A(string type, params (string K, string V)[] ps) => new() { Type = type, Params = ps.ToDictionary(x => x.K, x => x.V) };
        data.Automations.Value.Automations.AddRange(
        [
            Mk("Braucht Input → benachrichtigen", "agent.needs_input", [A("notify", ("message", "{{title}} braucht deinen Input"))]),
            Mk("Fehlgeschlagen → benachrichtigen", "agent.failed", [A("notify", ("message", "{{title}} ist fehlgeschlagen"))]),
            Mk("Karte auf In Progress → umsetzen", "agent.moved", [A("agent.continue", ("prompt", "Setze den besprochenen Plan jetzt um."))], true, new() { ["to"] = "InProgress" }),
            Mk("Feierabend-Check: Stunden gebucht?", "schedule", [A("time.check")], true, new() { ["time"] = "16:30", ["days"] = "Mo,Di,Mi,Do,Fr" }),
            Mk("Pipeline rot → benachrichtigen", "pipeline.failed", [A("notify", ("message", "Pipeline auf {{branch}} fehlgeschlagen"))]),
            Mk("Fertig → Jira-Kommentar", "agent.finished", [A("jira.comment", ("issue", "{{ticket}}"), ("text", "Copit-Agent fertig ({{lines}} in {{files}} Dateien):\n{{summary}}"))], false),
            Mk("Fertig → Jira In Review", "agent.finished", [A("jira.transition", ("issue", "{{ticket}}"), ("status", "In Review"))], false),
            Mk("Fertig → Agent-Zeit buchen", "agent.finished", [A("jira.worklog", ("issue", "{{ticket}}"), ("minutes", ""), ("comment", "{{title}}"))], false),
            Mk("Pipeline rot → Fix-Agent", "pipeline.failed", [A("agent.start", ("title", "Pipeline fixen {{branch}}"), ("prompt", "Die GitLab-Pipeline auf {{branch}} ist fehlgeschlagen ({{pipelineUrl}}). Finde die Ursache und behebe sie."), ("mode", "Edit"))], false),
            Mk("Ticket In Arbeit → Agent anlegen", "jira.transitioned", [A("agent.start", ("title", "{{ticket}} {{issueSummary}}"), ("prompt", "Analysiere Ticket {{ticket}} ({{issueSummary}}) und erstelle einen Umsetzungsplan."), ("mode", "Plan"), ("branch", "feature/{{ticket}}"))], false, new() { ["status"] = "In Arbeit" }),
        ]);
        data.Automations.Value.Seeded = true;
        data.Automations.Save();
    }

    public static string Render(string? tpl, IReadOnlyDictionary<string, string> vars) =>
        Regex.Replace(tpl ?? "", @"\{\{\s*([\w.]+)\s*\}\}", m => vars.GetValueOrDefault(m.Groups[1].Value, ""));

    public static bool Matches(RuleCondition c, IReadOnlyDictionary<string, string> vars)
    {
        var raw = vars.GetValueOrDefault(c.Field, "");
        var v = raw.ToLowerInvariant(); var x = (c.Value ?? "").ToLowerInvariant();
        return c.Op switch
        {
            "equals" => v == x,
            "not_equals" => v != x,
            "contains" => v.Contains(x),
            "matches" => SafeRegex(c.Value ?? "", raw),
            _ => false,
        };
    }

    private static bool SafeRegex(string pattern, string input) { try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase); } catch { return false; } }

    public static bool TriggerMatches(Automation a, AutomationEvent e)
    {
        if (!a.Enabled || a.Trigger != e.Type) return false;
        var p = a.TriggerParams;
        if (e.Type == "agent.moved" && p.GetValueOrDefault("to") is { Length: > 0 } to && !string.Equals(to, e.Vars.GetValueOrDefault("to"), StringComparison.OrdinalIgnoreCase)) return false;
        if (e.Type == "jira.transitioned" && p.GetValueOrDefault("status") is { Length: > 0 } st && !string.Equals(st, e.Vars.GetValueOrDefault("status"), StringComparison.OrdinalIgnoreCase)) return false;
        if (e.Vars.GetValueOrDefault("automationId") is { Length: > 0 } id && id != a.Id) return false;
        return a.Conditions.All(c => Matches(c, e.Vars));
    }

    public static Dictionary<string, string> AgentVars(ChatSession? a) => a == null ? [] : new()
    {
        ["agentId"] = a.Id, ["title"] = a.Title, ["ticket"] = a.Ticket ?? "", ["branch"] = a.Branch ?? "", ["status"] = AgentStatusInfo.Title(a.Status),
        ["summary"] = a.Summary ?? "", ["provider"] = a.Provider.ToString(), ["cost"] = a.TotalCostUsd.ToString("0.00"),
        ["agentMinutes"] = (a.AgentMs / 60000).ToString(), ["aiMinutes"] = (a.AiMs / 60000).ToString(),
        ["lines"] = $"+{a.LinesAdded} -{a.LinesRemoved}", ["files"] = a.FilesChanged.ToString(), ["repo"] = a.Cwd ?? "",
    };

    private IEnumerable<string> Repos() =>
        data.Projects.Value.Projects.Select(p => p.SourceFolder).Where(GitService.IsRepo).Distinct(StringComparer.OrdinalIgnoreCase);

    public async Task DispatchAsync(AutomationEvent e)
    {
        if (_depth > 3) return; // Endlosschleifen verhindern
        var vars = AgentVars(e.Agent);
        foreach (var (k, v) in e.Vars) vars[k] = v;
        if (e.Agent != null && Agents?.RepoOf(e.Agent) is { } r && !vars.ContainsKey("project")) vars["project"] = ActivityService.ProjectName(r);
        var ev = e with { Vars = vars };
        foreach (var a in All.Where(x => TriggerMatches(x, ev)).ToList())
        {
            _depth++;
            var run = new AutomationRun { AutomationId = a.Id, AutomationName = a.Name };
            try
            {
                var results = new List<string>();
                foreach (var act in a.Actions) results.Add(await RunActionAsync(act, vars, e.Agent));
                run.Output = string.Join(" · ", results.Where(x => x.Length > 0)) is { Length: > 0 } o ? o : "ok";
            }
            catch (Exception ex)
            {
                run.Success = false;
                run.Output = "Fehler: " + ex.Message;
                Notified?.Invoke(new Notice($"Automation \"{a.Name}\"", ex.Message, NoticeLevel.Error));
            }
            finally
            {
                _depth--;
                a.LastRun = DateTime.Now;
                a.LastResult = run.Output.Length > 4000 ? run.Output[..4000] : run.Output;
                a.RunCount++;
                data.Automations.Value.Runs.Insert(0, run);
                if (data.Automations.Value.Runs.Count > 200) data.Automations.Value.Runs.RemoveRange(200, data.Automations.Value.Runs.Count - 200);
                await data.Automations.SaveAsync();
                RunsChanged?.Invoke();
            }
        }
    }

    /// <summary>Testlauf aus dem Builder.</summary>
    public Task TestAsync(Automation a, ChatSession? agent)
    {
        var vars = AgentVars(agent);
        vars["ticket"] = agent?.Ticket ?? "TEST-1";
        vars["automationId"] = a.Id;
        vars.TryAdd("repo", Repos().FirstOrDefault() ?? "");
        foreach (var (k, v) in a.TriggerParams) vars[k] = v;
        var wasEnabled = a.Enabled;
        a.Enabled = true;
        return DispatchAsync(new AutomationEvent(a.Trigger, vars, agent)).ContinueWith(_ => a.Enabled = wasEnabled);
    }

    private async Task<string> RunActionAsync(RuleAction act, Dictionary<string, string> vars, ChatSession? agent)
    {
        var p = act.Params.ToDictionary(x => x.Key, x => Render(x.Value, vars));
        string P(string k) => p.GetValueOrDefault(k, "");
        var cwd = agent?.Cwd ?? (vars.GetValueOrDefault("repo") is { Length: > 0 } rp ? rp : Repos().FirstOrDefault());
        JiraClient Jira() => hub.Jira ?? throw new InvalidOperationException("Jira nicht konfiguriert");
        string Issue() => P("issue") is { Length: > 0 } k ? k : vars.GetValueOrDefault("ticket") is { Length: > 0 } t ? t : throw new InvalidOperationException("Kein Ticket");
        string Cwd() => cwd ?? throw new InvalidOperationException("Kein Repository");
        switch (act.Type)
        {
            case "agent.set_status":
                if (agent != null && Enum.TryParse<AgentStatus>(P("status").Replace(" ", "").Replace("_", ""), true, out var st)) Agents?.SetStatus(agent.Id, st);
                return $"Status {P("status")}";
            case "agent.start":
            {
                if (Agents == null) throw new InvalidOperationException("Agents nicht verfügbar");
                var mode = Enum.TryParse<AgentMode>(P("mode"), true, out var m) ? m : AgentMode.Edit;
                var a = await Agents.StartNewAsync(agent != null ? Agents.RepoOf(agent) : cwd, P("title"), P("prompt"), vars.GetValueOrDefault("ticket"),
                    P("branch") is { Length: > 0 } b ? b : null, mode, agent?.Provider ?? AiProvider.Claude);
                return $"Agent {a.Title}";
            }
            case "agent.continue":
                if (agent != null && Agents != null) _ = Agents.ContinueAsync(agent.Id, P("prompt"));
                return "fortgesetzt";
            case "notify":
                Notified?.Invoke(new Notice(P("message"), vars.GetValueOrDefault("title", ""), NoticeLevel.Info, agent != null ? "agent" : null, agent?.Id));
                return "";
            case "jira.comment":
                await Jira().AddCommentAsync(Issue(), P("text"));
                return $"Kommentar {Issue()}";
            case "jira.transition":
                await Jira().TransitionAsync(Issue(), P("status"));
                return $"{Issue()} → {P("status")}";
            case "jira.worklog":
            {
                var sec = double.TryParse(P("minutes"), out var min) ? (long)(min * 60) : (agent?.AgentMs ?? 0) / 1000;
                if (sec < 60) throw new InvalidOperationException("Weniger als 1 Minute – nicht gebucht");
                var account = P("account") is { Length: > 0 } acc ? acc
                    : (await Jira().GetIssueAsync(Issue()))?.Account
                      ?? data.Accounts.Value.IssueAccounts.GetValueOrDefault(Issue())
                      ?? data.Accounts.Value.ProjectAccounts.GetValueOrDefault(TimeBookingService.ProjectKey(Issue()));
                var booked = await Jira().AddWorklogAsync(Issue(), DateTime.Now.AddSeconds(-sec), sec, P("comment"), account);
                return $"{sec / 60} min auf {Issue()}{(booked != null ? $" · Account {booked}" : "")}";
            }
            case "time.check":
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var target = time.TargetFor(today);
                if (target <= 0 || hub.Jira == null) return "kein Soll / Jira";
                var booked = (await time.GetBookedHoursAsync(today, today)).GetValueOrDefault(today);
                if (booked + 0.01 >= target) return $"Alles gebucht ({booked:0.##}h)";
                Notified?.Invoke(new Notice("Stunden fehlen", $"Heute sind erst {booked:0.##}h von {target:0.##}h gebucht.", NoticeLevel.Warning, "book", today.ToString("yyyy-MM-dd")));
                return $"{booked:0.##}/{target:0.##}h";
            }
            case "git.commit":
                await git.CommitAllAsync(Cwd(), P("message") is { Length: > 0 } msg ? msg : "Copit-Commit");
                return "Commit";
            case "git.push":
                await git.PushAsync(Cwd());
                return "Push";
            case "git.branch":
                await git.CreateBranchAsync(Cwd(), P("name"));
                return $"Branch {P("name")}";
            case "gitlab.mr":
            {
                var gl = hub.GitLab ?? throw new InvalidOperationException("GitLab nicht konfiguriert");
                var project = GitLabClient.ProjectPathFromRemote(await git.GetRemoteUrlAsync(Cwd()) ?? "") ?? throw new InvalidOperationException("GitLab-Projekt nicht erkannt");
                var status = await git.GetStatusAsync(Cwd());
                try { await git.PushAsync(Cwd()); } catch { /* evtl. schon gepusht */ }
                var target = P("target") is { Length: > 0 } t ? t : (await gl.GetProjectAsync(project)).DefaultBranch;
                var mr = await gl.CreateMergeRequestAsync(project, status.Branch, target, P("title"), vars.GetValueOrDefault("summary", ""));
                return $"MR !{mr.Iid}";
            }
            case "ai.prompt":
            {
                var text = await ai.AskAsync(P("prompt"), AiProvider.Claude, cwd, null, CancellationToken.None);
                Notified?.Invoke(new Notice("KI-Antwort", text.Length > 160 ? text[..160] + "…" : text, NoticeLevel.Success));
                return text.Length > 300 ? text[..300] : text;
            }
            case "shell":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", P("command")], cwd, ct: cts.Token);
                var err = (r.StdErr + r.StdOut).Trim();
                if (!r.Ok) throw new InvalidOperationException(err.Length > 300 ? err[..300] : err);
                var o = r.StdOut.Trim();
                return o.Length > 200 ? o[..200] : o;
            }
        }
        return "";
    }

    // ------------------------------------------------------------ Watcher

    public void Start()
    {
        if (_cts != null) return;
        SeedDefaults();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private bool Wants(string t) => All.Any(a => a.Enabled && a.Trigger == t);

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(6), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); } catch { /* weiter */ }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { break; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var repos = new HashSet<string>(Repos(), StringComparer.OrdinalIgnoreCase);
        foreach (var a in Agents?.Agents ?? []) if (a.Worktree && a.Cwd != null && Directory.Exists(a.Cwd)) repos.Add(a.Cwd);
        if (new[] { "git.commit", "git.push", "git.branch", "pipeline.failed", "pipeline.success" }.Any(Wants))
            foreach (var r in repos) await PollGitAsync(r, ct);
        if (Wants("jira.transitioned") || Wants("jira.assigned")) await PollJiraAsync(ct);
        await PollScheduleAsync();
    }

    private ChatSession? AgentFor(string cwd) =>
        Agents?.Agents.FirstOrDefault(a => a.Cwd != null && string.Equals(a.Cwd.TrimEnd('\\'), cwd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    private async Task PollGitAsync(string cwd, CancellationToken ct)
    {
        var agent = AgentFor(cwd);
        Dictionary<string, string> Base(params (string, string)[] extra)
        {
            var d = new Dictionary<string, string> { ["repo"] = cwd };
            foreach (var (k, v) in extra) d[k] = v;
            return d;
        }
        var status = await git.GetStatusAsync(cwd, ct);
        var branch = status.Branch;
        var head = (await git.RunAsync(cwd, ct, "rev-parse", "HEAD")).StdOut.Trim();
        if (head.Length > 0)
        {
            if (_heads.TryGetValue(cwd, out var prev) && prev != head)
            {
                var c = (await git.GetRecentCommitsAsync(cwd, 1, ct)).FirstOrDefault();
                await DispatchAsync(new("git.commit", Base(("branch", branch), ("sha", head), ("message", c?.Subject ?? ""), ("author", c?.Author ?? "")), agent));
            }
            _heads[cwd] = head;
        }
        var up = await git.RunAsync(cwd, ct, "rev-parse", "@{u}");
        if (up.Ok)
        {
            var key = cwd + "|" + branch;
            if (_upstreams.TryGetValue(key, out var prev) && prev != up.StdOut.Trim()) await DispatchAsync(new("git.push", Base(("branch", branch)), agent));
            _upstreams[key] = up.StdOut.Trim();
        }
        var list = (await git.GetBranchesAsync(cwd, ct)).ToHashSet();
        if (_branches.TryGetValue(cwd, out var known))
            foreach (var b in list.Where(b => !known.Contains(b))) await DispatchAsync(new("git.branch", Base(("branch", b)), agent));
        _branches[cwd] = list;

        if (hub.GitLab is { } gl && (Wants("pipeline.failed") || Wants("pipeline.success")))
        {
            var project = GitLabClient.ProjectPathFromRemote(await git.GetRemoteUrlAsync(cwd, ct) ?? "");
            if (project == null || branch == "") return;
            GitLabPipeline? p;
            try { p = (await gl.GetPipelinesAsync(project, branch, 1, ct)).FirstOrDefault(); } catch { return; }
            if (p == null) return;
            var key = cwd + "|" + branch;
            var state = $"{p.Id}:{p.Status}";
            if (_pipelines.TryGetValue(key, out var prev) && prev != state && p.Status is "failed" or "success")
                await DispatchAsync(new(p.Status == "failed" ? "pipeline.failed" : "pipeline.success", Base(("branch", branch), ("pipelineUrl", p.WebUrl), ("pipelineId", p.Id.ToString())), agent));
            _pipelines[key] = state;
        }
    }

    private async Task PollJiraAsync(CancellationToken ct)
    {
        if (hub.Jira is not { } j) return;
        var repo = Repos().FirstOrDefault() ?? "";
        var autos = All.Where(a => a.Enabled && a.Trigger.StartsWith("jira.")).ToList();
        foreach (var jql in autos.Select(a => a.TriggerParams.GetValueOrDefault("jql") is { Length: > 0 } q ? q : "assignee = currentUser()").Distinct())
        {
            List<JiraIssue> issues;
            try { issues = await j.SearchAsync($"({jql}) AND updated >= -1d ORDER BY updated DESC", 50, ct); } catch { continue; }
            foreach (var i in issues)
            {
                if (_jiraStatus.TryGetValue(i.Key, out var prev) && prev != i.Status)
                    await DispatchAsync(new("jira.transitioned", new() { ["ticket"] = i.Key, ["status"] = i.Status, ["from"] = prev, ["issueSummary"] = i.Summary, ["repo"] = repo }));
                _jiraStatus[i.Key] = i.Status;
            }
        }
        if (autos.Any(a => a.Trigger == "jira.assigned"))
        {
            List<JiraIssue> mine;
            try { mine = await j.SearchAsync("assignee = currentUser() AND statusCategory != Done ORDER BY created DESC", 50, ct); } catch { return; }
            if (_jiraAssigned != null)
                foreach (var i in mine.Where(i => !_jiraAssigned.Contains(i.Key)))
                    await DispatchAsync(new("jira.assigned", new() { ["ticket"] = i.Key, ["status"] = i.Status, ["issueSummary"] = i.Summary, ["repo"] = repo }));
            _jiraAssigned = mine.Select(i => i.Key).ToHashSet();
        }
    }

    private static readonly string[] DayNames = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    public static bool IsDue(Automation a, DateTime now)
    {
        if (a.Trigger != "schedule") return false;
        var days = (a.TriggerParams.GetValueOrDefault("days") is { Length: > 0 } d ? d : "Mo,Di,Mi,Do,Fr").Split(',').Select(s => s.Trim());
        if (!days.Contains(DayNames[(int)now.DayOfWeek])) return false;
        if (!TimeOnly.TryParse(a.TriggerParams.GetValueOrDefault("time") is { Length: > 0 } t ? t : "09:00", out var time)) return false;
        var due = now.Date + time.ToTimeSpan();
        return now >= due && now - due < TimeSpan.FromHours(2) && (a.LastRun == null || a.LastRun.Value < due);
    }

    private async Task PollScheduleAsync()
    {
        var now = DateTime.Now;
        foreach (var a in All.Where(x => x.Enabled && IsDue(x, now)).ToList())
        {
            if (!_scheduleDone.Add($"{a.Id}|{now:yyyy-MM-dd}")) continue;
            await DispatchAsync(new("schedule", new() { ["repo"] = Repos().FirstOrDefault() ?? "", ["automationId"] = a.Id }));
        }
    }
}
