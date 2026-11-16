using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

// ---------------------------------------------------------------- MCP-Server finden

/// <summary>Ein MCP-Server aus einer der bekannten Konfigurationen.</summary>
public record McpServerInfo(string Name, string Source, JsonObject Config)
{
    public string Type => Config["type"]?.GetValue<string>() ?? (Config["url"] != null ? "http" : "stdio");
    /// <summary>Kurzbeschreibung: Befehl oder URL (ohne Umgebungsvariablen/Secrets).</summary>
    public string Summary => Config["url"]?.GetValue<string>()
        ?? string.Join(' ', new[] { Config["command"]?.GetValue<string>() ?? "" }.Concat(Config["args"]?.AsArray().Select(a => a?.ToString() ?? "") ?? []).Where(x => x.Length > 0));
    /// <summary>Muster für --allowedTools: erlaubt alle Tools dieses Servers.</summary>
    public string ToolPattern => "mcp__" + Name;
}

public class McpData
{
    /// <summary>In Copit angelegte Server (Name → Claude-MCP-Konfiguration).</summary>
    public Dictionary<string, JsonObject> Servers { get; set; } = [];
    /// <summary>Zuletzt gewählte Server je Tool ("ext/tool" → Namen).</summary>
    public Dictionary<string, List<string>> Selections { get; set; } = [];
    public List<McpRun> History { get; set; } = [];
}

public class McpRun
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Tool { get; set; } = "";
    public string? ProjectId { get; set; }
    public List<string> Servers { get; set; } = [];
    public string Prompt { get; set; } = "";
    public string Result { get; set; } = "";
    public bool IsError { get; set; }
    public double CostUsd { get; set; }
    public long Tokens { get; set; }
    public long DurationMs { get; set; }
    public List<string> ToolCalls { get; set; } = [];
}

