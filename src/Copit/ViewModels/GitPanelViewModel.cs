using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

/// <summary>Git + GitLab Panel: Branch, Änderungen, Pipeline, Merge Request.</summary>
public partial class GitPanelViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly ConnectionHub _hub;
    private readonly AssistService _assist;
    private readonly ToastService _toast;
    private readonly SettingsService _settings;
    private CopitProject? _project;
    private bool _refreshing;

    public ObservableCollection<GitChange> Changes { get; } = [];
    public ObservableCollection<GitLabJob> Jobs { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    public ObservableCollection<GitCommit> Commits { get; } = [];

    [ObservableProperty] public partial string Branch { get; set; } = "";
    [ObservableProperty] public partial string SyncInfo { get; set; } = "";
    [ObservableProperty] public partial bool IsRepo { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial GitLabPipeline? Pipeline { get; set; }
    [ObservableProperty] public partial GitLabMergeRequest? MergeRequest { get; set; }
    [ObservableProperty] public partial string PipelineError { get; set; } = "";
    [ObservableProperty] public partial bool ShowDiff { get; set; }
    [ObservableProperty] public partial string DiffText { get; set; } = "";
    [ObservableProperty] public partial string DiffTitle { get; set; } = "";
    [ObservableProperty] public partial GitChange? DiffChange { get; set; }
    [ObservableProperty] public partial bool ShowCommit { get; set; }
    [ObservableProperty] public partial string CommitMessage { get; set; } = "";
    [ObservableProperty] public partial bool GeneratingMessage { get; set; }
    [ObservableProperty] public partial bool ShowLog { get; set; }
    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial string LogTitle { get; set; } = "";
    [ObservableProperty] public partial GitLabJob? LogJob { get; set; }
    [ObservableProperty] public partial string Explanation { get; set; } = "";
    [ObservableProperty] public partial bool Explaining { get; set; }
    [ObservableProperty] public partial string LastRefresh { get; set; } = "";

    public bool HasChanges => Changes.Count > 0;
    public string ChangesTitle => Changes.Count == 0 ? "Keine Änderungen" : $"{Changes.Count} Änderung{(Changes.Count == 1 ? "" : "en")}";
    public bool HasPipeline => Pipeline != null;
    public bool HasMr => MergeRequest != null;
    public bool CanCreateMr => MergeRequest == null && IsRepo && _project != null && !string.IsNullOrEmpty(_project.GitLabProject) && _hub.GitLab != null;
    public bool PipelineFailed => Pipeline?.Status == "failed";
    public bool HasGitLab => IsRepo && _project != null && !string.IsNullOrEmpty(_project.GitLabProject) && _hub.GitLab != null;
    public bool CanRunPipeline => HasGitLab && Pipeline == null && !string.IsNullOrEmpty(Branch);
    partial void OnIsRepoChanged(bool value) { OnPropertyChanged(nameof(HasGitLab)); OnPropertyChanged(nameof(CanRunPipeline)); }
    partial void OnBranchChanged(string value) => OnPropertyChanged(nameof(CanRunPipeline));

    /// <summary>Wird ausgelöst, wenn die KI etwas in der Session tun soll (Prompt, Anzeigetext).</summary>
    public event Action<string, string>? SendToSession;

    public GitPanelViewModel(GitService git, ConnectionHub hub, AssistService assist, ToastService toast, SettingsService settings)
    {
        _git = git; _hub = hub; _assist = assist; _toast = toast; _settings = settings;
        Changes.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(ChangesTitle)); };
    }

    partial void OnPipelineChanged(GitLabPipeline? value) { OnPropertyChanged(nameof(HasPipeline)); OnPropertyChanged(nameof(PipelineFailed)); OnPropertyChanged(nameof(CanRunPipeline)); }
    partial void OnMergeRequestChanged(GitLabMergeRequest? value) { OnPropertyChanged(nameof(HasMr)); OnPropertyChanged(nameof(CanCreateMr)); }

    private string Dir => _project?.SourceFolder ?? "";

    public void SetProject(CopitProject? p)
    {
        if (_project?.Id == p?.Id) return;
        _project = p;
        Changes.Clear(); Jobs.Clear(); Branches.Clear(); Commits.Clear();
        Pipeline = null; MergeRequest = null; Branch = ""; SyncInfo = ""; Error = "";
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_project == null || _refreshing) return;
        _refreshing = true;
        IsBusy = true;
        try
        {
            IsRepo = GitService.IsRepo(Dir);
            if (!IsRepo) { Error = Directory.Exists(Dir) ? "Kein Git-Repository" : "Quellordner nicht gefunden"; return; }
            Error = "";
            var st = await _git.GetStatusAsync(Dir);
            Branch = st.Branch;
            SyncInfo = st.Upstream == null ? "nicht gepusht" : (st.Ahead == 0 && st.Behind == 0 ? "synchron" : $"↑{st.Ahead} ↓{st.Behind}");
            Sync(Changes, st.Changes, (a, b) => a.Path == b.Path && a.Status == b.Status);
            var branches = await _git.GetBranchesAsync(Dir);
            Sync(Branches, branches, (a, b) => a == b);
            var commits = await _git.GetRecentCommitsAsync(Dir, 6);
            Sync(Commits, commits, (a, b) => a.Hash == b.Hash);
            await RefreshGitLabAsync();
            LastRefresh = DateTime.Now.ToString("HH:mm:ss");
            OnPropertyChanged(nameof(CanCreateMr));
        }
        catch (Exception e) { Error = e.Message; }
        finally { IsBusy = false; _refreshing = false; }
    }

    private async Task RefreshGitLabAsync()
    {
        var gl = _hub.GitLab;
        if (gl == null || _project == null || string.IsNullOrEmpty(_project.GitLabProject) || string.IsNullOrEmpty(Branch)) { PipelineError = gl == null ? "GitLab nicht konfiguriert" : ""; return; }
        try
        {
            var pipe = (await gl.GetPipelinesAsync(_project.GitLabProject, Branch, 1)).FirstOrDefault();
            Pipeline = pipe;
            if (pipe != null)
            {
                var jobs = await gl.GetJobsAsync(_project.GitLabProject, pipe.Id);
                Sync(Jobs, jobs, (a, b) => a.Id == b.Id && a.Status == b.Status);
            }
            else Jobs.Clear();
            MergeRequest = (await gl.GetMergeRequestsAsync(_project.GitLabProject, "opened", Branch)).FirstOrDefault();
            PipelineError = "";
        }
        catch (Exception e) { PipelineError = e.Message; }
    }

    private static void Sync<T>(ObservableCollection<T> target, IList<T> source, Func<T, T, bool> same)
    {
        if (target.Count == source.Count && target.Zip(source).All(p => same(p.First, p.Second))) return;
        target.Clear();
        foreach (var s in source) target.Add(s);
    }

    [RelayCommand]
    private async Task OpenDiffAsync(GitChange c)
    {
        DiffChange = c;
        DiffTitle = c.Path;
        DiffText = "Lade Diff…";
        ShowDiff = true;
        try { DiffText = await _git.GetDiffAsync(Dir, c.Path); if (string.IsNullOrWhiteSpace(DiffText)) DiffText = "(keine Textänderungen)"; }
        catch (Exception e) { DiffText = e.Message; }
    }

    [RelayCommand]
    private async Task OpenFullDiffAsync()
    {
        DiffChange = null;
        DiffTitle = "Alle Änderungen";
        ShowDiff = true;
        DiffText = "Lade…";
        DiffText = await _git.GetFullDiffAsync(Dir);
    }

    [RelayCommand] private void CloseDiff() => ShowDiff = false;

    [RelayCommand]
    private async Task DiscardAsync(GitChange? c)
    {
        c ??= DiffChange;
        if (c == null) return;
        if (!await Dialogs.Confirm("Änderung verwerfen?", $"{c.Path} wird auf den letzten Commit zurückgesetzt.", "Verwerfen")) return;
        await _git.DiscardAsync(Dir, c.Path);
        ShowDiff = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task StartCommitAsync()
    {
        ShowCommit = true;
        if (!string.IsNullOrWhiteSpace(CommitMessage)) return;
        await GenerateMessageAsync();
    }

    [RelayCommand]
    private async Task GenerateMessageAsync()
    {
        GeneratingMessage = true;
        try
        {
            var key = TimeBookingService.IssueKeyRegex().Match(Branch) is { Success: true } m ? m.Value.ToUpperInvariant() : null;
            CommitMessage = await _assist.CommitMessageAsync(Dir, _project?.Provider ?? _settings.Settings.Ai.DefaultProvider, key, _ => { });
        }
        catch (Exception e) { _toast.Error(e, "Commit-Message"); }
        finally { GeneratingMessage = false; }
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage)) return;
        IsBusy = true;
        try
        {
            await _git.CommitAllAsync(Dir, CommitMessage.Trim());
            _toast.Success("Commit erstellt", CommitMessage.Split('\n')[0]);
            CommitMessage = ""; ShowCommit = false;
            await RefreshAsync();
        }
        catch (Exception e) { _toast.Error(e, "Commit fehlgeschlagen"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CommitAndPushAsync()
    {
        await CommitAsync();
        if (!ShowCommit) await PushAsync();
    }

    [RelayCommand] private void CancelCommit() => ShowCommit = false;

    [RelayCommand]
    private async Task PushAsync()
    {
        IsBusy = true;
        try { await _git.PushAsync(Dir); _toast.Success("Push erfolgreich", Branch); await Task.Delay(1500); await RefreshAsync(); }
        catch (Exception e) { _toast.Error(e, "Push fehlgeschlagen"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        IsBusy = true;
        try { await _git.PullAsync(Dir); _toast.Success("Pull erfolgreich", Branch); await RefreshAsync(); }
        catch (Exception e) { _toast.Error(e, "Pull fehlgeschlagen"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SwitchBranchAsync()
    {
        var options = Branches.Where(b => b != Branch).Prepend("Neuer Branch…").ToArray();
        var choice = await Dialogs.Choose("Branch wechseln", options);
        if (string.IsNullOrEmpty(choice) || choice == "Abbrechen") return;
        try
        {
            if (choice.StartsWith("Neuer Branch"))
            {
                var name = await Dialogs.Prompt("Neuer Branch", "Name (z.B. feature/ABC-123-login)", "feature/");
                if (string.IsNullOrWhiteSpace(name)) return;
                await _git.CreateBranchAsync(Dir, name.Trim());
            }
            else await _git.CheckoutAsync(Dir, choice);
            await RefreshAsync();
        }
        catch (Exception e) { _toast.Error(e, "Branch"); }
    }

    public async Task CreateBranchForIssueAsync(JiraIssue issue)
    {
        var slug = new string(issue.Summary.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        var name = await Dialogs.Prompt("Branch für Ticket", issue.Key, $"feature/{issue.Key}-{slug}");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await _git.CreateBranchAsync(Dir, name.Trim()); await RefreshAsync(); }
        catch (Exception e) { _toast.Error(e, "Branch"); }
    }

    [RelayCommand]
    private async Task RetryPipelineAsync()
    {
        if (Pipeline == null || _project == null) return;
        try { await _hub.RequireGitLab.RetryPipelineAsync(_project.GitLabProject, Pipeline.Id); _toast.Show("Pipeline neu gestartet", $"#{Pipeline.Id}"); await Task.Delay(1500); await RefreshAsync(); }
        catch (Exception e) { _toast.Error(e, "Pipeline"); }
    }

    [RelayCommand]
    private async Task RunPipelineAsync()
    {
        if (_project == null || string.IsNullOrEmpty(Branch)) return;
        try { await _hub.RequireGitLab.RunPipelineAsync(_project.GitLabProject, Branch); _toast.Show("Pipeline gestartet", Branch); await Task.Delay(1500); await RefreshAsync(); }
        catch (Exception e) { _toast.Error(e, "Pipeline"); }
    }

    [RelayCommand] private Task OpenPipelineAsync() => Dialogs.OpenUrl(Pipeline?.WebUrl);
    [RelayCommand] private Task OpenMrAsync() => Dialogs.OpenUrl(MergeRequest?.WebUrl);
    [RelayCommand] private Task OpenJobAsync(GitLabJob j) => Dialogs.OpenUrl(j.WebUrl);

    [RelayCommand]
    private async Task ShowJobLogAsync(GitLabJob j)
    {
        if (_project == null) return;
        LogJob = j;
        LogTitle = $"{j.Stage} · {j.Name}";
        LogText = "Lade Log…";
        Explanation = "";
        ShowLog = true;
        try { LogText = await _hub.RequireGitLab.GetJobTraceAsync(_project.GitLabProject, j.Id, 200); }
        catch (Exception e) { LogText = e.Message; }
    }

    [RelayCommand] private void CloseLog() => ShowLog = false;

    [RelayCommand]
    private async Task ExplainLogAsync()
    {
        if (LogJob == null) return;
        Explaining = true;
        try { Explanation = await _assist.ExplainPipelineFailureAsync(LogText, LogJob.Name, _project?.Provider ?? _settings.Settings.Ai.DefaultProvider, Dir, _ => { }); }
        catch (Exception e) { Explanation = "Fehler: " + e.Message; }
        finally { Explaining = false; }
    }

    [RelayCommand]
    private async Task FixInSessionAsync(GitLabJob? j)
    {
        j ??= LogJob;
        if (j == null || _project == null) return;
        var log = LogJob?.Id == j.Id && !LogText.StartsWith("Lade") ? LogText : await _hub.RequireGitLab.GetJobTraceAsync(_project.GitLabProject, j.Id, 150);
        ShowLog = false;
        SendToSession?.Invoke(
            $"Der GitLab-CI-Job \"{j.Name}\" (Stage {j.Stage}) auf Branch {Branch} ist fehlgeschlagen. Analysiere das Log, finde die Ursache im Code und behebe sie. Führe danach – falls möglich – den betroffenen Build/Test lokal aus.\n\n```\n{log}\n```",
            $"Pipeline-Fehler beheben: {j.Stage} · {j.Name}");
    }

    [RelayCommand]
    private async Task CreateMrAsync()
    {
        if (_project == null) return;
        var gl = _hub.RequireGitLab;
        IsBusy = true;
        try
        {
            var info = await gl.GetProjectAsync(_project.GitLabProject);
            var target = await Dialogs.Prompt("Merge Request", "Ziel-Branch", info.DefaultBranch);
            if (string.IsNullOrWhiteSpace(target)) return;
            var st = await _git.GetStatusAsync(Dir);
            if (st.Upstream == null || st.Ahead > 0) await _git.PushAsync(Dir);
            _toast.Show("KI schreibt MR-Beschreibung…");
            var (title, desc) = await _assist.MergeRequestTextAsync(Dir, target, _project.Provider ?? _settings.Settings.Ai.DefaultProvider, _ => { });
            title = await Dialogs.Prompt("MR-Titel", "Titel prüfen", title) ?? "";
            if (string.IsNullOrWhiteSpace(title)) return;
            MergeRequest = await gl.CreateMergeRequestAsync(_project.GitLabProject, Branch, target, title, desc);
            _toast.Success("Merge Request erstellt", title);
        }
        catch (Exception e) { _toast.Error(e, "Merge Request"); }
        finally { IsBusy = false; }
    }
}
