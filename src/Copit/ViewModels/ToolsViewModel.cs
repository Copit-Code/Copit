using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

public partial class AutomationItem : ObservableObject
{
    public Automation Model { get; }
    public AutomationItem(Automation m) => Model = m;
    public string Name => Model.Name;
    public string KindText => Model.Kind switch
    {
        AutomationKind.Reminder => "Erinnerung",
        AutomationKind.BookingCheck => "Buchungs-Check",
        AutomationKind.AiPrompt => "KI-Prompt",
        AutomationKind.PipelineWatch => "Pipeline-Wächter",
        AutomationKind.DailyStandup => "Standup",
        _ => Model.Kind.ToString()
    };
    public string Schedule => Model.Kind == AutomationKind.PipelineWatch
        ? $"alle {Model.IntervalMinutes} min"
        : $"{Model.TimeOfDay} · {string.Join(" ", Model.Days.Select(d => d.ToString()[..2]))}";
    public string LastRunText => Model.LastRun == null ? "noch nie" : $"zuletzt {Model.LastRun:dd.MM. HH:mm}";
    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled != value) { Model.Enabled = value; OnPropertyChanged(); EnabledChanged?.Invoke(); } }
    }
    public event Action? EnabledChanged;
    [ObservableProperty] public partial bool Running { get; set; }
    public void Refresh() { OnPropertyChanged(nameof(LastRunText)); OnPropertyChanged(nameof(Schedule)); OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(KindText)); }
}

public partial class ToolsViewModel : ObservableObject, IActivatable
{
    private readonly DataService _data;
    private readonly ConnectionHub _hub;
    private readonly AssistService _assist;
    private readonly GitService _git;
    private readonly TimeBookingService _time;
    private readonly SessionStore _sessions;
    private readonly AutomationService _automations;
    private readonly SettingsService _settings;
    private readonly ToastService _toast;
    private readonly NavigationService _nav;

