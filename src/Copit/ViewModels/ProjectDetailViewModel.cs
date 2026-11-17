using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;

namespace Copit.ViewModels;

public class IssueColumn
{
    public string Title { get; init; } = "";
    public string ColorKey { get; init; } = "Muted";
    public Color Color => (Color)Application.Current!.Resources[ColorKey];
    public ObservableCollection<JiraIssue> Issues { get; } = [];
}

public class NoteGroup : List<ProjectNote>
{
    public string Title { get; init; } = "";
}

/// <summary>Pflichtfeld der Jira-Anlege-Maske im Ticket-Dialog.</summary>
public partial class TicketFieldVM : ObservableObject
{
    public JiraCreateField Field { get; init; } = null!;
    public string Name => Field.Name + (Field.Required ? " *" : "");
    public List<string> Options => [.. Field.Options.Select(o => o.Label)];
    public bool HasOptions => Field.Options.Count > 0;
    public bool IsText => !HasOptions;
    [ObservableProperty] public partial string? Selected { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    public string Value => (HasOptions ? Selected : Text)?.Trim() ?? "";
}

public partial class ProjectDetailViewModel : ObservableObject, IActivatable
{
    private readonly DataService _data;
    private readonly TimeBookingService _time;
    private readonly ConnectionHub _hub;
    private readonly NavigationService _nav;
    private readonly SessionStore _store;
    private readonly SessionManager _sessions;
    private readonly AssistService _assist;
    private readonly ToastService _toast;
    private readonly GitService _git;
    private readonly SettingsService _settings;
    private List<JiraIssue> _all = [];
    private string? _myName;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public ObservableCollection<IssueColumn> Columns { get; } =
    [
        new() { Title = "Offen", ColorKey = "Muted" },
        new() { Title = "In Arbeit", ColorKey = "Cyan" },
        new() { Title = "Review / Test", ColorKey = "Amber" },
        new() { Title = "Erledigt", ColorKey = "Green" },
    ];
    public ObservableCollection<NoteGroup> NoteGroups { get; } = [];
    public ObservableCollection<SessionHost> SessionList { get; } = [];
    public ProjectEditor Editor { get; }
    /// <summary>Ticket-Typen des Projekts aus Jira („Name · ID“); Fallback ohne Jira.</summary>
    public ObservableCollection<string> IssueTypes { get; } = ["Task", "Story", "Bug"];
    [ObservableProperty] public partial string IssueTypesInfo { get; set; } = "";
    private List<JiraIssueType> _issueTypes = [];
    /// <summary>Pflichtfelder der Anlege-Maske, die Copit nicht selbst füllt (z. B. Availability, Licensing).</summary>
    public ObservableCollection<TicketFieldVM> TicketFields { get; } = [];
    public bool HasTicketFields => TicketFields.Count > 0;
    [ObservableProperty] public partial string CheckText { get; set; } = "";
    [ObservableProperty] public partial bool Checking { get; set; }

    [ObservableProperty] public partial CopitProject? Project { get; set; }
    [ObservableProperty] public partial JiraIssue? Epic { get; set; }
    [ObservableProperty] public partial bool Loading { get; set; }
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool OnlyMine { get; set; }
    [ObservableProperty] public partial string Tab { get; set; } = "tickets";
    [ObservableProperty] public partial string StatsTickets { get; set; } = "";
    [ObservableProperty] public partial string StatsHours { get; set; } = "";
    [ObservableProperty] public partial double DoneFraction { get; set; }
    [ObservableProperty] public partial double HoursFraction { get; set; }
    [ObservableProperty] public partial string Branch { get; set; } = "";
    [ObservableProperty] public partial string? PipelineStatus { get; set; }
    [ObservableProperty] public partial int ChangeCount { get; set; }
    // Notizen
    [ObservableProperty] public partial string NewNote { get; set; } = "";
    [ObservableProperty] public partial DateTime NewNoteDate { get; set; } = DateTime.Today;
    [ObservableProperty] public partial string NoteSearch { get; set; } = "";
    // Ticket anlegen
    [ObservableProperty] public partial bool ShowTicket { get; set; }
    [ObservableProperty] public partial string TicketIdea { get; set; } = "";
    [ObservableProperty] public partial string TicketSummary { get; set; } = "";
    [ObservableProperty] public partial string TicketDescription { get; set; } = "";
    [ObservableProperty] public partial string TicketType { get; set; } = "Task";
    [ObservableProperty] public partial string TicketEstimate { get; set; } = "";
    [ObservableProperty] public partial string TicketReason { get; set; } = "";
    [ObservableProperty] public partial bool Drafting { get; set; }
    [ObservableProperty] public partial bool Creating { get; set; }
    [ObservableProperty] public partial string DraftStatus { get; set; } = "";
    [ObservableProperty] public partial AiProvider TicketProvider { get; set; }
    // Account des neuen Tickets (nur wenn ein Account-Feld konfiguriert ist)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TicketAccountText), nameof(TicketAccountMissing))] public partial string TicketAccount { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TicketAccountText))] public partial string TicketAccountSource { get; set; } = "";
    public bool ShowTicketAccount => !string.IsNullOrWhiteSpace(_settings.Settings.Jira.AccountFieldId);
    public bool TicketAccountMissing => ShowTicketAccount && string.IsNullOrWhiteSpace(TicketAccount);
    public string TicketAccountText => string.IsNullOrWhiteSpace(TicketAccount) ? "Kein Account gefunden – bitte wählen"
        : $"{TicketAccount}{(TicketAccountSource.Length > 0 ? " · " + TicketAccountSource : "")}";

