using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

public partial class ToolsViewModel : ObservableObject, IActivatable
{
    private readonly DataService _data;
    private readonly ConnectionHub _hub;
    private readonly AssistService _assist;
    private readonly GitService _git;
    private readonly TimeBookingService _time;
    private readonly SessionStore _sessions;
    private readonly SettingsService _settings;
    private readonly ActivityService _activity;
    private readonly ToastService _toast;
    private readonly NavigationService _nav;

    public ObservableCollection<GitLabMergeRequest> MergeRequests { get; } = [];
    public ObservableCollection<CopitProject> Projects { get; } = [];
    public ObservableCollection<ReviewFinding> Findings { get; } = [];
    public ObservableCollection<PromptSnippet> Snippets { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];

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

    public bool IsReview => Tab == "review";
    public bool IsStandup => Tab == "standup";
    public bool IsChangelog => Tab == "changelog";
    public bool IsPrompts => Tab == "prompts";
    public bool IsClaude => Provider == AiProvider.Claude;
    public bool IsCopilot => Provider == AiProvider.Copilot;
    public bool CanPost => SelectedMr != null && HasReview;

    public ToolsViewModel(DataService data, ConnectionHub hub, AssistService assist, GitService git, TimeBookingService time,
        SessionStore sessions, SettingsService settings, ToastService toast, NavigationService nav, ActivityService activity)
    {
        _data = data; _hub = hub; _assist = assist; _git = git; _time = time; _sessions = sessions;
        _settings = settings; _activity = activity; _toast = toast; _nav = nav;
        Provider = settings.Settings.Ai.DefaultProvider;
    }

    partial void OnTabChanged(string value)
    {
        foreach (var p in new[] { nameof(IsReview), nameof(IsStandup), nameof(IsChangelog), nameof(IsPrompts) }) OnPropertyChanged(p);
    }
    partial void OnProviderChanged(AiProvider value) { OnPropertyChanged(nameof(IsClaude)); OnPropertyChanged(nameof(IsCopilot)); }
    partial void OnSelectedMrChanged(GitLabMergeRequest? value) => OnPropertyChanged(nameof(CanPost));
    partial void OnHasReviewChanged(bool value) => OnPropertyChanged(nameof(CanPost));
    partial void OnChangelogProjectChanged(CopitProject? value) => _ = LoadTagsAsync();

    public void OnActivated(object? parameter)
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects) Projects.Add(p);
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
            var ctx = await _time.GatherAsync(y, _time.TargetFor(y), Sessions(y), _ => { });
            var inProgress = _hub.Jira != null ? await _hub.Jira.SearchAsync("assignee = currentUser() AND statusCategory = \"In Progress\"", 20) : [];
            StandupText = await _assist.StandupAsync(ctx, inProgress, Provider, _ => { });
        }
        catch (Exception e) { _toast.Error(e, "Standup"); }
        finally { StandupBusy = false; }
    }

    private IEnumerable<DaySession> Sessions(DateOnly day)
    {
        var from = day.ToDateTime(TimeOnly.MinValue);
        return _activity.Build(from, from.AddDays(1), _sessions.All, id => _data.Project(id)?.SourceFolder)
            .Select(a => new DaySession(a.Title, a.Project, a.Branch, a.Ticket, (int)ActivityService.Union(a.Segments).TotalMinutes));
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
