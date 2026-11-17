namespace Copit.Core.Models;

public enum AiProvider { Claude, Copilot }

public enum JiraAuthMode { BearerToken, Basic }

/// <summary>Permission modes for an agent session. Mapped to the CLI flags of Claude / Copilot.</summary>
public enum AgentMode
{
    /// <summary>Nur Fragen beantworten, keine Änderungen.</summary>
    Ask,
    /// <summary>Plant zuerst und zeigt den Plan (Claude: plan mode).</summary>
    Plan,
    /// <summary>Darf Dateien ändern (Claude: acceptEdits).</summary>
    Edit,
    /// <summary>Darf alles inkl. Shell-Befehle (Claude: bypassPermissions / Copilot: --allow-all-tools).</summary>
    Auto
}

public class AppSettings
{
    public JiraSettings Jira { get; set; } = new();
    public GitLabSettings GitLab { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public TimeSettings Time { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public bool OnboardingDone { get; set; }
}

public class UpdateSettings
{
    /// <summary>GitHub-Repository (owner/name) mit den Copit-Releases.</summary>
    public string Repo { get; set; } = "Copit-Code/Copit";
    /// <summary>Beim Start und alle paar Stunden nach neuen Releases suchen.</summary>
    public bool Auto { get; set; } = true;
    public string? SkippedVersion { get; set; }
}

public class JiraSettings
{
    public string BaseUrl { get; set; } = "";
    public JiraAuthMode AuthMode { get; set; } = JiraAuthMode.BearerToken;
    /// <summary>Nur für Basic-Auth.</summary>
    public string Username { get; set; } = "";
    /// <summary>Custom field das den Account trägt (z.B. Tempo "customfield_10100").</summary>
    public string AccountFieldId { get; set; } = "";
    /// <summary>Epic-Link Feld (Jira Server/DC), z.B. "customfield_10101". Leer = parent verwenden.</summary>
    public string EpicLinkFieldId { get; set; } = "";
    /// <summary>Worklogs über Tempo Timesheets buchen (inkl. Account-Attribut).</summary>
    public bool UseTempo { get; set; }
    /// <summary>Key des Tempo Work-Attributes für den Account.</summary>
    public string TempoAccountAttributeKey { get; set; } = "_Account_";
    public string DefaultIssueType { get; set; } = "Task";
    public bool IgnoreCertificateErrors { get; set; }
}

public class GitLabSettings
{
    public string BaseUrl { get; set; } = "";
    public bool IgnoreCertificateErrors { get; set; }
}

public class AiSettings
{
    public AiProvider DefaultProvider { get; set; } = AiProvider.Claude;
    public string ClaudePath { get; set; } = "";
    public string CopilotPath { get; set; } = "";
    public string ClaudeModel { get; set; } = "";
    public string CopilotModel { get; set; } = "";
    public string ClaudeExtraArgs { get; set; } = "";
    public string CopilotExtraArgs { get; set; } = "";
    /// <summary>Preis pro Copilot Premium Request in USD (für Kosten in Activity/Budgets). Standard: Preis für zusätzliche Requests.</summary>
    public double CopilotPremiumRequestUsd { get; set; } = 0.04;
    /// <summary>Wird jeder Session als zusätzliche System-Anweisung mitgegeben.</summary>
    public string GlobalInstructions { get; set; } = "Antworte auf Deutsch. Halte Antworten kompakt.";
    /// <summary>Neue Agents mit Branch arbeiten in einem eigenen git worktree.</summary>
    public bool UseWorktrees { get; set; } = true;
    /// <summary>Copit-Regeln und Claude-settings.json als --allowedTools/--disallowedTools mitgeben.</summary>
    public bool ApplyPermissionRules { get; set; } = true;
    /// <summary>Tageslimit für Copit-Agents mit Claude (0 = aus).</summary>
    public DailyLimit ClaudeLimit { get; set; } = new() { Unit = "usd" };
    /// <summary>Tageslimit für Copit-Agents mit Copilot (0 = aus). Copilot meldet keine Kosten → Stunden oder Läufe.</summary>
    public DailyLimit CopilotLimit { get; set; } = new() { Unit = "hours" };
    /// <summary>Token sparen: Skript-Bibliothek für wiederkehrende Aufgaben.</summary>
    public ScriptLibrarySettings Scripts { get; set; } = new();
}

public class ScriptLibrarySettings
{
    public bool Enabled { get; set; }
    /// <summary>"global" (%LOCALAPPDATA%\Copit\scripts, für alle Projekte) oder "project" (.copit/scripts im Repo).</summary>
    public string Scope { get; set; } = "global";
}

public class DailyLimit
{
    public double Amount { get; set; }
    /// <summary>usd | hours | runs</summary>
    public string Unit { get; set; } = "usd";
    public string? WarnedDay { get; set; }

    public string Format(double v) => Unit switch { "usd" => $"${v:0.00}", "hours" => $"{v:0.#} h", _ => $"{v:0} Läufe" };
}

public class TimeSettings
{
    public double MinHoursPerDay { get; set; } = 8;
    public List<DayOfWeek> WorkDays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public string DayStart { get; set; } = "08:00";
    /// <summary>Rundung der Vorschläge in Minuten.</summary>
    public int RoundingMinutes { get; set; } = 15;
    /// <summary>Buchen nach Personen-Aufwand: was eine Person ohne KI gebraucht hätte, mal Faktor (etwas weniger).</summary>
    public bool HumanEffort { get; set; } = true;
    public double EffortFactor { get; set; } = 0.85;
    public List<RuleSet> RuleSets { get; set; } =
    [
        new() { Name = "Buchungstexte", Text = "Buchungstexte sind kurz, fachlich und im Präteritum, z.B. 'Login-Fehler im Token-Refresh analysiert und behoben'. Keine Ticketnummern im Text.", Enabled = true },
        new() { Name = "Keine Mini-Buchungen", Text = "Buche nie weniger als 0,25h auf ein Ticket. Fasse Kleinkram auf dem passendsten Ticket zusammen.", Enabled = true }
    ];
}

public class RuleSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
