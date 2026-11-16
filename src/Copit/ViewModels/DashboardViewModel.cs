using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

public class WeekBar
{
    public string Label { get; init; } = "";
    public double Booked { get; init; }
    public double Target { get; init; }
    public bool IsToday { get; init; }
    public double BarHeight => Math.Max(4, Math.Min(1.15, Target > 0 ? Booked / Target : Booked > 0 ? 1 : 0) * 90);
    public Color Color => (Color)Application.Current!.Resources[Target > 0 && Booked + 0.01 >= Target ? "Green" : Booked > 0 ? "Amber" : "Line"];
    public string Text => Booked > 0 ? $"{Booked:0.#}" : "";
    public DateOnly Date { get; init; }
}

public partial class DashboardViewModel : ObservableObject, IActivatable
{
    private readonly DataService _data;
    private readonly ConnectionHub _hub;
    private readonly NavigationService _nav;
    private readonly TimeBookingService _time;
    private readonly GitService _git;
    private readonly SessionStore _sessions;
    private DateTime _lastLoad;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public ObservableCollection<WeekBar> Week { get; } = [];
    public ObservableCollection<ProjectCard> Projects { get; } = [];
    public ObservableCollection<GitLabMergeRequest> Reviews { get; } = [];
    public ObservableCollection<GitLabMergeRequest> MyMrs { get; } = [];
    public ObservableCollection<JiraIssue> InProgress { get; } = [];
    public ObservableCollection<AutomationRun> Runs { get; } = [];

    [ObservableProperty] public partial string Greeting { get; set; } = "";
    [ObservableProperty] public partial string DateText { get; set; } = "";
    [ObservableProperty] public partial double TodayFraction { get; set; }
    [ObservableProperty] public partial string TodayText { get; set; } = "";
    [ObservableProperty] public partial string TodayHint { get; set; } = "";
    [ObservableProperty] public partial string WeekText { get; set; } = "";
    [ObservableProperty] public partial bool Loading { get; set; }

    public bool HasReviews => Reviews.Count > 0;
    public bool HasMyMrs => MyMrs.Count > 0;

    public DashboardViewModel(DataService data, ConnectionHub hub, NavigationService nav, TimeBookingService time, GitService git,
        SessionStore sessions, AutomationService automations)
    {
        _data = data; _hub = hub; _nav = nav; _time = time; _git = git; _sessions = sessions;
        Reviews.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasReviews));
        MyMrs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMyMrs));
        automations.RunsChanged += () => MainThread.BeginInvokeOnMainThread(LoadRuns);
    }

    public void OnActivated(object? parameter)
    {
        var h = DateTime.Now.Hour;
        Greeting = h < 11 ? "Guten Morgen" : h < 17 ? "Guten Tag" : "Guten Abend";
        DateText = DateTime.Today.ToString("dddd, d. MMMM yyyy", De);
        LoadRuns();
        if ((DateTime.Now - _lastLoad).TotalSeconds > 45 || parameter is "refresh") _ = LoadAsync();
    }

    private void LoadRuns()
    {
        Runs.Clear();
        foreach (var r in _data.Automations.Value.Runs.Take(5)) Runs.Add(r);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        _lastLoad = DateTime.Now;
        Loading = true;
        LoadProjects();
        await Task.WhenAll(LoadHoursAsync(), LoadGitLabAsync(), LoadJiraAsync());
        Loading = false;
    }

    private void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened).Take(4))
        {
            var card = new ProjectCard { Project = p, Sessions = _sessions.ForProject(p.Id).Count() };
            Projects.Add(card);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!GitService.IsRepo(p.SourceFolder)) return;
                    var st = await _git.GetStatusAsync(p.SourceFolder);
                    string? pipe = null;
                    if (_hub.GitLab != null && !string.IsNullOrEmpty(p.GitLabProject))
                        pipe = (await _hub.GitLab.GetPipelinesAsync(p.GitLabProject, st.Branch, 1)).FirstOrDefault()?.Status;
                    MainThread.BeginInvokeOnMainThread(() => { card.Branch = st.Branch; card.Changes = st.Changes.Count; card.PipelineStatus = pipe; });
                }
                catch { }
            });
        }
    }

    private async Task LoadHoursAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        Dictionary<DateOnly, double> booked = [];
        if (_hub.Jira != null)
        {
            try { booked = await _time.GetBookedHoursAsync(monday, monday.AddDays(6)); }
            catch (Exception e) { TodayHint = "Jira: " + e.Message; }
        }
        Week.Clear();
        double wb = 0, wt = 0;
        for (int i = 0; i < 7; i++)
        {
            var d = monday.AddDays(i);
            var t = _time.TargetFor(d);
            var b = booked.GetValueOrDefault(d);
            wb += b; wt += t;
            Week.Add(new WeekBar { Label = d.ToString("ddd", De)[..2], Booked = b, Target = t, IsToday = d == today, Date = d });
        }
        var tt = _time.TargetFor(today);
        var tb = booked.GetValueOrDefault(today);
        TodayFraction = tt > 0 ? Math.Min(1, tb / tt) : 0;
        TodayText = $"{tb:0.##}h";
        if (string.IsNullOrEmpty(TodayHint) || !TodayHint.StartsWith("Jira"))
            TodayHint = tt <= 0 ? "Heute ist frei" : tb + 0.01 >= tt ? "vollständig gebucht" : $"noch {tt - tb:0.##}h bis {tt:0.#}h";
        WeekText = $"Woche: {wb:0.#}h / {wt:0.#}h";
    }

    private async Task LoadGitLabAsync()
    {
        Reviews.Clear(); MyMrs.Clear();
        if (_hub.GitLab == null) return;
        try
        {
            foreach (var mr in (await _hub.GitLab.GetReviewRequestsAsync()).Take(6)) Reviews.Add(mr);
            foreach (var mr in (await _hub.GitLab.GetMyMergeRequestsAsync()).Take(6)) MyMrs.Add(mr);
        }
        catch { }
    }

    private async Task LoadJiraAsync()
    {
        InProgress.Clear();
        if (_hub.Jira == null) return;
        try
        {
            foreach (var i in await _hub.Jira.SearchAsync("assignee = currentUser() AND statusCategory = \"In Progress\" ORDER BY updated DESC", 8))
                InProgress.Add(i);
        }
        catch { }
    }

    [RelayCommand] private void BookToday() => _nav.Go(Routes.Time, DateOnly.FromDateTime(DateTime.Today));
    [RelayCommand] private void BookDay(WeekBar w) => _nav.Go(Routes.Time, w.Date);
    [RelayCommand] private void NewProject() => _nav.Go(Routes.Projects, "new");
    [RelayCommand] private void OpenProject(ProjectCard c) => _nav.Go(Routes.Project, c.Project.Id);
    [RelayCommand] private void OpenSession(ProjectCard c) => _nav.Go(Routes.Session, c.Project);
    [RelayCommand] private void Review(GitLabMergeRequest mr) => _nav.Go(Routes.Tools, mr);
    [RelayCommand] private Task OpenMrAsync(GitLabMergeRequest mr) => Dialogs.OpenUrl(mr.WebUrl);
    [RelayCommand] private Task OpenIssueAsync(JiraIssue i) => Dialogs.OpenUrl(i.Url);
    [RelayCommand] private void Standup() => _nav.Go(Routes.Tools, "standup");
    [RelayCommand] private void Automations() => _nav.Go(Routes.Tools, "automations");
}
