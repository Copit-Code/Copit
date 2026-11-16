using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

public class ProjectsData
{
    public List<CopitProject> Projects { get; set; } = [];
    public string? ActiveProjectId { get; set; }
    /// <summary>Projektordner (auch leere) mit Auf-/Zuklapp-Zustand.</summary>
    public List<ProjectFolder> Folders { get; set; } = [];
}
public class NotesData { public List<ProjectNote> Notes { get; set; } = []; }
public class DaysData { public Dictionary<string, DayEntry> Days { get; set; } = []; }
public class AutomationsData { public List<Automation> Automations { get; set; } = []; public List<AutomationRun> Runs { get; set; } = []; public bool Seeded { get; set; } }
public class LocalWorklogData { public List<JiraWorklog> Worklogs { get; set; } = []; }
public class BudgetsData { public List<Budget> Budgets { get; set; } = []; }
public class PromptsData { public List<PromptSnippet> Snippets { get; set; } = []; public bool Seeded { get; set; } }
public class PromptSnippet { public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string Title { get; set; } = ""; public string Text { get; set; } = ""; }

/// <summary>Zentrale lokale Persistenz (JSON unter %LOCALAPPDATA%\Copit).</summary>
public class DataService
{
    public JsonStore<ProjectsData> Projects { get; } = new(AppPaths.File("projects.json"));
    public JsonStore<NotesData> Notes { get; } = new(AppPaths.File("notes.json"));
    public JsonStore<DaysData> Days { get; } = new(AppPaths.File("days.json"));
    public JsonStore<AccountMemory> Accounts { get; } = new(AppPaths.File("accounts.json"));
    public JsonStore<AutomationsData> Automations { get; } = new(AppPaths.File("automations.json"));
    public JsonStore<PromptsData> Prompts { get; } = new(AppPaths.File("prompts.json"));
    public JsonStore<TimeMemory> TimeMemory { get; } = new(AppPaths.File("time-memory.json"));
    public JsonStore<BudgetsData> Budgets { get; } = new(AppPaths.File("budgets.json"));
    public JsonStore<BookingSources> Sources { get; } = new(AppPaths.File("time-sources.json"));
    public JsonStore<DocsData> Docs { get; } = new(AppPaths.File("docs.json"));
    public JsonStore<LocalWorklogData> LocalWorklogs { get; } = new(AppPaths.File("worklogs.json"));
    public JsonStore<ExtensionsData> Extensions { get; } = new(AppPaths.File("extensions.json"));

    public event Action? ProjectsChanged;
    public void RaiseProjectsChanged() => ProjectsChanged?.Invoke();

    public DayEntry Day(DateOnly d)
    {
        var key = d.ToString("yyyy-MM-dd");
        if (!Days.Value.Days.TryGetValue(key, out var e))
            Days.Value.Days[key] = e = new DayEntry { Date = d };
        return e;
    }

    public DayEntry? PeekDay(DateOnly d) => Days.Value.Days.GetValueOrDefault(d.ToString("yyyy-MM-dd"));

    public IEnumerable<ProjectNote> NotesFor(string projectId) =>
        Notes.Value.Notes.Where(n => n.ProjectId == projectId).OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Date).ThenByDescending(n => n.CreatedAt);

    public IEnumerable<ProjectNote> NotesOn(DateOnly d) => Notes.Value.Notes.Where(n => n.Date == d);

    public CopitProject? Project(string? id) => Projects.Value.Projects.FirstOrDefault(p => p.Id == id);

    public void EnsureSeeds()
    {
        if (!Prompts.Value.Seeded)
        {
            Prompts.Value.Snippets.AddRange(
            [
                new() { Title = "Tests schreiben", Text = "Schreibe Unit-Tests für die zuletzt geänderten Klassen. Nutze das bestehende Test-Framework des Projekts und führe die Tests aus." },
                new() { Title = "Refactoring", Text = "Analysiere die geänderten Dateien auf Code-Smells und schlage ein Refactoring vor. Setze es erst nach meiner Bestätigung um." },
                new() { Title = "Bug suchen", Text = "Ich habe folgenden Fehler: \n\nFinde die Ursache im Code, erkläre sie kurz und behebe sie." },
                new() { Title = "Pipeline fixen", Text = "Die GitLab-Pipeline ist fehlgeschlagen. Analysiere das Log und behebe die Ursache im Code." },
                new() { Title = "Doku erstellen", Text = "Erstelle/aktualisiere die README mit Setup, Architektur-Überblick und wichtigsten Befehlen." },
            ]);
            Prompts.Value.Seeded = true;
            Prompts.Save();
        }
    }
}

/// <summary>Liefert konfigurierte API-Clients und baut sie bei Einstellungsänderungen neu.</summary>
public class ConnectionHub
{
    private readonly SettingsService _settings;
    private JiraClient? _jira;
    private GitLabClient? _gitlab;

    public ConnectionHub(SettingsService settings)
    {
        _settings = settings;
        settings.Changed += () => { _jira = null; _gitlab = null; };
    }

    public JiraClient? Jira => _settings.JiraConfigured
        ? _jira ??= new JiraClient(_settings.Settings.Jira, _settings.GetSecret(SecretKeys.JiraToken))
        : null;

    public GitLabClient? GitLab => _settings.GitLabConfigured
        ? _gitlab ??= new GitLabClient(_settings.Settings.GitLab.BaseUrl, _settings.GetSecret(SecretKeys.GitLabToken), _settings.Settings.GitLab.IgnoreCertificateErrors)
        : null;

    public JiraClient RequireJira => Jira ?? throw new InvalidOperationException("Jira ist nicht konfiguriert (Einstellungen → Jira).");
    public GitLabClient RequireGitLab => GitLab ?? throw new InvalidOperationException("GitLab ist nicht konfiguriert (Einstellungen → GitLab).");
}
