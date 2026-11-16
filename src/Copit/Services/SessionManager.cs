using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Services;

/// <summary>Hält alle geöffneten Sessions. Läufe laufen weiter, auch wenn die Ansicht gewechselt wird.</summary>
public class SessionManager(SessionStore store, AgentRunnerFactory runners, SettingsService settings, DataService data)
{
    private readonly Dictionary<string, SessionHost> _hosts = [];
    public event Action? RunningChanged;

    public int RunningCount => _hosts.Values.Count(h => h.IsRunning);

    public SessionHost Host(ChatSession s)
    {
        if (_hosts.TryGetValue(s.Id, out var h)) return h;
        h = new SessionHost(s, store, runners, settings, data);
        h.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionHost.IsRunning)) RunningChanged?.Invoke(); };
        return _hosts[s.Id] = h;
    }

    public SessionHost Create(CopitProject project, AiProvider? provider = null)
    {
        var s = store.Create(project, provider ?? project.Provider ?? settings.Settings.Ai.DefaultProvider, AgentMode.Edit);
        return Host(s);
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
    private readonly SessionStore _store;
    private readonly AgentRunnerFactory _runners;
    private readonly SettingsService _settings;
    private readonly DataService _data;
    private CancellationTokenSource? _cts;
    private MessageVM? _streaming;
    private DateTime _lastSave;

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

    public SessionHost(ChatSession session, SessionStore store, AgentRunnerFactory runners, SettingsService settings, DataService data)
    {
        Session = session; _store = store; _runners = runners; _settings = settings; _data = data;
        foreach (var m in session.Messages) Messages.Add(new MessageVM(m) { IsDone = true });
        Interrupted = !string.IsNullOrEmpty(session.PendingPrompt);
        RestoreQuestions();
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
        Questions.Clear();
        Denials.Clear();
        Interrupted = false;
        if (Session.Messages.Count(m => m.Role == ChatRole.User) == 0 && Session.Title == "Neue Session")
            Title = (displayText ?? prompt).Split('\n')[0] is var t && t.Length > 48 ? t[..48] + "…" : (displayText ?? prompt).Split('\n')[0];

        Add(new ChatMessage { Role = ChatRole.User, Text = displayText ?? prompt }).IsDone = true;
        var history = Session.Messages.ToList();
        Session.PendingPrompt = prompt;
        Save();

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

        var request = new AgentRequest
        {
            Prompt = prompt,
            WorkingDirectory = p?.SourceFolder,
            Mode = Session.Mode,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            ResumeSessionId = Provider == AiProvider.Claude ? Session.CliSessionId : null,
            AllowedTools = Session.AllowedTools,
            AppendSystemPrompt = BuildSystemPrompt(),
            History = Provider == AiProvider.Copilot ? history.Take(history.Count - 1).ToList() : [],
        };

        try
        {
            await foreach (var e in _runners.Get(Provider).RunAsync(request, _cts.Token))
            {
                Handle(e);
                SaveThrottled();
            }
        }
        catch (Exception ex)
        {
            Add(new ChatMessage { Role = ChatRole.Error, Text = ex.Message, IsError = true });
        }
        finally
        {
            timer.Stop();
            if (_streaming != null) _streaming.IsDone = true;
            _streaming = null;
            foreach (var m in Messages.Where(m => !m.IsDone)) m.IsDone = true;
            Session.PendingPrompt = null;
            IsRunning = false;
            Status = "";
            Save();
            RunFinished?.Invoke();
        }
    }

    private void Handle(AgentEvent e)
    {
        switch (e)
        {
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
                if (tu.Name == "TodoWrite") UpdateTodos(tu.InputJson);
                if (vm.IsFileChange && vm.ToolSummary.Length > 0 && !ChangedFiles.Contains(vm.ToolSummary)) ChangedFiles.Add(vm.ToolSummary);
                break;

            case AgentToolResult tr:
                var tool = Messages.LastOrDefault(m => m.Model.ToolUseId == tr.ToolUseId);
                if (tool != null) { tool.Result = tr.Content; tool.IsError = tr.IsError; tool.IsDone = true; }
                Status = "arbeitet…";
                break;

            case AgentUsage u:
                if (u.ContextTokens > 0) { Session.ContextTokens = u.ContextTokens; NotifyContext(); }
                break;

            case AgentStatus s:
                if (s.Text.Length < 200) Status = s.Text;
                break;

            case AgentResult r:
                if (!string.IsNullOrEmpty(r.SessionId)) Session.CliSessionId = r.SessionId;
                Session.TotalCostUsd += r.CostUsd;
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

    /// <summary>Erlaubt ein Tool (für diese Session) und setzt den Lauf fort.</summary>
    public async Task AllowAsync(PermissionDenial d, bool wholeTool)
    {
        string pattern = d.ToolName;
        if (!wholeTool && d.ToolName == "Bash")
        {
            try
            {
                var cmd = JsonNode.Parse(d.InputJson)?["command"]?.ToString();
                if (!string.IsNullOrWhiteSpace(cmd)) pattern = $"Bash({cmd})";
            }
            catch { }
        }
        if (!Session.AllowedTools.Contains(pattern)) Session.AllowedTools.Add(pattern);
        Save();
        await SendAsync($"Die Berechtigung für {pattern} wurde erteilt. Bitte führe den Schritt jetzt aus und fahre fort.", $"Erlaubt: {pattern}");
    }

    public Task ResumeInterruptedAsync() =>
        SendAsync("Der letzte Lauf wurde unterbrochen (Neustart). Prüfe kurz den aktuellen Stand und fahre mit der Aufgabe fort.", "Unterbrochenen Lauf fortsetzen");
}
