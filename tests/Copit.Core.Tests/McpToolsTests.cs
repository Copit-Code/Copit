using System.Text.Json.Nodes;
using Copit.Core.Ai;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class McpToolsTests
{
    [Fact]
    public void DiscoversServersFromAllSourcesWithProjectPrecedence()
    {
        var root = Directory.CreateTempSubdirectory("copit-mcp").FullName;
        var home = Path.Combine(root, "home"); var appData = Path.Combine(root, "appdata"); var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(home); Directory.CreateDirectory(Path.Combine(appData, "Claude")); Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(home, ".claude.json"), $$"""
            {
              "mcpServers": { "jira": { "command": "npx", "args": ["-y", "mcp-jira"] }, "sentry": { "type": "http", "url": "https://mcp.sentry.dev/mcp" } },
              "projects": { {{System.Text.Json.JsonSerializer.Serialize(repo)}}: { "mcpServers": { "db": { "command": "uvx", "args": ["mcp-sqlite"] } } } }
            }
            """);
        File.WriteAllText(Path.Combine(repo, ".mcp.json"), """{ "mcpServers": { "jira": { "command": "projekt-jira" } } }""");
        File.WriteAllText(Path.Combine(appData, "Claude", "claude_desktop_config.json"), """{ "mcpServers": { "files": { "command": "npx", "args": ["fs"] } } }""");
        var own = new McpData(); own.Servers["gitlab"] = McpCatalog.ParseServer("npx -y @x/gitlab-mcp --token-env GL");

        var list = McpCatalog.Discover(repo, own, home, appData);
        Assert.Equal(["jira", "db", "sentry", "gitlab", "files"], list.Select(s => s.Name));
        Assert.Equal("projekt-jira", list[0].Summary);                    // Projekt vor Benutzer
        Assert.Equal("Claude Code (Projekt)", list[1].Source);
        Assert.Equal(("http", "https://mcp.sentry.dev/mcp"), (list[2].Type, list[2].Summary));
        Assert.Equal("npx -y @x/gitlab-mcp --token-env GL", list[3].Summary);
        Assert.Equal("mcp__gitlab", list[3].ToolPattern);
        Assert.Equal(["jira", "sentry", "files"], McpCatalog.Discover(null, null, home, appData).Select(s => s.Name));
    }

    [Fact]
    public void WritesConfigWithOnlySelectedServers()
    {
        var a = new McpServerInfo("a", "x", new JsonObject { ["command"] = "a" });
        var b = new McpServerInfo("b", "x", new JsonObject { ["url"] = "https://b" });
        var file = McpCatalog.WriteConfig([b]);
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Null(json["mcpServers"]!["a"]);
        Assert.Equal("https://b", json["mcpServers"]!["b"]!["url"]!.GetValue<string>());
        File.Delete(file);
        Assert.Equal("http", McpCatalog.ParseServer("https://x/mcp")["type"]!.GetValue<string>());
        Assert.Equal("cmd", McpCatalog.ParseServer("""{ "command": "cmd" }""")["command"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeArgsUseStrictMcpConfig()
    {
        var args = ClaudeCliRunner.BuildArgs(new AgentRequest { Prompt = "x", McpConfigPath = "/tmp/m.json", StrictMcpConfig = true, AllowedTools = ["mcp__jira"] }, null);
        Assert.Equal("/tmp/m.json", args[args.IndexOf("--mcp-config") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("mcp__jira", args[args.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--mcp-config", ClaudeCliRunner.BuildArgs(new AgentRequest { Prompt = "x" }, null));
    }

    [Fact]
    public void SystemPromptListsOnlySelectedServers()
    {
        var p = McpAnalysisService.SystemPrompt("Sei genau.", [new McpServerInfo("jira", "x", new JsonObject { ["command"] = "npx" })], false);
        Assert.StartsWith("Sei genau.", p);
        Assert.Contains("- jira (stdio: npx)", p);
        Assert.Contains("Lies keine lokalen Dateien", p);
    }

    [Fact]
    public void ManifestRejectsUnknownToolKind()
    {
        var dir = Directory.CreateTempSubdirectory("copit-ext").FullName;
        File.WriteAllText(Path.Combine(dir, ExtensionService.ManifestFile), """{ "id": "x-tool", "tools": [ { "id": "t", "title": "T", "kind": "zauberei" } ] }""");
        var e = Assert.Throws<InvalidOperationException>(() => ExtensionService.ReadManifest(dir));
        Assert.Contains("zauberei", e.Message);
    }
}
