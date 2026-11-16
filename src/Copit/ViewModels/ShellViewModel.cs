using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Ai;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

/// <summary>Statusleiste: Verbindungen, Stunden heute, laufende Sessions.</summary>
public partial class SidebarProject : ObservableObject
{
    public CopitProject Project { get; init; } = null!;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    public string Ago
    {
        get
        {
            var d = DateTime.Now - Project.LastOpened;
            return d.TotalMinutes < 60 ? (d.TotalMinutes < 2 ? "NOW" : $"{(int)d.TotalMinutes}M") : d.TotalHours < 24 ? $"{(int)d.TotalHours}H" : $"{(int)d.TotalDays}D";
        }
    }
}

public partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService _nav;
    private readonly DataService _data;
    private readonly SessionStore _store;
    public ObservableCollection<SidebarProject> Projects { get; } = [];
    private readonly SettingsService _settings;
    private readonly ConnectionHub _hub;
    private readonly AgentRunnerFactory _runners;
    private readonly TimeBookingService _time;
    private readonly SessionManager _sessions;

    [ObservableProperty] public partial Color JiraColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial Color GitLabColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial Color ClaudeColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial Color CopilotColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial string JiraInfo { get; set; } = "Jira";
    [ObservableProperty] public partial string GitLabInfo { get; set; } = "GitLab";
    [ObservableProperty] public partial string ClaudeInfo { get; set; } = "Claude CLI";
    [ObservableProperty] public partial string CopilotInfo { get; set; } = "Copilot CLI";
    [ObservableProperty] public partial string TodayHours { get; set; } = "–";
    [ObservableProperty] public partial string Clock { get; set; } = "";
    [ObservableProperty] public partial string RunningInfo { get; set; } = "";
    [ObservableProperty] public partial bool AnyRunning { get; set; }

    public ShellViewModel(SettingsService settings, ConnectionHub hub, AgentRunnerFactory runners, TimeBookingService time, SessionManager sessions,
        NavigationService nav, DataService data, SessionStore store)
    {
        _nav = nav; _data = data; _store = store;
        data.ProjectsChanged += () => MainThread.BeginInvokeOnMainThread(LoadProjects);
        nav.Navigated += (_, _) => LoadProjects();
        _settings = settings; _hub = hub; _runners = runners; _time = time; _sessions = sessions;
        settings.Changed += () => _ = RefreshConnectionsAsync();
        sessions.RunningChanged += UpdateRunning;
    }

    private static Color C(string key) => (Color)Application.Current!.Resources[key];

    public void StartClock()
    {
        Clock = DateTime.Now.ToString("HH:mm");
        Application.Current!.Dispatcher.StartTimer(TimeSpan.FromSeconds(20), () => { Clock = DateTime.Now.ToString("HH:mm"); return true; });
        Application.Current!.Dispatcher.StartTimer(TimeSpan.FromMinutes(10), () => { _ = RefreshHoursAsync(); return true; });
    }

    public void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened).Take(14))
            Projects.Add(new SidebarProject
            {
                Project = p,
                IsRunning = _store.ForProject(p.Id).Any(s => _sessions.Host(s).IsRunning)
            });
    }

    [RelayCommand]
    private void NewSession()
    {
        var p = _data.Project(_data.Projects.Value.ActiveProjectId) ?? _data.Projects.Value.Projects.OrderByDescending(x => x.LastOpened).FirstOrDefault();
        if (p == null) { _nav.Go(Routes.Projects, "new"); return; }
        _nav.Go(Routes.Session, new SessionRequest(p, NewSession: true));
    }

    [RelayCommand] private void NewProject() => _nav.Go(Routes.Projects, "new");
    [RelayCommand] private void OpenProject(CopitProject p) => _nav.Go(Routes.Project, p.Id);

    private void UpdateRunning()
    {
        MainThread.BeginInvokeOnMainThread(() => { foreach (var p in Projects) p.IsRunning = _store.ForProject(p.Project.Id).Any(s => _sessions.Host(s).IsRunning); });
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var n = _sessions.RunningCount;
            AnyRunning = n > 0;
            RunningInfo = n == 0 ? "" : n == 1 ? "1 Session arbeitet" : $"{n} Sessions arbeiten";
        });
    }

    public async Task RefreshConnectionsAsync()
    {
        JiraColor = GitLabColor = C("Muted");
        var tasks = new List<Task>
        {
            Task.Run(async () =>
            {
                if (_hub.Jira == null) { Set(() => { JiraColor = C("Muted"); JiraInfo = "Jira: nicht konfiguriert"; }); return; }
                try { var me = await _hub.Jira.GetMyselfAsync(); Set(() => { JiraColor = C("Green"); JiraInfo = $"Jira: {me.DisplayName}"; }); }
                catch (Exception e) { Set(() => { JiraColor = C("Red"); JiraInfo = "Jira: " + e.Message; }); }
            }),
            Task.Run(async () =>
            {
                if (_hub.GitLab == null) { Set(() => { GitLabColor = C("Muted"); GitLabInfo = "GitLab: nicht konfiguriert"; }); return; }
                try { var me = await _hub.GitLab.GetMeAsync(); Set(() => { GitLabColor = C("Green"); GitLabInfo = $"GitLab: @{me.Username}"; }); }
                catch (Exception e) { Set(() => { GitLabColor = C("Red"); GitLabInfo = "GitLab: " + e.Message; }); }
            }),
            Task.Run(() =>
            {
                var c = _runners.Claude.Resolve();
                var p = _runners.Copilot.Resolve();
                Set(() =>
                {
                    ClaudeColor = c != null ? C("Green") : C("Red");
                    ClaudeInfo = c != null ? $"Claude CLI: {c.DisplayPath}" : "Claude CLI nicht gefunden";
                    CopilotColor = p != null ? C("Green") : C("Red");
                    CopilotInfo = p != null ? $"Copilot CLI: {p.DisplayPath}" : "Copilot CLI nicht gefunden";
                });
            }),
            RefreshHoursAsync()
        };
        await Task.WhenAll(tasks);
    }

    public async Task RefreshHoursAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var target = _time.TargetFor(today);
        try
        {
            var booked = (await _time.GetBookedHoursAsync(today, today)).GetValueOrDefault(today);
            Set(() => TodayHours = $"{booked:0.##}/{target:0.#}h");
        }
        catch { Set(() => TodayHours = $"?/{target:0.#}h"); }
    }

    private static void Set(Action a) => MainThread.BeginInvokeOnMainThread(a);
}
