using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

/// <summary>Parameter um eine Session zu öffnen (optional mit Start-Prompt).</summary>
public record SessionRequest(CopitProject Project, string? Prompt = null, string? DisplayText = null, bool NewSession = false, JiraIssue? Issue = null,
    AgentMode? Mode = null, string? AutoPrompt = null);

public partial class SessionViewModel : ObservableObject, IActivatable
{
    private readonly SessionManager _manager;
    private readonly SessionStore _store;
    private readonly DataService _data;
    private readonly SettingsService _settings;
    private readonly ToastService _toast;
    private readonly NavigationService _nav;
    private IDispatcherTimer? _gitTimer;

    public GitPanelViewModel Git { get; }
    public ObservableCollection<SessionHost> Sessions { get; } = [];
    public ObservableCollection<CopitProject> Projects { get; } = [];
    public ObservableCollection<PromptSnippet> Snippets { get; } = [];
    public string[] ClaudeModels { get; } = ["", "opus", "sonnet", "haiku", "opus[1m]", "sonnet[1m]"];

    [ObservableProperty] public partial CopitProject? Project { get; set; }
    [ObservableProperty] public partial SessionHost? Host { get; set; }
    [ObservableProperty] public partial string Input { get; set; } = "";
    [ObservableProperty] public partial bool ShowSessions { get; set; } = true;
    [ObservableProperty] public partial bool ShowGit { get; set; } = true;
    [ObservableProperty] public partial bool ShowSnippets { get; set; }

    public bool HasProject => Project != null;
    public bool HasHost => Host != null;

    public event Action? ScrollToEndRequested;

    public SessionViewModel(SessionManager manager, SessionStore store, DataService data, SettingsService settings,
        ToastService toast, NavigationService nav, GitPanelViewModel git)
    {
        _manager = manager; _store = store; _data = data; _settings = settings; _toast = toast; _nav = nav;
        Git = git;
        Git.SendToSession += (prompt, display) => _ = SendInternalAsync(prompt, display);
    }

    partial void OnProjectChanged(CopitProject? value)
    {
        OnPropertyChanged(nameof(HasProject));
        Git.SetProject(value);
        LoadSessions();
        if (value != null)
        {
            value.LastOpened = DateTime.Now;
            _data.Projects.Value.ActiveProjectId = value.Id;
            _ = _data.Projects.SaveAsync();
        }
    }

    private SessionHost? _oldHost;
    partial void OnHostChanged(SessionHost? value)
    {
        if (_oldHost != null) { _oldHost.RunFinished -= OnRunFinished; _oldHost.MessageAdded -= OnMessageAdded; }
        _oldHost = value;
        if (value != null) { value.RunFinished += OnRunFinished; value.MessageAdded += OnMessageAdded; }
        OnPropertyChanged(nameof(HasHost));
        ScrollToEndRequested?.Invoke();
    }

    private void OnRunFinished()
    {
        _ = Git.RefreshAsync();
        LoadSessions();
    }

    private void OnMessageAdded() => ScrollToEndRequested?.Invoke();

    private void LoadSessions()
    {
        Sessions.Clear();
        if (Project == null) return;
        foreach (var s in _store.ForProject(Project.Id)) Sessions.Add(_manager.Host(s));
    }

