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

/// <summary>Zeile der gruppierten Projektliste: Ordner-Kopf oder Projekt.</summary>
public partial class ProjectListRow : ObservableObject
{
    public bool IsFolder { get; init; }
    public bool IsProject => !IsFolder;
    public string FolderPath { get; init; } = "";
    public string FolderName { get; init; } = "";
    public int Count { get; init; }
    public bool Collapsed { get; init; }
    public string Chevron => Collapsed ? "chevron-right" : "chevron-down";
    public int Depth { get; init; }
    public Thickness Indent => new(10 + Depth * 22, IsFolder ? 10 : 12, 10, IsFolder ? 10 : 12);
    public ProjectCard? Card { get; init; }
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
    /// <summary>Ordner in der Projektliste (z. B. "Kunde A/Backend").</summary>
    [ObservableProperty] public partial string Group { get; set; } = "";
    /// <summary>Ticket-Typ für neue Tickets (Name oder ID, leer = Standard).</summary>
    [ObservableProperty] public partial string IssueType { get; set; } = "";
    public List<string> KnownGroups { get; set; } = [];
    /// <summary>Jira-Component/Label/JQL zur Abgrenzung bei geteiltem Epic.</summary>
    [ObservableProperty] public partial string TicketComponent { get; set; } = "";
    [ObservableProperty] public partial string TicketLabel { get; set; } = "";
    [ObservableProperty] public partial string TicketJql { get; set; } = "";
    public string TicketFilterHint =>
        "Nur Tickets mit dieser Component bzw. diesem Label werden im Projekt angezeigt; neue Tickets bekommen sie automatisch. " +
        "Ohne Filter ordnet Copit Tickets über Commits und Branches im Repo zu – oder per Klick aufs Ticket → „Projekt zuordnen…“.";
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
        Color = p?.Color ?? Palette[Random.Shared.Next(Palette.Length)]; Instructions = p?.Instructions ?? ""; Group = p?.Group ?? Group; IssueType = p?.IssueType ?? "";
        TicketComponent = p?.TicketComponent ?? ""; TicketLabel = p?.TicketLabel ?? ""; TicketJql = p?.TicketJql ?? "";
        EpicInfo = ""; EpicOk = false; Error = "";
        IsOpen = true;
        if (!string.IsNullOrEmpty(Epic)) _ = CheckEpicAsync();
    }

    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private void PickColor(string c) => Color = c;

    [RelayCommand]
    private async Task PickGroupAsync()
    {
        var options = new List<string> { "Ohne Ordner", "Neuer Ordner…" };
        options.AddRange(KnownGroups);
        var pick = await Dialogs.Choose("Ordner wählen", [.. options]);
        if (pick == null) return;
        if (pick == "Ohne Ordner") Group = "";
        else if (pick == "Neuer Ordner…") { var n = await Dialogs.Prompt("Neuer Ordner", "Name, Unterordner mit / (z. B. Kunde A/Backend)", Group); if (n != null) Group = ProjectFolders.Normalize(n); }
        else Group = pick;
    }

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
            GitLabProject = GitLab.Trim(), Color = Color, Instructions = Instructions, Group = ProjectFolders.Normalize(Group), IssueType = IssueType.Trim(),
            TicketComponent = TicketComponent.Trim(), TicketLabel = TicketLabel.Trim(), TicketJql = TicketJql.Trim(),
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

    public ObservableCollection<ProjectListRow> Rows { get; } = [];
    private readonly Dictionary<string, ProjectCard> _cards = [];
    public ProjectEditor Editor { get; }
    public bool IsEmpty => _data.Projects.Value.Projects.Count == 0 && _data.Projects.Value.Folders.Count == 0;

    private readonly TicketScopeService _scope;

    public ProjectsViewModel(DataService data, GitService git, ConnectionHub hub, NavigationService nav, SessionStore sessions, ToastService toast, TicketScopeService scope)
    {
        _scope = scope;
        _data = data; _git = git; _hub = hub; _nav = nav; _sessions = sessions; _toast = toast;
        Editor = new ProjectEditor(git, hub);
        Editor.Saved += SaveProjectAsync;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task SaveProjectAsync(CopitProject p)
    {
        var list = _data.Projects.Value.Projects;
        var existing = list.FirstOrDefault(x => x.Id == p.Id);
        if (existing != null)
        {
            existing.Name = p.Name; existing.EpicKey = p.EpicKey; existing.SourceFolder = p.SourceFolder;
            existing.GitLabProject = p.GitLabProject; existing.Color = p.Color; existing.Instructions = p.Instructions; existing.IssueType = p.IssueType;
            existing.TicketComponent = p.TicketComponent; existing.TicketLabel = p.TicketLabel; existing.TicketJql = p.TicketJql;
            ProjectFolders.Move(_data.Projects.Value, existing, p.Group);
        }
        else { list.Add(p); ProjectFolders.Add(_data.Projects.Value, p.Group); }
        await _data.Projects.SaveAsync();
        _data.RaiseProjectsChanged();
        _toast.Success(existing == null ? "Projekt angelegt" : "Projekt gespeichert", p.Name);
        Load();
        if (existing == null) _nav.Go(Routes.Project, p.Id);
    }

    public void OnActivated(object? parameter)
    {
        Load(refresh: true);
        Editor.KnownGroups = ProjectFolders.AllPaths(_data.Projects.Value);
        if (parameter is "new") { Editor.Group = ""; Editor.Open(null); }
        else if (parameter is string s && s.StartsWith("new:")) { Editor.Group = s[4..]; Editor.Open(null); }
    }

    /// <summary>Baut die gruppierte Liste neu auf. Karten (Git/Jira-Daten) werden wiederverwendet, nur bei refresh neu geladen.</summary>
    private void Load(bool refresh = false)
    {
        Rows.Clear();
        if (refresh) _cards.Clear();
        foreach (var r in ProjectFolders.Flatten(_data.Projects.Value))
        {
            if (r.IsFolder) { Rows.Add(new ProjectListRow { IsFolder = true, FolderPath = r.Path, FolderName = r.Name, Count = r.Count, Collapsed = r.Collapsed, Depth = r.Depth }); continue; }
            var p = r.Project!;
            if (!_cards.TryGetValue(p.Id, out var card))
            {
                _cards[p.Id] = card = new ProjectCard { Project = p, Sessions = _sessions.ForProject(p.Id).Count() };
                _ = EnrichAsync(card);
            }
            Rows.Add(new ProjectListRow { Card = card, Depth = r.Depth, FolderPath = r.Path });
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task SaveFoldersAsync()
    {
        await _data.Projects.SaveAsync();
        _data.RaiseProjectsChanged();
        Editor.KnownGroups = ProjectFolders.AllPaths(_data.Projects.Value);
        Load();
    }

    [RelayCommand]
    private async Task ToggleFolderAsync(ProjectListRow row)
    {
        ProjectFolders.Toggle(_data.Projects.Value, row.FolderPath);
        await SaveFoldersAsync();
    }

    [RelayCommand]
    private async Task NewFolderAsync(ProjectListRow? parent)
    {
        var prefix = parent?.IsFolder == true ? parent.FolderPath + "/" : "";
        var name = await Dialogs.Prompt("Neuer Ordner", "Name, Unterordner mit / (z. B. Kunde A/Backend)", prefix);
        if (string.IsNullOrWhiteSpace(name)) return;
        ProjectFolders.Add(_data.Projects.Value, name);
        await SaveFoldersAsync();
    }

    [RelayCommand]
    private async Task FolderMenuAsync(ProjectListRow row)
    {
        var pick = await Dialogs.Choose(row.FolderPath, "Projekt in diesem Ordner anlegen", "Unterordner anlegen", "Umbenennen / verschieben", "Ordner löschen");
        switch (pick)
        {
            case "Projekt in diesem Ordner anlegen": Editor.Group = row.FolderPath; Editor.Open(null); break;
            case "Unterordner anlegen": await NewFolderAsync(row); break;
            case "Umbenennen / verschieben":
                var n = await Dialogs.Prompt("Ordner umbenennen", "Neuer Pfad, Unterordner mit /", row.FolderPath);
                if (string.IsNullOrWhiteSpace(n)) return;
                try { ProjectFolders.Rename(_data.Projects.Value, row.FolderPath, n); await SaveFoldersAsync(); }
                catch (Exception e) { _toast.Show("Nicht möglich", e.Message, NoticeLevel.Warning); }
                break;
            case "Ordner löschen":
                if (!await Dialogs.Confirm("Ordner löschen", $"„{row.FolderPath}“ löschen? Die {row.Count} Projekte darin rücken eine Ebene nach oben.", "Löschen")) return;
                ProjectFolders.Delete(_data.Projects.Value, row.FolderPath);
                await SaveFoldersAsync();
                break;
        }
    }

    [RelayCommand]
    private async Task MoveProjectAsync(ProjectListRow row)
    {
        if (row.Card == null) return;
        var options = new List<string> { "Ohne Ordner", "Neuer Ordner…" };
        options.AddRange(ProjectFolders.AllPaths(_data.Projects.Value));
        var pick = await Dialogs.Choose($"{row.Card.Project.Name} verschieben nach", [.. options]);
        if (pick == null) return;
        if (pick == "Neuer Ordner…") { pick = await Dialogs.Prompt("Neuer Ordner", "Name, Unterordner mit /", row.FolderPath); if (string.IsNullOrWhiteSpace(pick)) return; }
        await MoveToAsync(row.Card.Project, pick == "Ohne Ordner" ? "" : pick);
    }

    public CopitProject? FindProject(string id) => _data.Projects.Value.Projects.FirstOrDefault(p => p.Id == id);

    /// <summary>Drag &amp; Drop: Projekt auf einen Ordner (oder auf "Ohne Ordner") ziehen.</summary>
    public async Task MoveToAsync(CopitProject p, string folder)
    {
        ProjectFolders.Move(_data.Projects.Value, p, folder);
        await SaveFoldersAsync();
        _toast.Success("Verschoben", $"{p.Name} → {(p.Group.Length > 0 ? p.Group : "ohne Ordner")}");
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
                var scoped = await _scope.ScopeAsync(p, issues);   // geteiltes Epic: nur eigene + nicht zugeordnete zählen
                issues = issues.Where(i => scoped.Visible(i.Key)).ToList();
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
    [RelayCommand] private void Open(ProjectListRow r) { if (r.Card != null) _nav.Go(Routes.Project, r.Card.Project.Id); }
    [RelayCommand] private void OpenSession(ProjectListRow r) { if (r.Card != null) _nav.Go(Routes.Session, r.Card.Project); }
}
