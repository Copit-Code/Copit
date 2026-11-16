using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Services;

/// <summary>Hält alle Agents (Sessions). Läufe laufen weiter, auch wenn die Ansicht gewechselt wird.</summary>
public class SessionManager : IAgentOps
{
    private readonly Dictionary<string, SessionHost> _hosts = [];
    private readonly SessionStore _store;
    private readonly AgentRunnerFactory _runners;
    private readonly SettingsService _settings;
    private readonly DataService _data;
    public PermissionService Permissions { get; }
    public AutomationService Automations { get; }
    public ActivityService Activity { get; }
    public GitService Git { get; }

    public event Action? RunningChanged;
    /// <summary>Status, Zeiten oder Liste der Agents haben sich geändert (Board neu zeichnen).</summary>
    public event Action? AgentsChanged;

    public SessionManager(SessionStore store, AgentRunnerFactory runners, SettingsService settings, DataService data,
        PermissionService permissions, AutomationService automations, ActivityService activity, GitService git)
    {
        _store = store; _runners = runners; _settings = settings; _data = data;
        Permissions = permissions; Automations = automations; Activity = activity; Git = git;
        automations.Agents = this;
        // Läufe, die beim Beenden noch liefen, als unterbrochen markieren
        foreach (var s in store.All.Where(s => s.Status is AgentStatus.InProgress or AgentStatus.WaitingSubagent))
        {
            s.Status = string.IsNullOrEmpty(s.PendingPrompt) ? AgentStatus.Review : AgentStatus.NeedInput;
            store.Save(s);
        }
    }

    public int RunningCount => _hosts.Values.Count(h => h.IsRunning);

    public IEnumerable<ChatSession> Agents => _store.All.Where(s => !s.Archived);

