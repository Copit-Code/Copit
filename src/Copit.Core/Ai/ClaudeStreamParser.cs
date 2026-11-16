using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Core.Storage;

namespace Copit.Core.Ai;

/// <summary>Parst die Ausgabe von `claude -p --output-format stream-json --verbose [--include-partial-messages]`.</summary>
public static class ClaudeStreamParser
{
    public static IEnumerable<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') yield break;
        JsonNode? n;
        try { n = JsonNode.Parse(line); }
        catch (JsonException) { yield break; }
        if (n == null) yield break;

        switch (n.Str("type"))
        {
            case "system" when n.Str("subtype") == "init":
                var tools = n["tools"]?.AsArray().Select(t => t?.ToString() ?? "").ToList() ?? [];
                yield return new AgentInit(n.Str("session_id"), n.Str("model"), tools, n.Str("permissionMode"));
                break;

            case "stream_event":
                var ev = n["event"];
                if (ev.Str("type") == "content_block_delta")
                {
                    var delta = ev?["delta"];
                    switch (delta.Str("type"))
                    {
                        case "text_delta": yield return new AgentTextDelta(delta.Str("text")); break;
                        case "thinking_delta": yield return new AgentThinking(delta.Str("thinking")); break;
                    }
                }
                else if (ev.Str("type") == "message_start")
                {
                    var u = ev?["message"]?["usage"];
                    if (u != null) yield return Usage(u);
                }
                break;

            case "assistant":
                var msg = n["message"];
                foreach (var c in msg?["content"]?.AsArray() ?? [])
                {
                    switch (c.Str("type"))
                    {
                        case "text": yield return new AgentText(c.Str("text")); break;
                        case "tool_use":
                            yield return new AgentToolUse(c.Str("id"), c.Str("name"),
                                c?["input"]?.ToJsonString(Json.Compact) ?? "{}");
                            break;
                    }
                }
                if (msg?["usage"] is JsonNode usage) yield return Usage(usage);
                break;

            case "user":
                foreach (var c in n["message"]?["content"]?.AsArray() ?? [])
                {
                    if (c.Str("type") != "tool_result") continue;
                    yield return new AgentToolResult(c.Str("tool_use_id"), ContentToText(c?["content"]),
                        c?["is_error"]?.GetValue<bool>() ?? false);
                }
                break;

            case "result":
                var denials = n["permission_denials"]?.AsArray()
                    .Select(d => new PermissionDenial(d.Str("tool_name"), d.Str("tool_use_id"), d?["tool_input"]?.ToJsonString(Json.Compact) ?? "{}"))
                    .ToList() ?? [];
                double cost = 0;
                if (n["total_cost_usd"] is JsonValue cv) cv.TryGetValue(out cost);
                yield return new AgentResult(
                    n["is_error"]?.GetValue<bool>() ?? false,
                    n.Str("result"), cost, n.Long("duration_ms"), (int)n.Long("num_turns"),
                    denials, n.Str("session_id"), n.Str("subtype"));
                break;
        }
    }

    private static AgentUsage Usage(JsonNode u) =>
        new(u.Long("input_tokens") + u.Long("cache_creation_input_tokens") + u.Long("cache_read_input_tokens"), u.Long("output_tokens"));

    private static string ContentToText(JsonNode? content)
    {
        if (content == null) return "";
        if (content is JsonValue) return content.ToString();
        if (content is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var part in arr)
            {
                if (part.Str("type") == "text") sb.AppendLine(part.Str("text"));
                else if (part.Str("type") == "image") sb.AppendLine("[Bild]");
            }
            return sb.ToString().TrimEnd();
        }
        return content.ToJsonString();
    }
}

