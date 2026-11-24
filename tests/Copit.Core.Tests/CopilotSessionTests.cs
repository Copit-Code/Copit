using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

/// <summary>Copilot-CLI-Sessions außerhalb von Copit (~/.copilot/session-state/&lt;id&gt;/events.jsonl).</summary>
public class CopilotSessionTests
{
    private static string Ts(DateTime local) => local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static (string Root, string Repo) Setup(DateTime t0)
    {
        var root = Directory.CreateTempSubdirectory("copilot-home").FullName;
        var repo = Path.Combine(Path.GetTempPath(), "repos", "station-service");
        var dir = Path.Combine(root, "session-state", "8f1c2d3e-0000-4000-8000-000000000001");
        Directory.CreateDirectory(dir);
        string E(string type, DateTime at, string data, string extra = "") =>
            "{\"id\":\"" + Guid.NewGuid() + "\",\"timestamp\":\"" + Ts(at) + "\",\"parentId\":null," + extra + "\"type\":\"" + type + "\",\"data\":" + data + "}";
        var json = repo.Replace("\\", "\\\\");
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"),
        [
            E("session.start", t0, $$$"""{"sessionId":"8f1c2d3e-0000-4000-8000-000000000001","version":1,"producer":"copilot-agent","copilotVersion":"1.0.63","startTime":"{{{Ts(t0)}}}","selectedModel":"claude-sonnet-4.5","context":{"cwd":"{{{json}}}","gitRoot":"{{{json}}}","branch":"feature/GD-12"}}"""),
            E("user.message", t0.AddSeconds(5), """{"content":"Fix the   timeout in the station poller"}"""),
            E("assistant.turn_start", t0.AddSeconds(6), """{"turnId":"1"}"""),
            E("tool.execution_start", t0.AddSeconds(8), """{"toolCallId":"a","toolName":"bash","arguments":{}}"""),
            E("assistant.message", t0.AddMinutes(3), """{"messageId":"m1","model":"claude-sonnet-4.5","content":"done","outputTokens":300}"""),
            E("user.message", t0.AddMinutes(4), """{"content":"sub agent prompt"}""", "\"agentId\":\"sub-1\","),
            E("user.message", t0.AddMinutes(20), """{"content":"now add tests"}"""),
            E("assistant.message", t0.AddMinutes(25), """{"messageId":"m2","model":"claude-sonnet-4.5","content":"ok","outputTokens":200}"""),
            E("session.shutdown", t0.AddMinutes(26), """{"shutdownType":"routine","totalPremiumRequests":2,"totalNanoAiu":500000000,"totalApiDurationMs":1000,"sessionStartTime":1,"codeChanges":{},"currentModel":"claude-sonnet-4.5","modelMetrics":{"claude-sonnet-4.5":{"requests":{"count":4,"cost":2},"usage":{"inputTokens":50000,"outputTokens":500,"cacheReadTokens":40000,"cacheWriteTokens":2000}}}}"""),
            "not json",
        ]);
        return (root, repo);
    }

    [Fact]
    public void ReadsCopilotSessionsWithCwdBranchTitleTokensAndCost()
    {
        var t0 = DateTime.Today.AddHours(9);
        var (root, repo) = Setup(t0);
        var old = AgentRun.CopilotPrices;
        try
        {
            AgentRun.CopilotPrices = (0.04, 0.1);
            var svc = new ActivityService(Path.Combine(root, "no-claude"), [root]);
            var s = Assert.Single(svc.Build(DateTime.Today, DateTime.Today.AddDays(1), [], _ => null));
            Assert.Equal("copilot", s.Source);
            Assert.Equal("Copilot CLI", s.Entry);
            Assert.Equal(Path.GetFullPath(repo), Path.GetFullPath(s.ProjectPath));
            Assert.Equal("feature/GD-12", s.Branch);
            Assert.Equal("Fix the timeout in the station poller", s.Title);
            Assert.Equal(2, s.Prompts);                       // Sub-Agent-Prompt zählt nicht
            Assert.Equal(1, s.ToolCalls);
            Assert.Equal(1, s.Subagents);
            Assert.Equal(2, s.Segments.Count);                // Pause > 10 min trennt
            Assert.Equal((8000L, 500L, 40000L, 2000L), (s.InputTokens, s.OutputTokens, s.CacheRead, s.CacheWrite));
            Assert.Equal(2 * 0.04 + 0.5 * 0.1, s.CostUsd, 6);
        }
        finally { AgentRun.CopilotPrices = old; }
    }

    [Fact]
    public void SessionsStartedByCopitAreNotCountedTwice()
    {
        var t0 = DateTime.Today.AddHours(9);
        var (root, repo) = Setup(t0);
        var agent = new ChatSession { Id = "a1", ProjectId = "p1", Provider = AiProvider.Copilot, Title = "Copit-Agent" };
        agent.Runs.Add(new AgentRun { Start = t0, End = t0.AddMinutes(30) });
        var svc = new ActivityService(Path.Combine(root, "no-claude"), [root]);
        var list = svc.Build(DateTime.Today, DateTime.Today.AddDays(1), [agent], id => id == "p1" ? repo : null);
        Assert.DoesNotContain(list, s => s.Source == "copilot");
        Assert.Contains(list, s => s.AgentId == "a1");
    }

    [Fact]
    public void MissingCopilotFolderIsFine()
    {
        var svc = new ActivityService(Path.Combine(Path.GetTempPath(), "nope-claude"), [Path.Combine(Path.GetTempPath(), "nope-copilot-" + Guid.NewGuid())]);
        Assert.Empty(svc.Build(DateTime.Today, DateTime.Today.AddDays(1), [], _ => null));
    }
}