    public bool IsTickets => Tab == "tickets";
    public bool IsNotes => Tab == "notes";
    public bool IsSessions => Tab == "sessions";
    public bool HasDraft => !string.IsNullOrWhiteSpace(TicketSummary);
    public bool TicketIsClaude => TicketProvider == AiProvider.Claude;
    public bool TicketIsCopilot => TicketProvider == AiProvider.Copilot;
    public bool HasPipeline => PipelineStatus != null;

    public ProjectDetailViewModel(DataService data, ConnectionHub hub, NavigationService nav, SessionStore store, SessionManager sessions,
        AssistService assist, ToastService toast, GitService git, SettingsService settings, TimeBookingService time)
    {
        _time = time;
        _data = data; _hub = hub; _nav = nav; _store = store; _sessions = sessions; _assist = assist; _toast = toast; _git = git; _settings = settings;
        Editor = new ProjectEditor(git, hub);
        Editor.Saved += async p =>
        {
            var e = _data.Project(p.Id);
            if (e == null) return;
            e.Name = p.Name; e.EpicKey = p.EpicKey; e.SourceFolder = p.SourceFolder; e.GitLabProject = p.GitLabProject; e.Color = p.Color; e.Instructions = p.Instructions; e.IssueType = p.IssueType; ProjectFolders.Move(data.Projects.Value, e, p.Group);
            await _data.Projects.SaveAsync();
            _data.RaiseProjectsChanged();
            Project = null; Project = e;
            await LoadAsync();
        };
        TicketProvider = settings.Settings.Ai.DefaultProvider;
    }

    partial void OnTabChanged(string value) { OnPropertyChanged(nameof(IsTickets)); OnPropertyChanged(nameof(IsNotes)); OnPropertyChanged(nameof(IsSessions)); }
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyMineChanged(bool value) => ApplyFilter();
    partial void OnNoteSearchChanged(string value) => LoadNotes();
    partial void OnTicketSummaryChanged(string value) => OnPropertyChanged(nameof(HasDraft));
    partial void OnTicketProviderChanged(AiProvider value) { OnPropertyChanged(nameof(TicketIsClaude)); OnPropertyChanged(nameof(TicketIsCopilot)); }
    partial void OnPipelineStatusChanged(string? value) => OnPropertyChanged(nameof(HasPipeline));

