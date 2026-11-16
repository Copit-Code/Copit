using Copit.Core.Models;

namespace Copit.Core.Ai;

public abstract record AgentEvent;
public record AgentInit(string SessionId, string Model, IReadOnlyList<string> Tools, string PermissionMode) : AgentEvent;
public record AgentTextDelta(string Text) : AgentEvent;
public record AgentThinking(string Text) : AgentEvent;
public record AgentText(string Text) : AgentEvent;
public record AgentToolUse(string Id, string Name, string InputJson) : AgentEvent;
public record AgentToolResult(string ToolUseId, string Content, bool IsError) : AgentEvent;
public record AgentUsage(long ContextTokens, long OutputTokens) : AgentEvent;
public record AgentStatus(string Text) : AgentEvent;
public record AgentResult(bool IsError, string Text, double CostUsd, long DurationMs, int Turns,
    IReadOnlyList<PermissionDenial> Denials, string? SessionId, string Subtype) : AgentEvent;

public class AgentRequest
{
    public required string Prompt { get; init; }
    public string? WorkingDirectory { get; init; }
    public AgentMode Mode { get; init; } = AgentMode.Ask;
    public string? Model { get; init; }
    public string? ResumeSessionId { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public string? AppendSystemPrompt { get; init; }
    /// <summary>Verlauf für CLIs ohne eigenes Resume (Copilot).</summary>
    public IReadOnlyList<ChatMessage> History { get; init; } = [];
    public string? ExtraArgs { get; init; }
    public IReadOnlyList<string> AdditionalDirs { get; init; } = [];
}

public interface IAgentRunner
{
    AiProvider Provider { get; }
    /// <summary>Startet die CLI und liefert Events, bis der Lauf beendet ist (immer mit AgentResult am Ende).</summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct);
    Task<(bool Ok, string Info)> CheckAsync(CancellationToken ct = default);
}
