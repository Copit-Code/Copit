namespace Copit.Core.Models;

public enum ChatRole { User, Assistant, Tool, System, Error }

/// <summary>Spalten des Agent-Boards.</summary>
public enum AgentStatus { Inbox, InProgress, WaitingSubagent, NeedInput, Review, Done, Failed }

public static class AgentStatusInfo
{
    public static readonly (AgentStatus Status, string Title, string Color)[] Columns =
    [
        (AgentStatus.Inbox, "Inbox", "#8B7CF6"),
        (AgentStatus.InProgress, "In Progress", "#4FB3D9"),
        (AgentStatus.WaitingSubagent, "Waiting for Subagent", "#D9A441"),
        (AgentStatus.NeedInput, "Need Input", "#E8743B"),
        (AgentStatus.Review, "Review", "#D96BA0"),
        (AgentStatus.Done, "Done", "#7BC47F"),
        (AgentStatus.Failed, "Failed", "#E0604F"),
    ];

    public static string Title(AgentStatus s) => Columns.First(c => c.Status == s).Title;
    public static string Color(AgentStatus s) => Columns.First(c => c.Status == s).Color;
}

/// <summary>Ein Lauf eines Agents (CLI-Aufruf) mit Zeiten, Kosten und Tokens.</summary>
public class AgentRun
{
    public DateTime Start { get; set; } = DateTime.Now;
    public DateTime? End { get; set; }
    public long AiMs { get; set; }
    public long ToolMs { get; set; }
    public double CostUsd { get; set; }
    public int Turns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public bool? Ok { get; set; }
    public long TotalTokens => InputTokens + OutputTokens + CacheRead + CacheWrite;
}

/// <summary>Eine Session = ein Agent auf dem Board.</summary>
public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "Neue Session";
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public AgentMode Mode { get; set; } = AgentMode.Edit;
    public string Model { get; set; } = "";
    /// <summary>Session-ID der CLI (für --resume).</summary>
    public string? CliSessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];
    public long ContextTokens { get; set; }
    public long ContextWindow { get; set; } = 200_000;
    public double TotalCostUsd { get; set; }
    public List<string> AllowedTools { get; set; } = [];
    /// <summary>Prompt der beim letzten Lauf noch nicht beendet war (Absturz / Neustart).</summary>
    public string? PendingPrompt { get; set; }
    public bool Archived { get; set; }

    // ---------------- Agent / Board ----------------
    public AgentStatus Status { get; set; } = AgentStatus.Inbox;
    public string? Ticket { get; set; }
    public string? Branch { get; set; }
    /// <summary>Arbeitsordner (Repo oder eigener Worktree).</summary>
    public string? Cwd { get; set; }
    public bool Worktree { get; set; }
    /// <summary>Commit beim Start – Basis für +/- Zeilen.</summary>
    public string? BaseSha { get; set; }
    /// <summary>Modell-Zeit (Generierung) – Agent-Zeit minus Tool-Ausführung.</summary>
    public long AiMs { get; set; }
    /// <summary>Wanduhr-Laufzeit aller Läufe.</summary>
    public long AgentMs { get; set; }
    public long ToolMs { get; set; }
    public long Tokens { get; set; }
    public long OutputTokens { get; set; }
    public int Turns { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int FilesChanged { get; set; }
    public string? PendingQuestion { get; set; }
    public List<PermissionDenial> PendingPermissions { get; set; } = [];
    /// <summary>Für den nächsten Lauf einmalig erlaubte Tools.</summary>
    public List<string> AllowedOnce { get; set; } = [];
    public string? Error { get; set; }
    public string? Summary { get; set; }
    public List<AgentRun> Runs { get; set; } = [];
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    public string? ToolName { get; set; }
    public string? ToolUseId { get; set; }
    public string? ToolInput { get; set; }
    public string? ToolResult { get; set; }
    public bool IsError { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    /// <summary>Bei Sub-Agent-Aktivität: Tool-Use-ID des Task-Aufrufs.</summary>
    public string? ParentToolUseId { get; set; }
    public long? DurationMs { get; set; }
}

// ---------------- Automationen (regelbasiert) ----------------

public class RuleCondition
{
    public string Field { get; set; } = "ticket";
    /// <summary>equals | not_equals | contains | matches</summary>
    public string Op { get; set; } = "contains";
    public string Value { get; set; } = "";
}

public class RuleAction
{
    public string Type { get; set; } = "notify";
    public Dictionary<string, string> Params { get; set; } = [];
}

/// <summary>WENN Trigger [UND Bedingungen] DANN Aktionen.</summary>
public class Automation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Trigger { get; set; } = "";
    public Dictionary<string, string> TriggerParams { get; set; } = [];
    public List<RuleCondition> Conditions { get; set; } = [];
    public List<RuleAction> Actions { get; set; } = [];
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";
    public int RunCount { get; set; }
}

public class AutomationRun
{
    public string AutomationId { get; set; } = "";
    public string AutomationName { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public bool Success { get; set; } = true;
    public string Output { get; set; } = "";
}