    public void OnActivated(object? parameter)
    {
        var p = parameter switch
        {
            string id => _data.Project(id),
            CopitProject cp => cp,
            _ => Project
        };
        if (p == null) { _nav.Go(Routes.Projects); return; }
        var changed = Project?.Id != p.Id;
        Project = p;
        p.LastOpened = DateTime.Now;
        _ = _data.Projects.SaveAsync();
        LoadNotes();
        LoadSessions();
        if (changed || _all.Count == 0) { Tab = "tickets"; _ = LoadAsync(); }
        else _ = LoadGitAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Project == null) return;
        Loading = true; Error = "";
        _ = LoadGitAsync();
        try
        {
            var jira = _hub.RequireJira;
            _myName ??= (await jira.GetMyselfAsync()).DisplayName;
            Epic = await jira.GetIssueAsync(Project.EpicKey);
            _all = await jira.GetEpicIssuesAsync(Project.EpicKey);
            ApplyFilter();
            var done = _all.Count(i => i.Bucket == IssueBucket.Done);
            DoneFraction = _all.Count > 0 ? done / (double)_all.Count : 0;
            StatsTickets = $"{done} von {_all.Count} Tickets erledigt";
            var est = _all.Sum(i => i.OriginalEstimateSeconds) / 3600.0;
            var spent = _all.Sum(i => i.TimeSpentSeconds) / 3600.0;
            StatsHours = est > 0 ? $"{spent:0.#}h gebucht von {est:0.#}h geschätzt" : $"{spent:0.#}h gebucht";
            HoursFraction = est > 0 ? Math.Min(1, spent / est) : 0;
        }
        catch (Exception e) { Error = e.Message; }
        finally { Loading = false; }
    }

    private async Task LoadGitAsync()
    {
        var p = Project;
        if (p == null || !GitService.IsRepo(p.SourceFolder)) { Branch = ""; PipelineStatus = null; return; }
        try
        {
            var st = await _git.GetStatusAsync(p.SourceFolder);
            Branch = st.Branch; ChangeCount = st.Changes.Count;
            if (_hub.GitLab != null && !string.IsNullOrEmpty(p.GitLabProject))
                PipelineStatus = (await _hub.GitLab.GetPipelinesAsync(p.GitLabProject, st.Branch, 1)).FirstOrDefault()?.Status;
        }
        catch { }
    }

