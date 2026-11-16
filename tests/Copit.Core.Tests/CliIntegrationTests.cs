using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Core.Storage;

namespace Copit.Core.Tests;

/// <summary>
/// Echte CLI-Aufrufe. Laufen nur mit Umgebungsvariable COPIT_IT=1 (kostet einen kleinen KI-Aufruf).
/// </summary>
public class CliIntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("COPIT_IT") == "1";

    [Fact]
    public async Task ClaudeStreamsAndResumes()
    {
        if (!Enabled) return;
        AppPaths.Root = Path.Combine(Path.GetTempPath(), "copit-it");
        var runner = new ClaudeCliRunner(new SettingsService(new PlainProtector()));
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest { Prompt = "Merke dir das Wort BANANE. Antworte nur mit OK.", Mode = AgentMode.Ask, WorkingDirectory = Path.GetTempPath() }, default))
            events.Add(e);
        var init = Assert.Single(events.OfType<AgentInit>());
        var result = Assert.IsType<AgentResult>(events[^1]);
        Assert.False(result.IsError, result.Text);
        Assert.Contains(events, e => e is AgentTextDelta);
        Assert.Contains(events, e => e is AgentUsage { ContextTokens: > 0 });

        var second = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest { Prompt = "Welches Wort solltest du dir merken? Nur das Wort.", ResumeSessionId = init.SessionId, WorkingDirectory = Path.GetTempPath() }, default))
            second.Add(e);
        var r2 = Assert.IsType<AgentResult>(second[^1]);
        Assert.Contains("BANANE", r2.Text, StringComparison.OrdinalIgnoreCase);
    }
}
