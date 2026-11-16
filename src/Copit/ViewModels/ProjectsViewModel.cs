using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

public partial class ProjectCard : ObservableObject
{
    public CopitProject Project { get; init; } = null!;
    [ObservableProperty] public partial string Branch { get; set; } = "";
    [ObservableProperty] public partial int Changes { get; set; }
    [ObservableProperty] public partial string? PipelineStatus { get; set; }
    [ObservableProperty] public partial string EpicSummary { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial int Sessions { get; set; }
    public bool HasPipeline => PipelineStatus != null;
    partial void OnPipelineStatusChanged(string? value) => OnPropertyChanged(nameof(HasPipeline));
}

/// <summary>Projekt anlegen / bearbeiten (gemeinsam genutzt von Liste und Detail).</summary>
public partial class ProjectEditor : ObservableObject
{
    private readonly GitService _git;
    private readonly ConnectionHub _hub;
    public string[] Palette { get; } = ["#8B5CF6", "#22D3EE", "#34D399", "#FBBF24", "#F472B6", "#F87171", "#60A5FA", "#A3E635"];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string? EditingId { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Epic { get; set; } = "";
    [ObservableProperty] public partial string Folder { get; set; } = "";
    [ObservableProperty] public partial string GitLab { get; set; } = "";
    [ObservableProperty] public partial string Color { get; set; } = "#8B5CF6";
    [ObservableProperty] public partial string Instructions { get; set; } = "";
    [ObservableProperty] public partial string EpicInfo { get; set; } = "";
    [ObservableProperty] public partial bool EpicOk { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    public string Title => EditingId == null ? "Neues Projekt" : "Projekt bearbeiten";
    partial void OnEditingIdChanged(string? value) => OnPropertyChanged(nameof(Title));

    public event Func<CopitProject, Task>? Saved;

    public ProjectEditor(GitService git, ConnectionHub hub) { _git = git; _hub = hub; }

    public void Open(CopitProject? p)
    {
        EditingId = p?.Id;
        Name = p?.Name ?? ""; Epic = p?.EpicKey ?? ""; Folder = p?.SourceFolder ?? ""; GitLab = p?.GitLabProject ?? "";
        Color = p?.Color ?? Palette[Random.Shared.Next(Palette.Length)]; Instructions = p?.Instructions ?? "";
        EpicInfo = ""; EpicOk = false; Error = "";
        IsOpen = true;
        if (!string.IsNullOrEmpty(Epic)) _ = CheckEpicAsync();
    }

    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private void PickColor(string c) => Color = c;

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var f = await Dialogs.PickFolderAsync();
        if (string.IsNullOrEmpty(f)) return;
        Folder = f;
        if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileName(f.TrimEnd('\\'));
        var remote = await _git.GetRemoteUrlAsync(f);
        var path = remote != null ? GitLabClient.ProjectPathFromRemote(remote) : null;
        if (path != null) GitLab = path;
    }

    [RelayCommand]
    private async Task CheckEpicAsync()
    {
        EpicOk = false;
        if (string.IsNullOrWhiteSpace(Epic)) { EpicInfo = ""; return; }
        if (_hub.Jira == null) { EpicInfo = "Jira nicht konfiguriert – Epic wird später geladen."; return; }
        EpicInfo = "Prüfe…";
        try
        {
            var issue = await _hub.Jira.GetIssueAsync(Epic.Trim().ToUpperInvariant());
            if (issue == null) { EpicInfo = "Epic nicht gefunden"; return; }
            EpicInfo = $"{issue.IssueType}: {issue.Summary}";
            EpicOk = true;
            if (string.IsNullOrWhiteSpace(Name)) Name = issue.Summary;
        }
        catch (Exception e) { EpicInfo = e.Message; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Name fehlt."; return; }
        if (string.IsNullOrWhiteSpace(Epic)) { Error = "Epic-Key fehlt (z.B. SHOP-100)."; return; }
        var p = new CopitProject
        {
            Id = EditingId ?? Guid.NewGuid().ToString("N"),
            Name = Name.Trim(), EpicKey = Epic.Trim().ToUpperInvariant(), SourceFolder = Folder.Trim(),
            GitLabProject = GitLab.Trim(), Color = Color, Instructions = Instructions
        };
        IsOpen = false;
        if (Saved != null) await Saved(p);
    }
}

public partial class ProjectsViewModel : ObservableObject, IActivatable
{
    private readonly DataService _data;
    private readonly GitService _git;
    private readonly ConnectionHub _hub;
    private readonly NavigationService _nav;
    private readonly SessionStore _sessions;
    private readonly ToastService _toast;

    public ObservableCollection<ProjectCard> Projects { get; } = [];
    public ProjectEditor Editor { get; }
    public bool IsEmpty => Projects.Count == 0;

    public ProjectsViewModel(DataService data, GitService git, ConnectionHub hub, NavigationService nav, SessionStore sessions, ToastService toast)
    {
        _data = data; _git = git; _hub = hub; _nav = nav; _sessions = sessions; _toast = toast;
        Editor = new ProjectEditor(git, hub);
        Editor.Saved += SaveProjectAsync;
        Projects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task SaveProjectAsync(CopitProject p)
    {
        var list = _data.Projects.Value.Projects;
        var existing = list.FirstOrDefault(x => x.Id == p.Id);
        if (existing != null)
        {
            existing.Name = p.Name; existing.EpicKey = p.EpicKey; existing.SourceFolder = p.SourceFolder;
            existing.GitLabProject = p.GitLabProject; existing.Color = p.Color; existing.Instructions = p.Instructions;
        }
        else list.Add(p);
        await _data.Projects.SaveAsync();
        _data.RaiseProjectsChanged();
        _toast.Success(existing == null ? "Projekt angelegt" : "Projekt gespeichert", p.Name);
        Load();
        if (existing == null) _nav.Go(Routes.Project, p.Id);
    }

    public void OnActivated(object? parameter)
    {
        Load();
        if (parameter is "new") Editor.Open(null);
    }

    private void Load()
    {
        Projects.Clear();
        foreach (var p in _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened))
        {
            var card = new ProjectCard { Project = p, Sessions = _sessions.ForProject(p.Id).Count() };
            Projects.Add(card);
            _ = EnrichAsync(card);
        }
    }

    private async Task EnrichAsync(ProjectCard card)
    {
        var p = card.Project;
        try
        {
            if (GitService.IsRepo(p.SourceFolder))
            {
                var st = await _git.GetStatusAsync(p.SourceFolder);
                card.Branch = st.Branch; card.Changes = st.Changes.Count;
                if (_hub.GitLab != null && !string.IsNullOrEmpty(p.GitLabProject))
                    card.PipelineStatus = (await _hub.GitLab.GetPipelinesAsync(p.GitLabProject, st.Branch, 1)).FirstOrDefault()?.Status;
            }
            if (_hub.Jira != null && !string.IsNullOrEmpty(p.EpicKey))
            {
                var epic = await _hub.Jira.GetIssueAsync(p.EpicKey);
                card.EpicSummary = epic?.Summary ?? "";
                var issues = await _hub.Jira.GetEpicIssuesAsync(p.EpicKey);
                if (issues.Count > 0)
                {
                    var done = issues.Count(i => i.Bucket == IssueBucket.Done);
                    card.Progress = done / (double)issues.Count;
                    card.ProgressText = $"{done}/{issues.Count} erledigt";
                }
            }
        }
        catch { }
    }

    [RelayCommand] private void New() => Editor.Open(null);
    [RelayCommand] private void Open(ProjectCard c) => _nav.Go(Routes.Project, c.Project.Id);
    [RelayCommand] private void OpenSession(ProjectCard c) => _nav.Go(Routes.Session, c.Project);
}
