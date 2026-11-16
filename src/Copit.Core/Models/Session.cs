namespace Copit.Core.Models;

public enum ChatRole { User, Assistant, Tool, System, Error }

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
}

public enum AutomationKind { Reminder, BookingCheck, AiPrompt, PipelineWatch, DailyStandup }

public class Automation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public AutomationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public string? ProjectId { get; set; }
    public string Prompt { get; set; } = "";
    /// <summary>Uhrzeit "HH:mm" für tägliche Automationen.</summary>
    public string TimeOfDay { get; set; } = "16:30";
    /// <summary>Intervall für Watcher (Minuten).</summary>
    public int IntervalMinutes { get; set; } = 5;
    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public AiProvider Provider { get; set; } = AiProvider.Claude;
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";
    public string LastState { get; set; } = "";
}

public class AutomationRun
{
    public string AutomationId { get; set; } = "";
    public string AutomationName { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public bool Success { get; set; } = true;
    public string Output { get; set; } = "";
}