    public ObservableCollection<GitLabMergeRequest> MergeRequests { get; } = [];
    public ObservableCollection<CopitProject> Projects { get; } = [];
    public ObservableCollection<ReviewFinding> Findings { get; } = [];
    public ObservableCollection<AutomationItem> Automations { get; } = [];
    public ObservableCollection<AutomationRun> Runs { get; } = [];
    public ObservableCollection<PromptSnippet> Snippets { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public string[] Kinds { get; } = ["Erinnerung", "Buchungs-Check", "KI-Prompt", "Pipeline-Wächter", "Standup"];

    [ObservableProperty] public partial string Tab { get; set; } = "review";
    [ObservableProperty] public partial AiProvider Provider { get; set; }
    // Review
    [ObservableProperty] public partial GitLabMergeRequest? SelectedMr { get; set; }
    [ObservableProperty] public partial CopitProject? ReviewProject { get; set; }
    [ObservableProperty] public partial string ReviewSource { get; set; } = "";
    [ObservableProperty] public partial bool Reviewing { get; set; }
    [ObservableProperty] public partial string ReviewStatus { get; set; } = "";
    [ObservableProperty] public partial string ReviewSummary { get; set; } = "";
    [ObservableProperty] public partial string ReviewVerdict { get; set; } = "";
    [ObservableProperty] public partial bool HasReview { get; set; }
    private ReviewResult? _review;
    // Standup
    [ObservableProperty] public partial string StandupText { get; set; } = "";
    [ObservableProperty] public partial bool StandupBusy { get; set; }
    // Changelog
    [ObservableProperty] public partial CopitProject? ChangelogProject { get; set; }
    [ObservableProperty] public partial string ChangelogFrom { get; set; } = "";
    [ObservableProperty] public partial string ChangelogText { get; set; } = "";
    [ObservableProperty] public partial bool ChangelogBusy { get; set; }
    // Automation-Editor
    [ObservableProperty] public partial bool ShowEditor { get; set; }
    [ObservableProperty] public partial Automation? Editing { get; set; }
    [ObservableProperty] public partial string EdName { get; set; } = "";
    [ObservableProperty] public partial string EdKind { get; set; } = "Erinnerung";
    [ObservableProperty] public partial string EdTime { get; set; } = "16:30";
    [ObservableProperty] public partial string EdInterval { get; set; } = "5";
    [ObservableProperty] public partial string EdPrompt { get; set; } = "";
    [ObservableProperty] public partial CopitProject? EdProject { get; set; }
    [ObservableProperty] public partial AutomationRun? SelectedRun { get; set; }

    public bool IsReview => Tab == "review";
    public bool IsStandup => Tab == "standup";
    public bool IsAutomations => Tab == "automations";
    public bool IsChangelog => Tab == "changelog";
    public bool IsPrompts => Tab == "prompts";
    public bool IsClaude => Provider == AiProvider.Claude;
    public bool IsCopilot => Provider == AiProvider.Copilot;
    public bool CanPost => SelectedMr != null && HasReview;
    public bool EdIsWatch => EdKind == "Pipeline-Wächter";
    public bool EdNeedsPrompt => EdKind is "Erinnerung" or "KI-Prompt";

    public ToolsViewModel(DataService data, ConnectionHub hub, AssistService assist, GitService git, TimeBookingService time,
        SessionStore sessions, AutomationService automations, SettingsService settings, ToastService toast, NavigationService nav)
    {
        _data = data; _hub = hub; _assist = assist; _git = git; _time = time; _sessions = sessions;
        _automations = automations; _settings = settings; _toast = toast; _nav = nav;
        Provider = settings.Settings.Ai.DefaultProvider;
        automations.RunsChanged += () => MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var a in Automations) { a.Running = automations.IsRunning(a.Model.Id); a.Refresh(); }
            LoadRuns();
        });
    }

    partial void OnTabChanged(string value)
    {
        foreach (var p in new[] { nameof(IsReview), nameof(IsStandup), nameof(IsAutomations), nameof(IsChangelog), nameof(IsPrompts) }) OnPropertyChanged(p);
    }
    partial void OnProviderChanged(AiProvider value) { OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsCopilot)); }
    partial void OnSelectedMrChanged(GitLabMergeRequest? value) => OnPropertyChanged(nameof(CanPost));
    partial void OnHasReviewChanged(bool value) => OnPropertyChanged(nameof(CanPost));
    partial void OnEdKindChanged(string value) { OnPropertyChanged(nameof(EdIsWatch)); OnPropertyChanged(nameof(EdNeedsPrompt)); }
    partial void OnChangelogProjectChanged(CopitProject? value) => _ = LoadTagsAsync();

    public void OnActivated(object? parameter)
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects) Projects.Add(p);
        LoadAutomations();
        LoadRuns();
        Snippets.Clear();
        foreach (var s in _data.Prompts.Value.Snippets) Snippets.Add(s);

        switch (parameter)
        {
            case GitLabMergeRequest mr:
                Tab = "review";
                _ = LoadMrsAsync(mr);
                break;
            case string t:
                Tab = t;
                break;
        }
        if (IsReview && MergeRequests.Count == 0) _ = LoadMrsAsync(null);
    }

    [RelayCommand] private void SetTab(string t) { Tab = t; if (t == "review" && MergeRequests.Count == 0) _ = LoadMrsAsync(null); }
    [RelayCommand] private void SetProvider(string p) => Provider = Enum.Parse<AiProvider>(p);

    // ---------------- Review ----------------
    [RelayCommand]
    private async Task LoadMrsAsync(GitLabMergeRequest? select)
    {
        MergeRequests.Clear();
        if (_hub.GitLab == null) return;
        try
        {
            var list = (await _hub.GitLab.GetReviewRequestsAsync()).Concat(await _hub.GitLab.GetMyMergeRequestsAsync()).DistinctBy(m => (m.ProjectId, m.Iid));
            foreach (var m in list) MergeRequests.Add(m);
            if (select != null)
            {
                SelectedMr = MergeRequests.FirstOrDefault(m => m.ProjectId == select.ProjectId && m.Iid == select.Iid) ?? select;
                if (!MergeRequests.Contains(SelectedMr)) MergeRequests.Insert(0, SelectedMr);
                ReviewSource = $"MR !{SelectedMr.Iid}: {SelectedMr.Title}";
                await ReviewAsync();
            }
        }
        catch (Exception e) { _toast.Error(e, "Merge Requests"); }
    }

    [RelayCommand]
    private void PickMr(GitLabMergeRequest mr)
    {
        SelectedMr = mr; ReviewProject = null;
        ReviewSource = $"MR !{mr.Iid}: {mr.Title}";
    }

    [RelayCommand]
    private void PickLocal(CopitProject p)
    {
        ReviewProject = p; SelectedMr = null;
        ReviewSource = $"Lokale Änderungen in {p.Name}";
    }

    [RelayCommand]
    private async Task ReviewAsync()
    {
        if (Reviewing) return;
        if (SelectedMr == null && ReviewProject == null) { _toast.Show("Quelle wählen", "Wähle einen Merge Request oder ein Projekt.", NoticeLevel.Warning); return; }
        Reviewing = true; HasReview = false; Findings.Clear(); ReviewSummary = ""; ReviewVerdict = "";
        ReviewStatus = "Lade Diff…";
        try
        {
            string diff, context; string? dir = null;
            if (SelectedMr != null)
            {
                diff = await _hub.RequireGitLab.GetMergeRequestDiffAsync(SelectedMr.ProjectId.ToString(), SelectedMr.Iid);
                context = $"GitLab MR \"{SelectedMr.Title}\" ({SelectedMr.SourceBranch} → {SelectedMr.TargetBranch}). Beschreibung: {SelectedMr.Description}";
                var proj = _data.Projects.Value.Projects.FirstOrDefault(p => !string.IsNullOrEmpty(p.GitLabProject) && SelectedMr.WebUrl.Contains("/" + p.GitLabProject + "/", StringComparison.OrdinalIgnoreCase));
                dir = proj?.SourceFolder;
            }
            else
            {
                dir = ReviewProject!.SourceFolder;
                diff = await _git.GetFullDiffAsync(dir, 120_000);
                context = $"Lokale, noch nicht committete Änderungen im Projekt {ReviewProject.Name}.";
            }
            if (string.IsNullOrWhiteSpace(diff)) { ReviewStatus = ""; _toast.Show("Keine Änderungen gefunden"); return; }
            _review = await _assist.ReviewAsync(diff, context, dir, Provider, s => MainThread.BeginInvokeOnMainThread(() => ReviewStatus = s));
            ReviewSummary = _review.Summary;
            ReviewVerdict = _review.Verdict;
            var order = new[] { "critical", "major", "minor", "info" };
            foreach (var f in _review.Findings.OrderBy(f => Array.IndexOf(order, f.Severity) is var i && i < 0 ? 9 : i)) Findings.Add(f);
            HasReview = true;
        }
        catch (Exception e) { _toast.Error(e, "Review fehlgeschlagen"); }
        finally { Reviewing = false; ReviewStatus = ""; }
    }

    [RelayCommand]
    private async Task PostReviewAsync()
    {
        if (SelectedMr == null || _review == null) return;
        if (!await Dialogs.Confirm("Review posten?", "Das Review wird als Kommentar im Merge Request veröffentlicht.", "Posten")) return;
        try
        {
            await _hub.RequireGitLab.PostMergeRequestNoteAsync(SelectedMr.ProjectId.ToString(), SelectedMr.Iid, AssistService.ReviewToMarkdown(_review));
            _toast.Success("Review gepostet", SelectedMr.Title);
        }
        catch (Exception e) { _toast.Error(e, "Posten fehlgeschlagen"); }
    }

    [RelayCommand]
    private void FixInSession(ReviewFinding? f)
    {
        var project = ReviewProject ?? (SelectedMr != null
            ? _data.Projects.Value.Projects.FirstOrDefault(p => !string.IsNullOrEmpty(p.GitLabProject) && SelectedMr.WebUrl.Contains("/" + p.GitLabProject + "/", StringComparison.OrdinalIgnoreCase))
            : null);
        if (project == null) { _toast.Show("Kein passendes Projekt", "Lege das Projekt in Copit an, um Fixes per Session umzusetzen.", NoticeLevel.Warning); return; }
        List<ReviewFinding> items = f != null ? [f] : Findings.ToList();
        var text = string.Join("\n", items.Select(x => $"- [{x.Severity}] {x.File}:{x.Line} – {x.Title}: {x.Detail} Vorschlag: {x.Suggestion}"));
        _nav.Go(Routes.Session, new SessionRequest(project,
            $"Behebe folgende Punkte aus einem Code-Review:\n{text}",
            f != null ? $"Review-Fix: {f.Title}" : $"{items.Count} Review-Punkte beheben", true));
    }

    // ---------------- Standup ----------------
    [RelayCommand]
    private async Task StandupAsync()
    {
        StandupBusy = true; StandupText = "";
        try
        {
            var y = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
            while (!_time.IsWorkDay(y)) y = y.AddDays(-1);
            var ctx = await _time.GatherAsync(y, _time.TargetFor(y), _sessions.DescribeDay(y, _data), _ => { });
            var inProgress = _hub.Jira != null ? await _hub.Jira.SearchAsync("assignee = currentUser() AND statusCategory = \"In Progress\"", 20) : [];
            StandupText = await _assist.StandupAsync(ctx, inProgress, Provider, _ => { });
        }
        catch (Exception e) { _toast.Error(e, "Standup"); }
        finally { StandupBusy = false; }
    }

    [RelayCommand]
    private async Task CopyAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await Clipboard.Default.SetTextAsync(text);
        _toast.Success("In die Zwischenablage kopiert");
    }

    // ---------------- Release Notes ----------------
    private async Task LoadTagsAsync()
    {
        Tags.Clear();
        if (ChangelogProject == null || !GitService.IsRepo(ChangelogProject.SourceFolder)) return;
        foreach (var t in await _git.GetTagsAsync(ChangelogProject.SourceFolder)) Tags.Add(t);
        ChangelogFrom = Tags.FirstOrDefault() ?? "";
    }

    [RelayCommand] private void PickChangelogProject(CopitProject p) => ChangelogProject = p;
    [RelayCommand] private void PickTag(string t) => ChangelogFrom = t;

    [RelayCommand]
    private async Task ChangelogAsync()
    {
        if (ChangelogProject == null) return;
        ChangelogBusy = true; ChangelogText = "";
        try { ChangelogText = await _assist.ChangelogAsync(ChangelogProject.SourceFolder, ChangelogFrom, Provider, _ => { }); }
        catch (Exception e) { _toast.Error(e, "Release Notes"); }
        finally { ChangelogBusy = false; }
    }

    // ---------------- Automationen ----------------
    private void LoadAutomations()
    {
        Automations.Clear();
        foreach (var a in _data.Automations.Value.Automations)
        {
            var item = new AutomationItem(a) { Running = _automations.IsRunning(a.Id) };
            item.EnabledChanged += () => _ = _data.Automations.SaveAsync();
            Automations.Add(item);
        }
    }

    private void LoadRuns()
    {
        Runs.Clear();
        foreach (var r in _data.Automations.Value.Runs.Take(40)) Runs.Add(r);
    }

    [RelayCommand]
    private async Task RunNowAsync(AutomationItem a)
    {
        a.Running = true;
        await _automations.RunAsync(a.Model);
        a.Running = false;
        a.Refresh();
        SelectedRun = _data.Automations.Value.Runs.FirstOrDefault(r => r.AutomationId == a.Model.Id);
    }

    [RelayCommand] private void ShowRun(AutomationRun r) => SelectedRun = r;
    [RelayCommand] private void CloseRun() => SelectedRun = null;

    private static AutomationKind KindFrom(string s) => s switch
    {
        "Buchungs-Check" => AutomationKind.BookingCheck,
        "KI-Prompt" => AutomationKind.AiPrompt,
        "Pipeline-Wächter" => AutomationKind.PipelineWatch,
        "Standup" => AutomationKind.DailyStandup,
        _ => AutomationKind.Reminder
    };

    private static string KindName(AutomationKind k) => k switch
    {
        AutomationKind.BookingCheck => "Buchungs-Check",
        AutomationKind.AiPrompt => "KI-Prompt",
        AutomationKind.PipelineWatch => "Pipeline-Wächter",
        AutomationKind.DailyStandup => "Standup",
        _ => "Erinnerung"
    };

    [RelayCommand]
    private void NewAutomation()
    {
        Editing = null;
        EdName = ""; EdKind = "KI-Prompt"; EdTime = "08:30"; EdInterval = "5"; EdPrompt = ""; EdProject = Projects.FirstOrDefault();
        ShowEditor = true;
    }

    [RelayCommand]
    private void EditAutomation(AutomationItem a)
    {
        Editing = a.Model;
        EdName = a.Model.Name; EdKind = KindName(a.Model.Kind); EdTime = a.Model.TimeOfDay; EdInterval = a.Model.IntervalMinutes.ToString();
        EdPrompt = a.Model.Prompt; EdProject = _data.Project(a.Model.ProjectId);
        ShowEditor = true;
    }

    [RelayCommand] private void PickKind(string k) => EdKind = k;
    [RelayCommand] private void PickEdProject(CopitProject p) => EdProject = p;
    [RelayCommand] private void CloseEditor() => ShowEditor = false;

    [RelayCommand]
    private async Task SaveAutomationAsync()
    {
        if (string.IsNullOrWhiteSpace(EdName)) { _toast.Show("Name fehlt", "", NoticeLevel.Warning); return; }
        var a = Editing ?? new Automation();
        a.Name = EdName.Trim(); a.Kind = KindFrom(EdKind); a.TimeOfDay = EdTime.Trim();
        a.IntervalMinutes = int.TryParse(EdInterval, out var i) ? Math.Max(1, i) : 5;
        a.Prompt = EdPrompt; a.ProjectId = EdProject?.Id; a.Provider = Provider;
        if (Editing == null) _data.Automations.Value.Automations.Add(a);
        await _data.Automations.SaveAsync();
        ShowEditor = false;
        LoadAutomations();
        _toast.Success("Automation gespeichert", a.Name);
    }

    [RelayCommand]
    private async Task DeleteAutomationAsync(AutomationItem a)
    {
        if (!await Dialogs.Confirm("Automation löschen?", a.Name, "Löschen")) return;
        _data.Automations.Value.Automations.Remove(a.Model);
        await _data.Automations.SaveAsync();
        LoadAutomations();
    }

    // ---------------- Prompt-Bibliothek ----------------
    [RelayCommand]
    private void AddSnippet()
    {
        var s = new PromptSnippet { Title = "Neuer Prompt", Text = "" };
        _data.Prompts.Value.Snippets.Add(s);
        Snippets.Add(s);
    }

    [RelayCommand]
    private async Task DeleteSnippetAsync(PromptSnippet s)
    {
        _data.Prompts.Value.Snippets.Remove(s);
        Snippets.Remove(s);
        await _data.Prompts.SaveAsync();
    }

    [RelayCommand]
    private async Task SaveSnippetsAsync()
    {
        await _data.Prompts.SaveAsync();
        _toast.Success("Prompt-Bibliothek gespeichert");
    }
}