    public SessionHost Host(ChatSession s)
    {
        if (_hosts.TryGetValue(s.Id, out var h)) return h;
        h = new SessionHost(s, this, _store, _runners, _settings, _data);
        h.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionHost.IsRunning)) RunningChanged?.Invoke();
            if (e.PropertyName is nameof(SessionHost.IsRunning) or nameof(SessionHost.BoardStatus)) RaiseAgentsChanged();
        };
        return _hosts[s.Id] = h;
    }

    public void RaiseAgentsChanged() => MainThread.BeginInvokeOnMainThread(() => AgentsChanged?.Invoke());

    public SessionHost Create(CopitProject project, AiProvider? provider = null)
    {
        var s = _store.Create(project, provider ?? project.Provider ?? _settings.Settings.Ai.DefaultProvider, AgentMode.Edit);
        RaiseAgentsChanged();
        return Host(s);
    }

    /// <summary>Neuer Agent mit Titel, Ticket, Branch (optional eigener Worktree).</summary>
    public async Task<SessionHost> CreateAgentAsync(CopitProject project, string title, string? ticket, string? branch, AgentMode mode,
        AiProvider provider, string model = "", bool? worktree = null)
    {
        var s = _store.Create(project, provider, mode);
        s.Title = string.IsNullOrWhiteSpace(title) ? "Neuer Agent" : title.Trim();
        s.Ticket = string.IsNullOrWhiteSpace(ticket) ? null : ticket.Trim().ToUpperInvariant();
        s.Model = model;
        s.Cwd = project.SourceFolder;
        if (!string.IsNullOrWhiteSpace(branch) && GitService.IsRepo(project.SourceFolder))
        {
            branch = branch.Trim();
            s.Branch = branch;
            var useWt = worktree ?? _settings.Settings.Ai.UseWorktrees;
            var exists = (await Git.GetBranchesAsync(project.SourceFolder)).Contains(branch);
            if (useWt)
            {
                var safe = string.Concat(branch.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
                var dir = Path.Combine(Core.Storage.AppPaths.Dir("worktrees"), $"{Path.GetFileName(project.SourceFolder.TrimEnd('\\'))}-{safe}-{s.Id[..6]}");
                var r = exists
                    ? await Git.RunAsync(project.SourceFolder, default, "worktree", "add", dir, branch)
                    : await Git.RunAsync(project.SourceFolder, default, "worktree", "add", "-b", branch, dir, "HEAD");
                if (r.Ok) { s.Cwd = dir; s.Worktree = true; }
                else s.Error = "Worktree: " + (r.StdErr + r.StdOut).Trim();
            }
            else
            {
                var r = exists ? await Git.RunAsync(project.SourceFolder, default, "checkout", branch)
                               : await Git.RunAsync(project.SourceFolder, default, "checkout", "-b", branch);
                if (!r.Ok) s.Error = "Branch: " + (r.StdErr + r.StdOut).Trim();
            }
        }
        _store.Save(s);
        RaiseAgentsChanged();
        return Host(s);
    }

    public string? RepoOf(ChatSession agent) => _data.Project(agent.ProjectId)?.SourceFolder ?? agent.Cwd;

    public async Task<ChatSession> StartNewAsync(string? repo, string title, string prompt, string? ticket, string? branch, AgentMode mode, AiProvider provider)
    {
        var project = _data.Projects.Value.Projects.FirstOrDefault(p => repo != null && string.Equals(p.SourceFolder.TrimEnd('\\'), repo.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                      ?? _data.Projects.Value.Projects.FirstOrDefault()
                      ?? throw new InvalidOperationException("Kein Projekt angelegt");
        var h = await MainThread.InvokeOnMainThreadAsync(() => CreateAgentAsync(project, title, ticket, branch, mode, provider));
        if (!string.IsNullOrWhiteSpace(prompt)) MainThread.BeginInvokeOnMainThread(() => _ = h.SendAsync(prompt));
        return h.Session;
    }

    public Task ContinueAsync(string agentId, string prompt)
    {
        var s = _store.Get(agentId);
        if (s == null) return Task.CompletedTask;
        MainThread.BeginInvokeOnMainThread(() => _ = Host(s).SendAsync(prompt));
        return Task.CompletedTask;
    }

    public void SetStatus(string agentId, AgentStatus status)
    {
        var s = _store.Get(agentId);
        if (s == null) return;
        MainThread.BeginInvokeOnMainThread(() => Host(s).BoardStatus = status);
    }

    /// <summary>Automations-Ereignis eines Agents auslösen.</summary>
    public void Emit(string type, ChatSession agent, Dictionary<string, string>? vars = null) =>
        _ = Task.Run(() => Automations.DispatchAsync(new AutomationEvent(type, vars ?? [], agent)));

    /// <summary>Budget mit Hard-Stop erreicht? → Begründung, sonst null.</summary>
    public string? BudgetBlock(string? repo)
    {
        var hard = _data.Budgets.Value.Budgets.Where(b => b.HardStop && BudgetService.Covers(b, repo)).ToList();
        if (hard.Count == 0) return null;
        try
        {
            var now = DateTime.Now;
            var sessions = Activity.Build(BudgetService.EarliestNeeded(hard, now), now.AddMinutes(1), Agents, id => _data.Project(id)?.SourceFolder);
            var recs = ActivityService.Records(sessions);
            foreach (var b in hard)
            {
                var st = BudgetService.Evaluate(b, recs, now);
                if (st.Fraction >= 1)
                    return $"Budget \"{b.Name}\" ist erschöpft ({(b.Type == BudgetType.Cost ? $"${st.Used:0.00}" : $"{st.Used:0.#} h")} von {(b.Type == BudgetType.Cost ? $"${b.Amount:0.##}" : $"{b.Amount:0.#} h")}, {st.PeriodLabel}). Hard-Stop ist aktiv.";
            }
        }
        catch { }
        return null;
    }

    public void Archive(ChatSession s)
    {
        s.Archived = true;
        _store.Save(s);
        Close(s);
        RaiseAgentsChanged();
    }

    public async Task DeleteAsync(ChatSession s)
    {
        Close(s);
        if (s.Worktree && s.Cwd != null && _data.Project(s.ProjectId)?.SourceFolder is { } repo)
            await Git.RunAsync(repo, default, "worktree", "remove", "--force", s.Cwd);
        _store.Delete(s);
        RaiseAgentsChanged();
    }

    public void Close(ChatSession s)
    {
        if (_hosts.Remove(s.Id, out var h)) h.Stop();
    }

    public void SaveAll()
    {
        foreach (var h in _hosts.Values) h.Save();
    }
}

public partial class TodoItem : ObservableObject
{
    public string Text { get; init; } = "";
    public string Status { get; init; } = "pending";
    public string Glyph => Status switch { "completed" => "check", "in_progress" => "play", _ => "square" };
    public bool Done => Status == "completed";
    public bool Active => Status == "in_progress";
}

public partial class MessageVM : ObservableObject
{
    public ChatMessage Model { get; }
    public MessageVM(ChatMessage m) { Model = m; _text = m.Text; _result = m.ToolResult; _isError = m.IsError; }

    public ChatRole Role => Model.Role;
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsTool => Role == ChatRole.Tool;
    public bool IsSystem => Role == ChatRole.System;
    public bool IsErrorMessage => Role == ChatRole.Error;
    public string SystemIcon => Model.Text.StartsWith("Fertig") ? "check-circle" : Model.Text.StartsWith("Abgebrochen") ? "stop" : "info";
    public Color SystemColor => (Color)Application.Current!.Resources[Model.Text.StartsWith("Fertig") ? "Green" : "Muted"];
    public string Time => Model.Timestamp.ToString("HH:mm");
    /// <summary>Gehört zu einem Sub-Agent (Task) – im Verlauf eingerückt.</summary>
    public bool IsSub => !string.IsNullOrEmpty(Model.ParentToolUseId);
    public Thickness Indent => IsSub ? new Thickness(28, 0, 0, 0) : new Thickness(0);
    public string DurationText => Model.DurationMs is > 0 and var ms ? (ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.#} s") : "";
    public void NotifyDuration() => OnPropertyChanged(nameof(DurationText));

    private string _text;
    public string Text
    {
        get => _text;
        set { if (SetProperty(ref _text, value)) Model.Text = value; }
    }

    private string? _result;
    public string? Result
    {
        get => _result;
        set { if (SetProperty(ref _result, value)) { Model.ToolResult = value; OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(ResultPreview)); } }
    }
    public bool HasResult => !string.IsNullOrEmpty(Result);
    public string ResultPreview => Result is { Length: > 3000 } r ? r[..3000] + "\n…" : Result ?? "";

    private bool _isError;
    public bool IsError { get => _isError; set { if (SetProperty(ref _isError, value)) Model.IsError = value; } }

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }

    public string ToolName => Model.ToolName ?? "";
    public string ToolGlyph => Copit.Controls.IconSet.ForTool(ToolName);
    public string ToolVerb => ToolName switch
    {
        "Read" => "Liest", "Edit" or "MultiEdit" => "Bearbeitet", "Write" => "Schreibt", "Bash" => "Führt aus",
        "Grep" => "Sucht", "Glob" => "Findet Dateien", "WebFetch" => "Lädt", "WebSearch" => "Recherchiert",
        "TodoWrite" => "Plan aktualisiert", "Task" => "Sub-Agent", _ => ToolName
    };
    public string ToolSummary => Summarize(ToolName, Model.ToolInput);
    public string ToolInput => Model.ToolInput ?? "";
    public bool IsFileChange => ToolName is "Edit" or "Write" or "MultiEdit";

    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;

    public static string Summarize(string tool, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            var n = JsonNode.Parse(json);
            foreach (var key in new[] { "file_path", "path", "command", "pattern", "url", "query", "description", "notebook_path" })
            {
                var v = n?[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    v = v.Replace('\n', ' ');
                    return v.Length > 140 ? v[..140] + "…" : v;
                }
            }
            if (n?["todos"] is JsonArray todos)
                return $"{todos.Count(t => t?["status"]?.ToString() == "completed")}/{todos.Count} erledigt";
        }
        catch { }
        return json.Length > 120 ? json[..120] + "…" : json;
    }
}

/// <summary>Eine laufende/geöffnete Chat-Session inkl. Agent-Lauf, Persistenz und Status.</summary>
public partial class SessionHost : ObservableObject
{
    private readonly SessionManager _mgr;
    private readonly SessionStore _store;
    private readonly AgentRunnerFactory _runners;
    private readonly SettingsService _settings;
    private readonly DataService _data;
    private CancellationTokenSource? _cts;
    private MessageVM? _streaming;
    private DateTime _lastSave;
    private readonly Dictionary<string, DateTime> _toolStart = [];
    private readonly HashSet<string> _openTasks = [];
    private long _runToolMs;

    public ChatSession Session { get; }
    public CopitProject? Project => _data.Project(Session.ProjectId);
    public ObservableCollection<MessageVM> Messages { get; } = [];
    public ObservableCollection<PermissionDenial> Denials { get; } = [];
    public ObservableCollection<AgentQuestion> Questions { get; } = [];
    public ObservableCollection<TodoItem> Todos { get; } = [];
    public ObservableCollection<string> ChangedFiles { get; } = [];

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ModelDisplay))] public partial string ModelName { get; set; } = "";
    public string ModelDisplay => !string.IsNullOrEmpty(ModelName) ? ModelName : string.IsNullOrEmpty(Session.Model) ? "Standard-Modell" : Session.Model;
    [ObservableProperty] public partial bool Interrupted { get; set; }
    [ObservableProperty] public partial string Elapsed { get; set; } = "";

    public event Action? RunFinished;
    public event Action? MessageAdded;

    public SessionHost(ChatSession session, SessionManager mgr, SessionStore store, AgentRunnerFactory runners, SettingsService settings, DataService data)
    {
        _mgr = mgr;
        Session = session; _store = store; _runners = runners; _settings = settings; _data = data;
        foreach (var m in session.Messages) Messages.Add(new MessageVM(m) { IsDone = true });
        Interrupted = !string.IsNullOrEmpty(session.PendingPrompt);
        RestoreQuestions();
    }

    /// <summary>Spalte auf dem Board. Manuelles Verschieben löst agent.moved aus.</summary>
    public AgentStatus BoardStatus
    {
        get => Session.Status;
        set
        {
            if (Session.Status == value) return;
            Session.Status = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(StatusTitle)); OnPropertyChanged(nameof(StatusColor));
            Save();
            _mgr.Emit("agent.moved", Session, new() { ["to"] = value.ToString() });
        }
    }

    private void SetStatusSilently(AgentStatus value)
    {
        if (Session.Status == value) return;
        Session.Status = value;
        OnPropertyChanged(nameof(BoardStatus)); OnPropertyChanged(nameof(StatusTitle)); OnPropertyChanged(nameof(StatusColor));
    }

    public string StatusTitle => AgentStatusInfo.Title(Session.Status);
    public Color StatusColor => Color.FromArgb(AgentStatusInfo.Color(Session.Status));
    public string WorkDir => Session.Cwd ?? Project?.SourceFolder ?? "";

    public string AgentTimeText => FmtMs(Session.AgentMs);
    public string AiTimeText => FmtMs(Session.AiMs);
    public string ToolTimeText => FmtMs(Session.ToolMs);
    public string TokensText => Fmt(Session.Tokens);
    public string LinesText => Session.LinesAdded + Session.LinesRemoved > 0 ? $"+{Session.LinesAdded} −{Session.LinesRemoved}" : "";
    public static string FmtMs(long ms) => ms <= 0 ? "0s" : ms < 60_000 ? $"{ms / 1000}s" : ms < 3_600_000 ? $"{ms / 60_000}m {ms / 1000 % 60:00}s" : $"{ms / 3_600_000}h {ms / 60_000 % 60:00}m";

    private void NotifyMetrics()
    {
        foreach (var n in new[] { nameof(AgentTimeText), nameof(AiTimeText), nameof(ToolTimeText), nameof(TokensText), nameof(LinesText), nameof(CostText) })
            OnPropertyChanged(n);
    }

    public string Title
    {
        get => Session.Title;
        set { if (Session.Title != value) { Session.Title = value; OnPropertyChanged(); Save(); } }
    }

    public AiProvider Provider
    {
        get => Session.Provider;
        set
        {
            if (Session.Provider == value) return;
            Session.Provider = value;
            Session.CliSessionId = null; // anderer Anbieter → neue CLI-Session
            OnPropertyChanged(); OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsCopilot));
            Save();
        }
    }
    public bool IsClaude => Provider == AiProvider.Claude;
    public bool IsCopilot => Provider == AiProvider.Copilot;

    public AgentMode Mode
    {
        get => Session.Mode;
        set
        {
            if (Session.Mode == value) return;
            Session.Mode = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsAsk)); OnPropertyChanged(nameof(IsPlan)); OnPropertyChanged(nameof(IsEdit)); OnPropertyChanged(nameof(IsAuto));
            Save();
        }
    }
    public bool IsAsk => Mode == AgentMode.Ask;
    public bool IsPlan => Mode == AgentMode.Plan;
    public bool IsEdit => Mode == AgentMode.Edit;
    public bool IsAuto => Mode == AgentMode.Auto;

    public string Model
    {
        get => Session.Model;
        set { if (Session.Model != value) { Session.Model = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModelDisplay)); OnPropertyChanged(nameof(ContextWindow)); OnPropertyChanged(nameof(ContextText)); Save(); } }
    }

    public long ContextWindow => Session.Model.Contains("1m", StringComparison.OrdinalIgnoreCase) || ModelName.Contains("1m", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 200_000;
    public double ContextFraction => Math.Clamp(Session.ContextTokens / (double)ContextWindow, 0, 1);
    public string ContextText => $"{Fmt(Session.ContextTokens)} / {Fmt(ContextWindow)}";
    public string ContextPercent => $"{ContextFraction * 100:0}%";
    public Color ContextColor => (Color)Application.Current!.Resources[ContextFraction > 0.85 ? "Red" : ContextFraction > 0.6 ? "Amber" : "Cyan"];
    public string CostText => Session.TotalCostUsd > 0 ? $"${Session.TotalCostUsd:0.00}" : "";
    public bool HasCliSession => !string.IsNullOrEmpty(Session.CliSessionId);

    private static string Fmt(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private void NotifyContext()
    {
        OnPropertyChanged(nameof(ContextFraction)); OnPropertyChanged(nameof(ContextText));
        OnPropertyChanged(nameof(ContextPercent)); OnPropertyChanged(nameof(ContextColor)); OnPropertyChanged(nameof(CostText));
    }

    private void RestoreQuestions()
    {
        Questions.Clear();
        var last = Messages.LastOrDefault(m => m.IsAssistant);
        if (last == null || Messages.Last() != last) return;
        var (_, qs) = QuestionParser.Extract(last.Text);
        foreach (var q in qs) Questions.Add(q);
    }

    public void Save()
    {
        _lastSave = DateTime.Now;
        try { _store.Save(Session); } catch (Exception e) { App.LogCrash(e); }
    }

    private void SaveThrottled()
    {
        if ((DateTime.Now - _lastSave).TotalSeconds > 2) Save();
    }

    private MessageVM Add(ChatMessage m)
    {
        Session.Messages.Add(m);
        var vm = new MessageVM(m);
        Messages.Add(vm);
        MessageAdded?.Invoke();
        return vm;
    }

    public void AddSystem(string text) => Add(new ChatMessage { Role = ChatRole.System, Text = text }).IsDone = true;

    private string BuildSystemPrompt()
    {
        var parts = new List<string>();
        var ai = _settings.Settings.Ai;
        if (!string.IsNullOrWhiteSpace(ai.GlobalInstructions)) parts.Add(ai.GlobalInstructions);
        var p = Project;
        if (p != null)
        {
            parts.Add($"Du arbeitest im Projekt \"{p.Name}\" (Jira-Epic {p.EpicKey}{(string.IsNullOrEmpty(p.GitLabProject) ? "" : ", GitLab " + p.GitLabProject)}).");
            if (!string.IsNullOrWhiteSpace(p.Instructions)) parts.Add(p.Instructions);
        }
        parts.Add(QuestionParser.Instructions);
        return string.Join("\n\n", parts);
    }

    [RelayCommand]
    public void Stop()
    {
        _cts?.Cancel();
    }

    public async Task SendAsync(string prompt, string? displayText = null)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(prompt)) return;
        var p = Project;
        if (_mgr.BudgetBlock(p?.SourceFolder) is { } block)
        {
            Add(new ChatMessage { Role = ChatRole.Error, Text = block, IsError = true }).IsDone = true;
            Session.Error = block;
            Save();
            return;
        }
        Questions.Clear();
        Denials.Clear();
        Interrupted = false;
        if (Session.Messages.Count(m => m.Role == ChatRole.User) == 0 && Session.Title == "Neue Session")
            Title = (displayText ?? prompt).Split('\n')[0] is var t && t.Length > 48 ? t[..48] + "…" : (displayText ?? prompt).Split('\n')[0];

        Add(new ChatMessage { Role = ChatRole.User, Text = displayText ?? prompt }).IsDone = true;
        var history = Session.Messages.ToList();
        Session.PendingPrompt = prompt;
        Save();

        Session.Error = null;
        Session.PendingQuestion = null;
        Session.PendingPermissions.Clear();
        var cwd = string.IsNullOrEmpty(Session.Cwd) || !Directory.Exists(Session.Cwd) ? p?.SourceFolder : Session.Cwd;
        if (Session.BaseSha == null && cwd != null && GitService.IsRepo(cwd))
        {
            var head = await _mgr.Git.RunAsync(cwd, default, "rev-parse", "HEAD");
            if (head.Ok) Session.BaseSha = head.StdOut.Trim();
        }
        var run = new AgentRun();
        Session.Runs.Add(run);
        _runToolMs = 0; _toolStart.Clear(); _openTasks.Clear();
        SetStatusSilently(AgentStatus.InProgress);
        _mgr.Emit("agent.started", Session);

        IsRunning = true;
        Status = Provider == AiProvider.Claude ? "Claude startet…" : "Copilot startet…";
        _cts = new CancellationTokenSource();
        var started = DateTime.Now;
        var timer = Application.Current!.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => Elapsed = $"{(DateTime.Now - started):m\\:ss}";
        timer.Start();
        _streaming = null;

        var model = string.IsNullOrWhiteSpace(Session.Model)
            ? (Provider == AiProvider.Claude ? _settings.Settings.Ai.ClaudeModel : _settings.Settings.Ai.CopilotModel)
            : Session.Model;

        var allowed = Session.AllowedTools.Concat(Session.AllowedOnce).ToList();
        var disallowed = new List<string>();
        if (_settings.Settings.Ai.ApplyPermissionRules)
        {
            var (a, d) = _mgr.Permissions.Flags(p?.SourceFolder);
            allowed.AddRange(a);
            disallowed.AddRange(d);
        }
        Session.AllowedOnce.Clear();
        var request = new AgentRequest
        {
            Prompt = prompt,
            WorkingDirectory = cwd,
            Mode = Session.Mode,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            ResumeSessionId = Provider == AiProvider.Claude ? Session.CliSessionId : null,
            AllowedTools = allowed.Distinct().ToList(),
            DisallowedTools = disallowed.Distinct().ToList(),
            AppendSystemPrompt = BuildSystemPrompt(),
            History = Provider == AiProvider.Copilot ? history.Take(history.Count - 1).ToList() : [],
        };

        try
        {
            await foreach (var e in _runners.Get(Provider).RunAsync(request, _cts.Token))
            {
                Handle(e, run);
                SaveThrottled();
            }
        }
        catch (Exception ex)
        {
            Add(new ChatMessage { Role = ChatRole.Error, Text = ex.Message, IsError = true });
            Session.Error = ex.Message;
            run.Ok = false;
        }
        finally
        {
            timer.Stop();
            if (_streaming != null) _streaming.IsDone = true;
            _streaming = null;
            foreach (var m in Messages.Where(m => !m.IsDone)) m.IsDone = true;
            Session.PendingPrompt = null;
            run.End = DateTime.Now;
            var wall = (long)(run.End.Value - run.Start).TotalMilliseconds;
            run.ToolMs = _runToolMs;
            run.AiMs = Math.Max(0, wall - _runToolMs);
            Session.AgentMs += wall;
            Session.ToolMs += _runToolMs;
            Session.AiMs += run.AiMs;
            await UpdateLineStatsAsync(cwd);
            var lastText = Messages.LastOrDefault(m => m.IsAssistant && !m.IsSub)?.Text ?? "";
            if (lastText.Length > 0) Session.Summary = QuestionParser.Extract(lastText).CleanText is var c && c.Length > 600 ? c[..600] + "…" : QuestionParser.Extract(lastText).CleanText;
            IsRunning = false;
            Status = "";
            // Board-Status nach dem Lauf
            if (run.Ok == false && Session.Error != null && !Session.Error.StartsWith("Abgebrochen")) { SetStatusSilently(AgentStatus.Failed); _mgr.Emit("agent.failed", Session); }
            else if (Session.Error == "Abgebrochen") { SetStatusSilently(AgentStatus.Review); _mgr.Emit("agent.stopped", Session); }
            else if (Questions.Count > 0 || Denials.Count > 0)
            {
                Session.PendingQuestion = Questions.FirstOrDefault()?.Text;
                Session.PendingPermissions = Denials.ToList();
                SetStatusSilently(AgentStatus.NeedInput);
                _mgr.Emit("agent.needs_input", Session);
            }
            else { SetStatusSilently(AgentStatus.Review); _mgr.Emit("agent.finished", Session); }
            NotifyMetrics();
            Save();
            _mgr.RaiseAgentsChanged();
            RunFinished?.Invoke();
        }
    }

    private async Task UpdateLineStatsAsync(string? cwd)
    {
        if (cwd == null || Session.BaseSha == null || !GitService.IsRepo(cwd)) return;
        try
        {
            var r = await _mgr.Git.RunAsync(cwd, default, "diff", "--numstat", Session.BaseSha);
            int add = 0, del = 0, files = 0;
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                files++;
                if (int.TryParse(parts[0], out var a)) add += a;
                if (int.TryParse(parts[1], out var d)) del += d;
            }
            var untracked = await _mgr.Git.RunAsync(cwd, default, "ls-files", "--others", "--exclude-standard");
            files += untracked.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            Session.LinesAdded = add; Session.LinesRemoved = del; Session.FilesChanged = files;
        }
        catch { }
    }

    private void Handle(AgentEvent e, AgentRun run)
    {
        switch (e)
        {
            case AgentTextDelta { Parent: not null }:
                return; // Sub-Agent-Text kommt vollständig als AgentText

            case AgentText { Parent: { } parent } st:
                Add(new ChatMessage { Role = ChatRole.Assistant, Text = st.Text, ParentToolUseId = parent }).IsDone = true;
                return;

            case AgentToolUse { Parent: { } parent } stu:
                var svm = Add(new ChatMessage { Role = ChatRole.Tool, ToolName = stu.Name, ToolUseId = stu.Id, ToolInput = stu.InputJson, ParentToolUseId = parent });
                Status = $"Sub-Agent · {svm.ToolVerb} {svm.ToolSummary}";
                _toolStart[stu.Id] = DateTime.Now;
                return;

            case AgentInit init:
                if (!string.IsNullOrEmpty(init.SessionId)) Session.CliSessionId = init.SessionId;
                ModelName = init.Model;
                OnPropertyChanged(nameof(HasCliSession));
                OnPropertyChanged(nameof(ContextWindow));
                Status = "verbunden · " + init.Model;
                break;

            case AgentTextDelta d:
                _streaming ??= Add(new ChatMessage { Role = ChatRole.Assistant });
                _streaming.Text += d.Text;
                Status = "schreibt…";
                break;

            case AgentThinking:
                Status = "denkt nach…";
                break;

            case AgentText t:
                if (_streaming != null) { _streaming.Text = t.Text; _streaming.IsDone = true; _streaming = null; }
                else Add(new ChatMessage { Role = ChatRole.Assistant, Text = t.Text }).IsDone = true;
                break;

            case AgentToolUse tu:
                if (_streaming != null) { _streaming.IsDone = true; _streaming = null; }
                var vm = Add(new ChatMessage { Role = ChatRole.Tool, ToolName = tu.Name, ToolUseId = tu.Id, ToolInput = tu.InputJson });
                Status = $"{vm.ToolVerb} {vm.ToolSummary}";
                _toolStart[tu.Id] = DateTime.Now;
                if (tu.Name is "Task" or "Agent") { _openTasks.Add(tu.Id); SetStatusSilently(AgentStatus.WaitingSubagent); _mgr.RaiseAgentsChanged(); }
                if (tu.Name == "TodoWrite") UpdateTodos(tu.InputJson);
                if (vm.IsFileChange && vm.ToolSummary.Length > 0 && !ChangedFiles.Contains(vm.ToolSummary)) ChangedFiles.Add(vm.ToolSummary);
                break;

            case AgentToolResult tr:
                var tool = Messages.LastOrDefault(m => m.Model.ToolUseId == tr.ToolUseId);
                if (_toolStart.Remove(tr.ToolUseId, out var t0))
                {
                    var ms = (long)(DateTime.Now - t0).TotalMilliseconds;
                    if (tr.Parent == null) _runToolMs += ms; // Sub-Agent-Zeit steckt im Task-Aufruf
                    if (tool != null) { tool.Model.DurationMs = ms; tool.NotifyDuration(); }
                }
                if (tool != null) { tool.Result = tr.Content; tool.IsError = tr.IsError; tool.IsDone = true; }
                if (_openTasks.Remove(tr.ToolUseId) && _openTasks.Count == 0) { SetStatusSilently(AgentStatus.InProgress); _mgr.RaiseAgentsChanged(); }
                Status = "arbeitet…";
                break;

            case AgentUsage u:
                if (u.ContextTokens > 0) { Session.ContextTokens = u.ContextTokens; NotifyContext(); }
                break;

            case AgentStatusLine s:
                if (s.Text.Length < 200) Status = s.Text;
                break;

            case AgentResult r:
                if (!string.IsNullOrEmpty(r.SessionId)) Session.CliSessionId = r.SessionId;
                Session.TotalCostUsd += r.CostUsd;
                run.CostUsd = r.CostUsd;
                run.Turns = r.Turns;
                Session.Turns += r.Turns;
                run.Ok = !r.IsError;
                if (r.IsError) Session.Error = r.Subtype == "cancelled" ? "Abgebrochen" : string.IsNullOrWhiteSpace(r.Text) ? r.Subtype : r.Text;
                if (r.Usage is { } usage)
                {
                    run.InputTokens = usage.Input; run.OutputTokens = usage.Output; run.CacheRead = usage.CacheRead; run.CacheWrite = usage.CacheWrite;
                    Session.Tokens += usage.Total;
                    Session.OutputTokens += usage.Output;
                }
                if (Provider == AiProvider.Copilot)
                {
                    // Kontext grob schätzen (Copilot liefert keine Token-Zahlen)
                    Session.ContextTokens = Session.Messages.Sum(m => (long)(m.Text.Length + (m.ToolResult?.Length ?? 0))) / 4;
                }
                NotifyContext();
                if (r.IsError && r.Subtype != "cancelled")
                    Add(new ChatMessage { Role = ChatRole.Error, Text = string.IsNullOrWhiteSpace(r.Text) ? "Unbekannter Fehler der CLI." : r.Text, IsError = true }).IsDone = true;
                else if (r.Subtype == "cancelled")
                    AddSystem("Abgebrochen");
                else if (!Messages.Any(m => m.IsAssistant && m.Model.Timestamp >= Session.Messages.Last(x => x.Role == ChatRole.User).Timestamp) && !string.IsNullOrWhiteSpace(r.Text))
                    Add(new ChatMessage { Role = ChatRole.Assistant, Text = r.Text }).IsDone = true;
                else if (r.Subtype == "error_max_turns")
                    AddSystem("Maximale Anzahl an Schritten erreicht – schreibe \"weiter\", um fortzufahren.");

                foreach (var d in r.Denials.DistinctBy(d => d.ToolName + d.InputJson)) Denials.Add(d);

                var last = Messages.LastOrDefault(m => m.IsAssistant);
                if (last != null)
                {
                    var (clean, qs) = QuestionParser.Extract(last.Text);
                    if (qs.Count > 0) { foreach (var q in qs) Questions.Add(q); }
                }
                if (r.DurationMs > 0)
                    AddSystem($"Fertig in {TimeSpan.FromMilliseconds(r.DurationMs):m\\:ss} · {r.Turns} Schritte{(r.CostUsd > 0 ? $" · ${r.CostUsd:0.000}" : "")}");
                break;
        }
    }

    private void UpdateTodos(string json)
    {
        try
        {
            var arr = JsonNode.Parse(json)?["todos"]?.AsArray();
            if (arr == null) return;
            Todos.Clear();
            foreach (var t in arr)
                Todos.Add(new TodoItem { Text = t?["content"]?.ToString() ?? "", Status = t?["status"]?.ToString() ?? "pending" });
        }
        catch { }
    }

    /// <summary>Freigabe-Reichweite: einmal, für diese Session, für das Repo oder global (Copit-Regel).</summary>
    public enum AllowScope { Once, Session, Repo, Global }

    /// <summary>Erlaubt die abgelehnten Befehle (Ketten werden in Einzelmuster zerlegt) und setzt den Lauf fort.</summary>
    public async Task AllowAsync(PermissionDenial d, AllowScope scope = AllowScope.Session)
    {
        var patterns = PermissionService.PatternsForDenial(d.ToolName, d.InputJson);
        foreach (var pattern in patterns)
        {
            switch (scope)
            {
                case AllowScope.Once: if (!Session.AllowedOnce.Contains(pattern)) Session.AllowedOnce.Add(pattern); break;
                case AllowScope.Session: if (!Session.AllowedTools.Contains(pattern)) Session.AllowedTools.Add(pattern); break;
                case AllowScope.Repo: _mgr.Permissions.Add(pattern, PermissionLevel.Allow, RuleSource.Copit, Project?.SourceFolder); break;
                case AllowScope.Global: _mgr.Permissions.Add(pattern, PermissionLevel.Allow, RuleSource.Copit, null); break;
            }
        }
        Denials.Remove(d);
        Save();
        var label = string.Join(", ", patterns);
        await SendAsync($"Die Berechtigung für {label} wurde erteilt. Bitte führe den Schritt jetzt aus und fahre fort.", $"Erlaubt ({scope switch { AllowScope.Once => "einmal", AllowScope.Session => "Session", AllowScope.Repo => "Repo", _ => "global" }}): {label}");
    }

    public Task AllowAsync(PermissionDenial d, bool wholeTool) => AllowAsync(d, wholeTool ? AllowScope.Repo : AllowScope.Session);

    public async Task DenyAsync(PermissionDenial d)
    {
        Denials.Remove(d);
        await SendAsync($"Die Berechtigung für {d.ToolName} wurde verweigert. Mache ohne diesen Schritt weiter oder schlage eine Alternative vor.", $"Abgelehnt: {d.ToolName}");
    }

    public Task ResumeInterruptedAsync() =>
        SendAsync("Der letzte Lauf wurde unterbrochen (Neustart). Prüfe kurz den aktuellen Stand und fahre mit der Aufgabe fort.", "Unterbrochenen Lauf fortsetzen");
}