/// <summary>
/// Sammelt MCP-Server aus Claude Code (~/.claude.json: Benutzer- und Projekt-Scope), .mcp.json im Repo,
/// Claude Desktop (claude_desktop_config.json) und eigenen Copit-Einträgen.
/// </summary>
public static class McpCatalog
{
    public static List<McpServerInfo> Discover(string? projectDir, McpData? own = null, string? home = null, string? appData = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<McpServerInfo>();
        void Add(JsonNode? servers, string source)
        {
            if (servers is not JsonObject o) return;
            foreach (var (name, cfg) in o)
                if (cfg is JsonObject c && !list.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new McpServerInfo(name, source, (JsonObject)c.DeepClone()));
        }
        // Reihenfolge = Vorrang bei gleichen Namen: Projekt vor Benutzer
        if (!string.IsNullOrEmpty(projectDir))
        {
            Add(Read(Path.Combine(projectDir, ".mcp.json"))?["mcpServers"], "Projekt (.mcp.json)");
            var claude = Read(Path.Combine(home, ".claude.json"));
            if (claude?["projects"] is JsonObject projects)
                foreach (var (path, p) in projects)
                    if (SamePath(path, projectDir)) Add(p?["mcpServers"], "Claude Code (Projekt)");
        }
        Add(Read(Path.Combine(home, ".claude.json"))?["mcpServers"], "Claude Code");
        if (own != null) foreach (var (n, c) in own.Servers) Add(new JsonObject { [n] = c.DeepClone() }, "Copit");
        Add(Read(Path.Combine(appData, "Claude", "claude_desktop_config.json"))?["mcpServers"], "Claude Desktop");
        return list;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static JsonNode? Read(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) : null; }
        catch { return null; }
    }

    /// <summary>Schreibt eine MCP-Konfiguration nur mit den gewählten Servern (für --mcp-config).</summary>
    public static string WriteConfig(IEnumerable<McpServerInfo> servers, string? file = null)
    {
        file ??= Path.Combine(Path.GetTempPath(), $"copit-mcp-{Guid.NewGuid():N}.json");
        var o = new JsonObject();
        foreach (var s in servers) o[s.Name] = s.Config.DeepClone();
        File.WriteAllText(file, new JsonObject { ["mcpServers"] = o }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    /// <summary>Server aus einer Eingabe anlegen: JSON-Objekt oder Befehlszeile ("npx -y @x/server") bzw. URL.</summary>
    public static JsonObject ParseServer(string input)
    {
        input = input.Trim();
        if (input.StartsWith('{')) return JsonNode.Parse(input) as JsonObject ?? throw new InvalidOperationException("Kein JSON-Objekt.");
        if (input.StartsWith("http://") || input.StartsWith("https://")) return new JsonObject { ["type"] = "http", ["url"] = input };
        var parts = ProcessRunner.SplitArgs(input);
        if (parts.Count == 0) throw new InvalidOperationException("Befehl fehlt.");
        return new JsonObject { ["command"] = parts[0], ["args"] = new JsonArray([.. parts.Skip(1).Select(p => (JsonNode)p)]) };
    }
}

// ---------------------------------------------------------------- Auswertung ausführen

/// <summary>Startet eine Claude-Auswertung, die ausschließlich die gewählten MCP-Server verwenden darf.</summary>
public class McpAnalysisService(AgentRunnerFactory runners, SettingsService settings)
{
    public JsonStore<McpData> Store { get; } = new(AppPaths.File("mcp.json"));

    public List<McpServerInfo> Servers(string? projectDir) => McpCatalog.Discover(projectDir, Store.Value);

    public static string SystemPrompt(string? toolPrompt, IReadOnlyList<McpServerInfo> servers, bool readFiles)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(toolPrompt)) sb.AppendLine(toolPrompt.Trim()).AppendLine();
        sb.AppendLine("Für diese Auswertung stehen genau diese MCP-Server zur Verfügung (andere Datenquellen gibt es nicht):");
        foreach (var s in servers) sb.AppendLine($"- {s.Name} ({s.Type}{(s.Summary.Length > 0 ? ": " + s.Summary : "")})");
        sb.AppendLine(readFiles ? "Zusätzlich darfst du Dateien im Arbeitsordner lesen (nicht ändern)." : "Lies keine lokalen Dateien und führe keine Befehle aus.");
        sb.AppendLine("Nenne am Ende kurz, welche Server/Tools du für welche Zahlen verwendet hast. Wenn Daten fehlen oder ein Server nicht antwortet, sag das klar statt zu schätzen.");
        return sb.ToString();
    }

    /// <summary>Führt die Auswertung aus; Events (Text, Tool-Aufrufe) werden live gemeldet. Das Ergebnis landet im Verlauf.</summary>
    public async Task<McpRun> RunAsync(string toolKey, string? toolPrompt, IReadOnlyList<McpServerInfo> servers, string prompt, string? workingDir,
        bool readFiles, string? projectId, Action<AgentEvent>? onEvent = null, CancellationToken ct = default)
    {
        if (servers.Count == 0) throw new InvalidOperationException("Bitte mindestens einen MCP-Server auswählen.");
        var cfg = McpCatalog.WriteConfig(servers);
        var run = new McpRun { Tool = toolKey, ProjectId = projectId, Servers = [.. servers.Select(s => s.Name)], Prompt = prompt };
        var text = new StringBuilder();
        try
        {
            var allowed = servers.Select(s => s.ToolPattern).ToList();
            if (readFiles) allowed.AddRange(["Read", "Grep", "Glob"]);
            var model = settings.Settings.Ai.ClaudeModel;
            var req = new AgentRequest
            {
                Prompt = prompt,
                WorkingDirectory = string.IsNullOrEmpty(workingDir) ? Path.GetTempPath() : workingDir,
                Mode = AgentMode.Ask,
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
                AllowedTools = allowed,
                AppendSystemPrompt = SystemPrompt(toolPrompt, servers, readFiles),
                McpConfigPath = cfg,
                StrictMcpConfig = true,
            };
            await foreach (var e in runners.Claude.RunAsync(req, ct))
            {
                onEvent?.Invoke(e);
                switch (e)
                {
                    case AgentTextDelta d: text.Append(d.Text); break;
                    case AgentToolUse t: run.ToolCalls.Add(t.Name); break;
                    case AgentResult r:
                        run.IsError = r.IsError; run.CostUsd = r.CostUsd; run.DurationMs = r.DurationMs; run.Tokens = r.Usage?.Total ?? 0;
                        run.Result = string.IsNullOrWhiteSpace(r.Text) ? text.ToString() : r.Text;
                        break;
                }
            }
        }
        finally { try { File.Delete(cfg); } catch { } }
        if (string.IsNullOrEmpty(run.Result)) run.Result = text.ToString();
        var data = Store.Value;
        data.Selections[toolKey] = run.Servers;
        data.History.Insert(0, run);
        if (data.History.Count > 100) data.History.RemoveRange(100, data.History.Count - 100);
        await Store.SaveAsync();
        return run;
    }
}