    public void OnActivated(object? parameter)
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened)) Projects.Add(p);
        Snippets.Clear();
        foreach (var s in _data.Prompts.Value.Snippets) Snippets.Add(s);

        switch (parameter)
        {
            case SessionRequest req:
                Project = req.Project;
                if (req.NewSession || Host == null || Host.Session.ProjectId != req.Project.Id || Host.IsRunning)
                {
                    // Den in der Session gewählten Modus (z. B. Auto) übernehmen – vor dem ersten Lauf, nicht erst danach.
                    var mode = req.Mode ?? Host?.Mode;
                    Host = _manager.Create(req.Project, mode: mode);
                }
                LoadSessions();
                if (req.Issue != null) _ = Git.CreateBranchForIssueAsync(req.Issue);
                var prompt = Host.Mode == AgentMode.Auto && !string.IsNullOrWhiteSpace(req.AutoPrompt) ? req.AutoPrompt : req.Prompt;
                if (!string.IsNullOrWhiteSpace(prompt)) _ = Host.SendAsync(prompt, req.DisplayText);
                break;
            case SessionHost h:
                Project = h.Project;
                Host = h;
                break;
            case CopitProject p:
                Project = p;
                Host = Sessions.FirstOrDefault() ?? _manager.Create(p);
                LoadSessions();
                break;
            default:
                if (Project == null)
                {
                    var p = _data.Project(_data.Projects.Value.ActiveProjectId) ?? Projects.FirstOrDefault();
                    if (p != null) { Project = p; Host = Sessions.FirstOrDefault(); }
                }
                break;
        }
        StartGitTimer();
    }

    private void StartGitTimer()
    {
        if (_gitTimer != null) return;
        _gitTimer = Application.Current!.Dispatcher.CreateTimer();
        _gitTimer.Interval = TimeSpan.FromSeconds(25);
        _gitTimer.Tick += (_, _) => { if (_nav.Current == Routes.Session) _ = Git.RefreshAsync(); };
        _gitTimer.Start();
    }

    [RelayCommand]
    private async Task SwitchProjectAsync()
    {
        if (Projects.Count == 0) { _nav.Go(Routes.Projects); return; }
        var choice = await Dialogs.Choose("Projekt wählen", Projects.Select(p => p.Name).ToArray());
        var p = Projects.FirstOrDefault(x => x.Name == choice);
        if (p == null) return;
        Project = p;
        Host = Sessions.FirstOrDefault() ?? _manager.Create(p);
        LoadSessions();
    }

    [RelayCommand]
    private void NewSession()
    {
        if (Project == null) return;
        Host = _manager.Create(Project);
        LoadSessions();
    }

    [RelayCommand] private void OpenSession(SessionHost h) => Host = h;

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionHost h)
    {
        if (!await Dialogs.Confirm("Session löschen?", $"\"{h.Title}\" wird dauerhaft gelöscht.", "Löschen")) return;
        _manager.Close(h.Session);
        _store.Delete(h.Session);
        if (Host == h) Host = null;
        LoadSessions();
        Host ??= Sessions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task RenameSessionAsync()
    {
        if (Host == null) return;
        var t = await Dialogs.Prompt("Session umbenennen", "Titel", Host.Title);
        if (!string.IsNullOrWhiteSpace(t)) { Host.Title = t; LoadSessions(); }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (Host == null)
        {
            if (Project == null) { _toast.Show("Kein Projekt", "Lege zuerst ein Projekt an.", NoticeLevel.Warning); return; }
            Host = _manager.Create(Project);
            LoadSessions();
        }
        if (Host.IsRunning) { _toast.Show("Session arbeitet noch", "Warte bis der Lauf fertig ist oder stoppe ihn."); return; }
        Input = "";
        await Host.SendAsync(text);
    }

    private async Task SendInternalAsync(string prompt, string display)
    {
        if (Project == null) return;
        if (Host == null || Host.IsRunning) { Host = _manager.Create(Project); LoadSessions(); }
        await Host.SendAsync(prompt, display);
    }

    [RelayCommand] private void Stop() => Host?.Stop();

    [RelayCommand]
    private void SetMode(string mode)
    {
        if (Host != null && Enum.TryParse<AgentMode>(mode, out var m)) Host.Mode = m;
    }

    [RelayCommand]
    private void SetProvider(string provider)
    {
        if (Host != null && Enum.TryParse<AiProvider>(provider, out var p))
        {
            if (Host.Provider != p && Host.Session.Messages.Count > 0)
                Host.AddSystem($"Anbieter gewechselt zu {p}. Der bisherige Verlauf wird als Kontext mitgegeben.");
            Host.Provider = p;
        }
    }

    [RelayCommand]
    private async Task PickModelAsync()
    {
        if (Host == null) return;
        var options = Host.IsClaude
            ? new[] { "Standard", "opus", "sonnet", "haiku", "opus[1m]", "sonnet[1m]", "Eigenes…" }
            : new[] { "Standard", "claude-sonnet-4.5", "gpt-5", "Eigenes…" };
        var c = await Dialogs.Choose("Modell", options);
        if (c == null || c == "Abbrechen") return;
        if (c == "Eigenes…") c = await Dialogs.Prompt("Modell", "Modellname", Host.Model) ?? Host.Model;
        Host.Model = c == "Standard" ? "" : c;
    }

    [RelayCommand]
    private async Task PickContextAsync()
    {
        if (Host == null) return;
        var options = Host.IsClaude
            ? new[] { "Automatisch (nach Modell)", "200k", "1M – Modell mit [1m]", "Eigener Wert…" }
            : new[] { "Automatisch (nach Modell)", "128k", "200k", "400k", "1M", "Eigener Wert…" };
        var c = await Dialogs.Choose($"Kontextfenster · aktuell {Host.ContextText}", options);
        if (c == null || c == "Abbrechen") return;
        long? size = c switch
        {
            "128k" => 128_000, "200k" => 200_000, "400k" => 400_000, "1M" or "1M – Modell mit [1m]" => 1_000_000,
            "Eigener Wert…" => await Dialogs.Prompt("Kontextfenster", "Tokens, z. B. 300000 oder 300k", (Host.ContextWindow / 1000) + "k") is { } v
                && long.TryParse(v.Trim().ToLowerInvariant().Replace("k", "000").Replace("m", "000000").Replace(".", ""), out var n) && n >= 8_000 ? n : -1,
            _ => null,
        };
        if (size == -1) return;
        Host.SetContextWindow(size);
    }

    /// <summary>Option einer Rückfrage: bei nur einer Frage sofort senden, bei mehreren nur auswählen.</summary>
    [RelayCommand]
    private async Task AnswerAsync(QuestionOptionVM option)
    {
        if (Host == null || Host.IsRunning) return;
        option.Owner.Choose(option);
        if (Host.Questions.Count == 1 && option.Owner.IsAnswered) await Host.SendAnswersAsync();
    }

    [RelayCommand]
    private async Task SendAnswersAsync()
    {
        if (Host == null || Host.IsRunning) return;
        await Host.SendAnswersAsync();
    }

    [RelayCommand] private Task AllowAsync(PermissionDenial d) => Host?.AllowAsync(d, false) ?? Task.CompletedTask;
    [RelayCommand] private Task AllowToolAsync(PermissionDenial d) => Host?.AllowAsync(d, true) ?? Task.CompletedTask;

    [RelayCommand]
    private Task SwitchToAutoAsync()
    {
        if (Host == null) return Task.CompletedTask;
        Host.Mode = AgentMode.Auto;
        return Host.SendAsync("Du hast jetzt alle Berechtigungen. Führe die zuvor blockierten Schritte aus und fahre fort.", "Auto-Modus aktiviert – fortfahren");
    }

    [RelayCommand] private Task ResumeAsync() => Host?.ResumeInterruptedAsync() ?? Task.CompletedTask;

    [RelayCommand] private void ToggleSessions() => ShowSessions = !ShowSessions;
    [RelayCommand] private void ToggleGit() => ShowGit = !ShowGit;
    [RelayCommand] private void ToggleSnippets() => ShowSnippets = !ShowSnippets;

    [RelayCommand]
    private void UseSnippet(PromptSnippet s)
    {
        Input = string.IsNullOrWhiteSpace(Input) ? s.Text : Input + "\n" + s.Text;
        ShowSnippets = false;
    }

    [RelayCommand]
    private async Task InsertDiffAsync()
    {
        if (Project == null) return;
        Input += "\n\nBitte prüfe meine aktuellen, noch nicht committeten Änderungen (git diff) und gib Feedback.";
        await Task.CompletedTask;
    }

    [RelayCommand] private void OpenFolder() { if (Project != null) Dialogs.OpenInEditor(Project.SourceFolder); }
    [RelayCommand] private void OpenTerminal() { if (Project != null) Dialogs.OpenTerminal(Project.SourceFolder); }
    [RelayCommand] private void OpenProject() { if (Project != null) _nav.Go(Routes.Project, Project.Id); }
}
