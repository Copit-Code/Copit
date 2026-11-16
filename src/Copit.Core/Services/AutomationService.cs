using Copit.Core.Ai;
using Copit.Core.Models;

namespace Copit.Core.Services;

public enum NoticeLevel { Info, Success, Warning, Error }

public record Notice(string Title, string Message, NoticeLevel Level = NoticeLevel.Info, string? Action = null, string? ActionArg = null);

/// <summary>Führt Automationen im Hintergrund aus (Reminder, Buchungs-Check, Pipeline-Wächter, KI-Prompts, Standup).</summary>
public class AutomationService(DataService data, ConnectionHub hub, TimeBookingService time, AiService ai, AssistService assist,
    GitService git, SessionStore sessions)
{
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _running = [];
    private readonly Dictionary<string, string> _pipelineState = [];

    public event Action<Notice>? Notified;
    public event Action? RunsChanged;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private async Task LoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            foreach (var a in data.Automations.Value.Automations.Where(a => a.Enabled).ToList())
            {
                if (_running.Contains(a.Id) || !IsDue(a, DateTime.Now)) continue;
                _ = RunAsync(a, ct);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { break; }
        }
    }

    public static bool IsDue(Automation a, DateTime now)
    {
        if (a.Kind == AutomationKind.PipelineWatch)
            return a.LastRun == null || now - a.LastRun.Value >= TimeSpan.FromMinutes(Math.Max(1, a.IntervalMinutes));
        if (!a.Days.Contains(now.DayOfWeek)) return false;
        if (!TimeOnly.TryParse(a.TimeOfDay, out var t)) return false;
        var dueToday = now.Date + t.ToTimeSpan();
        return now >= dueToday && now - dueToday < TimeSpan.FromHours(2) && (a.LastRun == null || a.LastRun.Value < dueToday);
    }

    public async Task RunAsync(Automation a, CancellationToken ct = default)
    {
        if (!_running.Add(a.Id)) return;
        var run = new AutomationRun { AutomationId = a.Id, AutomationName = a.Name };
        try
        {
            run.Output = a.Kind switch
            {
                AutomationKind.Reminder => Remind(a),
                AutomationKind.BookingCheck => await BookingCheckAsync(ct),
                AutomationKind.PipelineWatch => await PipelineWatchAsync(ct),
                AutomationKind.AiPrompt => await AiPromptAsync(a, ct),
                AutomationKind.DailyStandup => await StandupAsync(a, ct),
                _ => ""
            };
        }
        catch (Exception e)
        {
            run.Success = false;
            run.Output = e.Message;
            if (a.Kind != AutomationKind.PipelineWatch)
                Notified?.Invoke(new Notice($"Automation fehlgeschlagen: {a.Name}", e.Message, NoticeLevel.Error));
        }
        finally
        {
            a.LastRun = DateTime.Now;
            a.LastResult = run.Output.Length > 4000 ? run.Output[..4000] : run.Output;
            _running.Remove(a.Id);
            if (a.Kind != AutomationKind.PipelineWatch || run.Output.Length > 0)
            {
                data.Automations.Value.Runs.Insert(0, run);
                if (data.Automations.Value.Runs.Count > 200) data.Automations.Value.Runs.RemoveRange(200, data.Automations.Value.Runs.Count - 200);
            }
            await data.Automations.SaveAsync();
            RunsChanged?.Invoke();
        }
    }

    public bool IsRunning(string id) => _running.Contains(id);

    private string Remind(Automation a)
    {
        Notified?.Invoke(new Notice(a.Name, a.Prompt, NoticeLevel.Info));
        return a.Prompt;
    }

    private async Task<string> BookingCheckAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var target = time.TargetFor(today);
        if (target <= 0 || hub.Jira == null) return "";
        var booked = (await time.GetBookedHoursAsync(today, today, ct)).GetValueOrDefault(today);
        if (booked + 0.01 >= target) return $"Alles gebucht ({booked:0.##}h).";
        var msg = $"Heute sind erst {booked:0.##}h von {target:0.##}h gebucht.";
        Notified?.Invoke(new Notice("Stunden fehlen", msg, NoticeLevel.Warning, "book", today.ToString("yyyy-MM-dd")));
        return msg;
    }

    private async Task<string> PipelineWatchAsync(CancellationToken ct)
    {
        var gl = hub.GitLab;
        if (gl == null) return "";
        var changes = new List<string>();
        foreach (var p in data.Projects.Value.Projects.Where(p => !string.IsNullOrWhiteSpace(p.GitLabProject) && GitService.IsRepo(p.SourceFolder)))
        {
            var status = await git.GetStatusAsync(p.SourceFolder, ct);
            if (string.IsNullOrEmpty(status.Branch)) continue;
            var pipe = (await gl.GetPipelinesAsync(p.GitLabProject, status.Branch, 1, ct)).FirstOrDefault();
            if (pipe == null) continue;
            var key = $"{p.Id}:{status.Branch}";
            var state = $"{pipe.Id}:{pipe.Status}";
            if (_pipelineState.TryGetValue(key, out var old) && old != state)
            {
                if (pipe.Status == "failed")
                {
                    Notified?.Invoke(new Notice($"Pipeline fehlgeschlagen · {p.Name}", $"{status.Branch} · #{pipe.Id}", NoticeLevel.Error, "project", p.Id));
                    changes.Add($"{p.Name}/{status.Branch}: failed");
                }
                else if (pipe.Status == "success")
                {
                    Notified?.Invoke(new Notice($"Pipeline grün · {p.Name}", $"{status.Branch} · #{pipe.Id}", NoticeLevel.Success, "project", p.Id));
                    changes.Add($"{p.Name}/{status.Branch}: success");
                }
            }
            _pipelineState[key] = state;
        }
        return string.Join("\n", changes);
    }

    private async Task<string> AiPromptAsync(Automation a, CancellationToken ct)
    {
        var p = data.Project(a.ProjectId);
        var text = await ai.AskAsync(a.Prompt, a.Provider, p?.SourceFolder, null, ct);
        Notified?.Invoke(new Notice($"{a.Name}", text.Length > 160 ? text[..160] + "…" : text, NoticeLevel.Success, "automation", a.Id));
        return text;
    }

    private async Task<string> StandupAsync(Automation a, CancellationToken ct)
    {
        var y = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        while (!time.IsWorkDay(y)) y = y.AddDays(-1);
        var ctx = await time.GatherAsync(y, time.TargetFor(y), sessions.DescribeDay(y, data), _ => { }, ct);
        var inProgress = hub.Jira != null ? await hub.Jira.SearchAsync("assignee = currentUser() AND statusCategory = \"In Progress\"", 20, ct) : [];
        var text = await assist.StandupAsync(ctx, inProgress, a.Provider, _ => { }, ct);
        Notified?.Invoke(new Notice("Standup ist bereit", "In Tools → Automationen ansehen", NoticeLevel.Success, "automation", a.Id));
        return text;
    }
}