    private void ApplyFilter()
    {
        foreach (var c in Columns) c.Issues.Clear();
        var f = Filter?.Trim() ?? "";
        foreach (var i in _all.OrderByDescending(i => i.Updated))
        {
            if (f.Length > 0 && !i.Key.Contains(f, StringComparison.OrdinalIgnoreCase) && !i.Summary.Contains(f, StringComparison.OrdinalIgnoreCase) && !i.Assignee.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            if (OnlyMine && i.Assignee != _myName) continue;
            Columns[(int)i.Bucket].Issues.Add(i);
        }
    }

    [RelayCommand] private void SetTab(string t) => Tab = t;
    [RelayCommand] private void ToggleMine() => OnlyMine = !OnlyMine;
    [RelayCommand] private Task OpenIssueAsync(JiraIssue i) => Dialogs.OpenUrl(i.Url);
    [RelayCommand] private Task OpenEpicAsync() => Dialogs.OpenUrl(Project == null ? null : _hub.Jira?.IssueUrl(Project.EpicKey));
    [RelayCommand] private void OpenFolder() { if (Project != null) Dialogs.OpenFolder(Project.SourceFolder); }
    [RelayCommand] private void OpenEditor() { if (Project != null) Dialogs.OpenInEditor(Project.SourceFolder); }
    [RelayCommand] private void OpenTerminal() { if (Project != null) Dialogs.OpenTerminal(Project.SourceFolder); }
    [RelayCommand]
    private Task OpenGitLabAsync() => Dialogs.OpenUrl(Project == null || _hub.GitLab == null || string.IsNullOrEmpty(Project.GitLabProject) ? null : $"{_hub.GitLab.BaseUrl}/{Project.GitLabProject}");
    [RelayCommand] private void Edit() { Editor.KnownGroups = ProjectFolders.AllPaths(_data.Projects.Value); Editor.Open(Project); }

    [RelayCommand]
    private async Task DeleteProjectAsync()
    {
        if (Project == null) return;
        if (!await Dialogs.Confirm("Projekt entfernen?", $"\"{Project.Name}\" wird aus Copit entfernt. Quellcode und Jira bleiben unverändert.", "Entfernen")) return;
        _data.Projects.Value.Projects.Remove(Project);
        await _data.Projects.SaveAsync();
        _data.RaiseProjectsChanged();
        Project = null;
        _nav.Go(Routes.Projects);
    }

    [RelayCommand] private void OpenSession() { if (Project != null) _nav.Go(Routes.Session, Project); }
    [RelayCommand] private void NewSession() { if (Project != null) _nav.Go(Routes.Session, new SessionRequest(Project, NewSession: true)); }
    [RelayCommand] private void OpenExistingSession(SessionHost h) => _nav.Go(Routes.Session, h);

    [RelayCommand]
    private async Task WorkOnIssueAsync(JiraIssue i)
    {
        if (Project == null) return;
        var choice = await Dialogs.Choose($"{i.Key}: {i.Summary}", "Session starten", "Branch anlegen + Session starten", "Code-Änderungen ansehen", "In Jira öffnen");
        if (choice == null || choice == "Abbrechen") return;
        if (choice.StartsWith("In Jira")) { await Dialogs.OpenUrl(i.Url); return; }
        if (choice.StartsWith("Code-Änderungen")) { _nav.Go(Routes.Changes, new Copit.Views.ChangesRequest(Project.Id, i.Key)); return; }
        var desc = string.IsNullOrWhiteSpace(i.Description) ? "" : $"\n\nBeschreibung aus Jira:\n{(i.Description.Length > 4000 ? i.Description[..4000] : i.Description)}";
        var prompt = $"Wir arbeiten jetzt an Ticket {i.Key}: \"{i.Summary}\".{desc}\n\nVerschaffe dir einen Überblick über den relevanten Code und schlage einen Umsetzungsplan vor. Stelle Rückfragen, falls Anforderungen unklar sind.";
        var autoPrompt = $"Wir arbeiten jetzt an Ticket {i.Key}: \"{i.Summary}\".{desc}\n\nVerschaffe dir einen Überblick über den relevanten Code und setze das Ticket selbstständig um. " +
                         "Triff bei unklaren Anforderungen sinnvolle Annahmen und nenne sie am Ende.";
        _nav.Go(Routes.Session, new SessionRequest(Project, prompt, $"{i.Key}: {i.Summary}", true, choice.StartsWith("Branch") ? i : null, AutoPrompt: autoPrompt));
    }

    // ---------------- Notizen ----------------
    private void LoadNotes()
    {
        NoteGroups.Clear();
        if (Project == null) return;
        var q = NoteSearch?.Trim() ?? "";
        var notes = _data.NotesFor(Project.Id).Where(n => q.Length == 0 || n.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        var pinned = notes.Where(n => n.Pinned).ToList();
        if (pinned.Count > 0) NoteGroups.Add(new NoteGroup { Title = "Angeheftet" }.With(pinned));
        foreach (var g in notes.Where(n => !n.Pinned).GroupBy(n => n.Date).OrderByDescending(g => g.Key))
        {
            var title = g.Key == DateOnly.FromDateTime(DateTime.Today) ? "Heute" :
                        g.Key == DateOnly.FromDateTime(DateTime.Today.AddDays(-1)) ? "Gestern" : g.Key.ToString("dddd, dd.MM.yyyy", De);
            NoteGroups.Add(new NoteGroup { Title = title }.With(g));
        }
    }

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (Project == null || string.IsNullOrWhiteSpace(NewNote)) return;
        _data.Notes.Value.Notes.Add(new ProjectNote { ProjectId = Project.Id, Text = NewNote.Trim(), Date = DateOnly.FromDateTime(NewNoteDate) });
        await _data.Notes.SaveAsync();
        NewNote = "";
        LoadNotes();
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(ProjectNote n)
    {
        _data.Notes.Value.Notes.Remove(n);
        await _data.Notes.SaveAsync();
        LoadNotes();
    }

    [RelayCommand]
    private async Task PinNoteAsync(ProjectNote n)
    {
        n.Pinned = !n.Pinned;
        await _data.Notes.SaveAsync();
        LoadNotes();
    }

    private void LoadSessions()
    {
        SessionList.Clear();
        if (Project == null) return;
        foreach (var s in _store.ForProject(Project.Id)) SessionList.Add(_sessions.Host(s));
    }

    // ---------------- Ticket anlegen ----------------
    [RelayCommand]
    private void NewTicket()
    {
        TicketIdea = TicketSummary = TicketDescription = TicketReason = TicketEstimate = DraftStatus = "";
        TicketType = !string.IsNullOrWhiteSpace(Project?.IssueType) ? Project!.IssueType : _settings.Settings.Jira.DefaultIssueType;
        _ = LoadIssueTypesAsync();
        TicketAccount = ""; TicketAccountSource = "";
        CheckText = ""; TicketFields.Clear(); OnPropertyChanged(nameof(HasTicketFields));
        OnPropertyChanged(nameof(ShowTicketAccount));
        ShowTicket = true;
        if (ShowTicketAccount) _ = ResolveTicketAccountAsync();
    }

    [RelayCommand] private void CloseTicket() => ShowTicket = false;

    /// <summary>Lädt die im Projekt erlaubten Ticket-Typen mit ID und wählt den passenden Standard.</summary>
    private async Task LoadIssueTypesAsync()
    {
        if (Project == null || _hub.Jira == null || string.IsNullOrWhiteSpace(Project.JiraProjectKey)) return;
        IssueTypesInfo = "Lade Ticket-Typen…";
        try
        {
            await EnsureJiraFieldsAsync();
            var types = (await _hub.Jira.GetIssueTypesAsync(Project.JiraProjectKey)).Where(t => !t.Subtask).ToList();
            _issueTypes = types;
            if (types.Count == 0) { IssueTypesInfo = ""; return; }
            IssueTypes.Clear();
            foreach (var t in types) IssueTypes.Add(t.Label);
            // gewählten Typ (Name oder ID) auf „Name · ID“ abbilden
            var cur = JiraClient.IssueTypeRef(TicketType);
            var match = types.FirstOrDefault(t => cur["id"]?.ToString() == t.Id || string.Equals(cur["name"]?.ToString(), t.Name, StringComparison.OrdinalIgnoreCase));
            TicketType = match?.Label ?? types[0].Label;
            IssueTypesInfo = match == null && cur["name"] != null ? $"„{cur["name"]}“ gibt es hier nicht – {types[0].Name} gewählt" : "";
            await LoadTicketFieldsAsync();
        }
        catch (Exception e) { IssueTypesInfo = "Ticket-Typen nicht ladbar: " + e.Message; }
    }

    /// <summary>Account vorschlagen: Account des Epics → gemerkt fürs Epic → gemerkt fürs Jira-Projekt.</summary>
    private async Task ResolveTicketAccountAsync()
    {
        if (Project == null) return;
        TicketAccountSource = "suche…";
        try
        {
            string? acc; string src;
            if (!string.IsNullOrWhiteSpace(Project.EpicKey)) (acc, src) = await _time.EpicAccountAsync(Project.EpicKey);
            else
            {
                acc = _data.Accounts.Value.ProjectAccounts.GetValueOrDefault(Project.JiraProjectKey);
                src = acc != null ? "gemerkt fürs Projekt" : "";
            }
            if (string.IsNullOrWhiteSpace(TicketAccount)) { TicketAccount = acc ?? ""; TicketAccountSource = acc != null ? src : ""; }
            else if (TicketAccountSource == "suche…") TicketAccountSource = "";
        }
        catch { TicketAccountSource = ""; }
    }

    [RelayCommand]
    private async Task PickTicketAccountAsync()
    {
        var options = new List<string>(_data.Accounts.Value.KnownAccounts.Take(12));
        if (_settings.Settings.Jira.UseTempo && _hub.Jira != null)
        {
            try { foreach (var a in (await _hub.Jira.GetTempoAccountsAsync()).Take(80)) if (!options.Contains(a.Key)) options.Add(a.Key); }
            catch { }
        }
        options.Add("Eingeben…");
        var pick = await Dialogs.Choose("Account für das neue Ticket", [.. options]);
        if (pick == null) return;
        if (pick == "Eingeben…")
        {
            pick = await Dialogs.Prompt("Account für das neue Ticket", "Account-Key", TicketAccount, "z. B. ACC-1");
            if (string.IsNullOrWhiteSpace(pick)) return;
        }
        TicketAccount = pick.Trim();
        TicketAccountSource = "gewählt";
    }
    [RelayCommand] private void SetTicketProvider(string p) => TicketProvider = Enum.Parse<AiProvider>(p);
    [RelayCommand] private void SetTicketType(string t) { TicketType = t; _ = LoadTicketFieldsAsync(); }

    /// <summary>Epic-Link- und Account-Feld aus Jira erkennen und in den Einstellungen speichern, wenn dort noch nichts steht.</summary>
    private async Task EnsureJiraFieldsAsync()
    {
        var j = _settings.Settings.Jira;
        if (_hub.Jira == null || (!string.IsNullOrWhiteSpace(j.EpicLinkFieldId) && !string.IsNullOrWhiteSpace(j.AccountFieldId))) return;
        try
        {
            var ids = await _hub.Jira.DetectFieldsAsync();
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(j.EpicLinkFieldId) && ids.EpicLink != null) { j.EpicLinkFieldId = ids.EpicLink; found.Add($"Epic Link = {ids.EpicLink}"); }
            if (string.IsNullOrWhiteSpace(j.AccountFieldId) && ids.Account != null) { j.AccountFieldId = ids.Account; found.Add($"Account = {ids.Account}"); }
            if (found.Count == 0) return;
            await _settings.SaveAsync();
            _toast.Success("Jira-Felder erkannt und gespeichert", string.Join(" · ", found));
            OnPropertyChanged(nameof(ShowTicketAccount));
            if (ShowTicketAccount && string.IsNullOrWhiteSpace(TicketAccount)) _ = ResolveTicketAccountAsync();
        }
        catch { /* ohne Rechte auf die Feldliste: Einstellungen bleiben wie sie sind */ }
    }

    private string? SelectedTypeId()
    {
        var r = JiraClient.IssueTypeRef(TicketType);
        return r["id"]?.ToString() ?? _issueTypes.FirstOrDefault(t => string.Equals(t.Name, r["name"]?.ToString(), StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Pflichtfelder der Maske für den gewählten Typ laden und mit den gemerkten Werten des Projekts vorbelegen.</summary>
    private async Task LoadTicketFieldsAsync()
    {
        if (Project == null || _hub.Jira == null || SelectedTypeId() is not { } typeId) return;
        try
        {
            var fields = await _hub.Jira.GetCreateFieldsAsync(Project.JiraProjectKey, typeId);
            var ids = await _hub.Jira.DetectFieldsAsync().ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            SetTicketFields(JiraClient.MissingRequired(fields, ids, _settings.Settings.Jira.AccountFieldId));
        }
        catch { /* Maske nicht lesbar → Felder bleiben leer, „Prüfen“ zeigt den Grund */ }
    }

    private void SetTicketFields(IEnumerable<JiraCreateField> fields)
    {
        TicketFields.Clear();
        foreach (var f in fields)
        {
            var vm = new TicketFieldVM { Field = f };
            if (Project?.FieldDefaults.TryGetValue(f.Id, out var d) == true)
            {
                if (vm.HasOptions) vm.Selected = vm.Options.FirstOrDefault(o => o.Equals(d, StringComparison.OrdinalIgnoreCase));
                else vm.Text = d;
            }
            TicketFields.Add(vm);
        }
        OnPropertyChanged(nameof(HasTicketFields));
    }

    /// <summary>Fragt Jira: Berechtigung, Typ, erkannte Felder, offene Pflichtfelder.</summary>
    [RelayCommand]
    private async Task CheckTicketAsync()
    {
        if (Project == null || _hub.Jira == null) { CheckText = "Jira ist nicht verbunden."; return; }
        Checking = true; CheckText = "Prüfe in Jira…";
        try
        {
            await EnsureJiraFieldsAsync();
            var c = await _hub.Jira.CheckCreateAsync(Project.JiraProjectKey, TicketType);
            var lines = new List<string>
            {
                c.CanCreate switch { true => $"✓ Du darfst in {Project.JiraProjectKey} Vorgänge erstellen", false => $"✗ Keine Berechtigung „Vorgänge erstellen“ in {Project.JiraProjectKey}", _ => "? Berechtigung nicht abfragbar" },
                c.Type != null ? $"✓ Typ {c.Type.Label}" : "✗ Typ nicht gefunden",
                $"{(c.Fields.EpicLink != null ? "✓" : "–")} Epic-Link-Feld: {c.Fields.EpicLink ?? "keins (Epic wird als parent gesetzt)"}",
                $"{(c.Fields.Account != null ? "✓" : "–")} Account-Feld: {c.Fields.Account ?? "keins"}",
                c.Required.Count == 0 ? "✓ Keine weiteren Pflichtfelder" : $"! Pflichtfelder: {string.Join(", ", c.Required.Select(f => f.Name))}",
            };
            lines.AddRange(c.Problems.Select(p => "✗ " + p));
            CheckText = string.Join("\n", lines.Distinct());
            if (c.Type != null) SetTicketFields(c.Required);
        }
        catch (Exception e) { CheckText = "Prüfung fehlgeschlagen: " + JiraClient.JiraErrorText(e.Message); }
        finally { Checking = false; }
    }

    [RelayCommand]
    private async Task DraftTicketAsync()
    {
        if (Project == null || string.IsNullOrWhiteSpace(TicketIdea)) return;
        Drafting = true;
        DraftStatus = "KI analysiert Idee und Code…";
        try
        {
            var d = await _assist.DraftTicketAsync(Project, TicketIdea, _all, TicketProvider,
                s => MainThread.BeginInvokeOnMainThread(() => DraftStatus = s));
            TicketSummary = d.Summary;
            TicketDescription = d.Description + (d.Subtasks.Count > 0 ? "\n\nh3. Teilschritte\n" + string.Join("\n", d.Subtasks.Select(s => "* " + s)) : "");
            if (!string.IsNullOrWhiteSpace(d.IssueType) && !d.IssueType.Contains('|'))
            {
                // KI-Vorschlag (Name) auf einen echten Typ aus Jira abbilden, sonst Auswahl behalten
                var hit = IssueTypes.FirstOrDefault(l => l.Split(" · ")[0].Equals(d.IssueType.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit != null) TicketType = hit;
                else if (!IssueTypes.Any(l => l.Contains(" · "))) TicketType = d.IssueType;
            }
            TicketEstimate = d.EstimateHours.ToString("0.##", De);
            TicketReason = d.EstimateReason;
        }
        catch (Exception e) { _toast.Error(e, "KI-Entwurf fehlgeschlagen"); }
        finally { Drafting = false; DraftStatus = ""; }
    }

    [RelayCommand]
    private async Task CreateTicketAsync()
    {
        if (Project == null || string.IsNullOrWhiteSpace(TicketSummary)) return;
        Creating = true;
        try
        {
            var est = double.TryParse(TicketEstimate.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ? h : 0;
            string? account = null;
            if (ShowTicketAccount)
            {
                if (string.IsNullOrWhiteSpace(TicketAccount)) await PickTicketAccountAsync();
                if (string.IsNullOrWhiteSpace(TicketAccount)) { _toast.Show("Account fehlt", "Ohne Account wird kein Ticket angelegt – bitte einen Account wählen.", NoticeLevel.Warning); return; }
                account = TicketAccount.Trim();
            }
            var extra = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>();
            foreach (var f in TicketFields)
            {
                var node = JiraClient.FieldValue(f.Field, f.Value);
                if (node == null && f.Field.Required) { _toast.Show("Pflichtfeld fehlt", $"Bitte „{f.Field.Name}“ ausfüllen – Jira verlangt es für diesen Typ.", NoticeLevel.Warning); return; }
                if (node != null) extra[f.Field.Id] = node;
            }
            var key = await _hub.RequireJira.CreateIssueAsync(Project.JiraProjectKey, TicketSummary.Trim(), TicketDescription, TicketType, est, Project.EpicKey, default, account, extra);
            foreach (var f in TicketFields.Where(f => f.Value.Length > 0)) Project.FieldDefaults[f.Field.Id] = f.Value;   // pro Projekt merken
            Project.IssueType = TicketType;   // pro Projekt merken
            await _data.Projects.SaveAsync();
            if (account != null)
            {
                // fürs neue Ticket und – wenn selbst gewählt – fürs Epic merken
                await _time.RememberAccountAsync(key, account, false);
                if (TicketAccountSource == "gewählt" && !string.IsNullOrWhiteSpace(Project.EpicKey)) await _time.RememberAccountAsync(Project.EpicKey, account, false);
            }
            ShowTicket = false;
            var notes = _hub.RequireJira.LastCreateNotes;
            if (notes.Count > 0) _toast.Show($"Ticket {key} angelegt", string.Join(" · ", notes), NoticeLevel.Warning);
            else _toast.Success($"Ticket {key} angelegt", TicketSummary);
            await LoadAsync();
        }
        catch (Exception e) { _toast.Error(e, "Ticket konnte nicht angelegt werden"); }
        finally { Creating = false; }
    }
}

internal static class NoteGroupExt
{
    public static NoteGroup With(this NoteGroup g, IEnumerable<ProjectNote> notes) { g.AddRange(notes); return g; }
}
