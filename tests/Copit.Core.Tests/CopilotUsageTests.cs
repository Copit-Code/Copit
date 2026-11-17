using Copit.Core.Ai;

namespace Copit.Core.Tests;

public class CopilotUsageTests
{
    private static (CopilotUsageParser P, List<string> Text) Run(string output)
    {
        var p = new CopilotUsageParser();
        var text = output.Replace("\r", "").Split('\n').Where(l => !p.Feed(l)).ToList();
        return (p, text);
    }

    [Fact]
    public void ParsesCurrentCliSummary()
    {
        // So rendert Copilot CLI 1.x die Zusammenfassung (stderr, Labels auf 11 Zeichen aufgefüllt)
        var (p, text) = Run("""
            Die Datei enthält 3 Klassen.

            Changes    +12 -3
            Requests   1.5 Premium (1m 8s)
            Tokens     ↑ 12.6k (10.2k cached, 1k written) • ↓ 297 (50 reasoning)
            """);
        Assert.Equal(new TokenUsage(1_400, 297, 10_200, 1_000), p.Usage);
        Assert.Equal(1.5, p.PremiumRequests, 3);
        Assert.Equal(12, p.LinesAdded);
        Assert.Equal(3, p.LinesRemoved);
        Assert.Equal("Die Datei enthält 3 Klassen.", string.Join("\n", text).Trim());
    }

    [Fact]
    public void ParsesCurrentSummaryWithoutCacheAndWithCredits()
    {
        var (p, _) = Run("""
            Changes    +0 -0
            AI Credits 0.12 (8s)
            Tokens     ↑ 850 • ↓ 1.2m
            """);
        Assert.Equal(new TokenUsage(850, 1_200_000, 0, 0), p.Usage);
        Assert.Equal(0.12, p.AiCredits, 3);
        Assert.Equal(0, p.PremiumRequests);
    }

    [Fact]
    public void ToleratesBrokenArrowEncoding()
    {
        var (p, _) = Run("Tokens     â†‘ 2.5k (1k cached) â€¢ â†“ 40");
        Assert.Equal(new TokenUsage(1_500, 40, 1_000, 0), p.Usage);
    }

    [Fact]
    public void ParsesClassicSummaryAndKeepsAnswer()
    {
        var (p, text) = Run("""
            Die Datei enthält 3 Klassen.

            Total usage est:       1 Premium request
            Total duration (API):  3.2s
            Total duration (wall): 5.1s
            Total code changes:    0 lines added, 0 lines removed
            Usage by model:
                gpt-5                12.6k input, 297 output, 1,200 cache read, 0 cache write (Est. 1 Premium request)
            """);
        Assert.Equal(new TokenUsage(12_600, 297, 1_200, 0), p.Usage);
        Assert.Equal(1, p.PremiumRequests);
        Assert.Equal("Die Datei enthält 3 Klassen.", string.Join("\n", text).Trim());
    }

    [Fact]
    public void ParsesNewerSummaryWithSeveralModels()
    {
        var (p, text) = Run("""
            Fertig.
            Total usage est:        0.33 Premium requests
            API time spent:         5s
            Total session time:     8s
            Total code changes:     +12 -3
            Breakdown by AI model:
             claude-sonnet-4.5       18.8k in, 240 out, 15.4k cached (Est. 0.33 Premium requests)
             gpt-5-mini              1.5k in, 50 out, 0 cached (Est. 0 Premium requests)
            """);
        // „in“ enthält die gecachten Tokens → 3.4k + 1.5k frische Eingabe
        Assert.Equal(new TokenUsage(4_900, 290, 15_400, 0), p.Usage);
        Assert.Equal(0.33, p.PremiumRequests, 3);
        Assert.Equal(["Fertig."], text);
    }

    [Fact]
    public void WithoutSummaryNothingIsSwallowed()
    {
        var (p, text) = Run("Total: 3 Dateien\nalles gut");
        Assert.Null(p.Usage);
        Assert.Equal(2, text.Count);
    }
}

public class QuestionAnswerTests
{
    [Fact]
    public void SingleAnswerIsSentAsIs()
        => Assert.Equal("Ja", Copit.Core.Ai.QuestionParser.ComposeAnswers([("Soll ich committen?", " Ja ")]));

    [Fact]
    public void SeveralAnswersAreBundledAndOpenOnesLeftToTheAgent()
    {
        var msg = Copit.Core.Ai.QuestionParser.ComposeAnswers([("Branch?", "feature/x"), ("Tests?", null), ("Sprache?", "Deutsch")]);
        Assert.Contains("1. Branch?\n   → feature/x", msg);
        Assert.Contains("2. Tests?\n   → (keine Antwort – entscheide selbst", msg);
        Assert.Contains("3. Sprache?\n   → Deutsch", msg);
    }
}

public class CardMovedTriggerTests
{
    private static Copit.Core.Models.Automation Rule(string from, string to) => new()
    {
        Trigger = "agent.moved", Enabled = true, TriggerParams = new() { ["from"] = from, ["to"] = to },
    };

    private static Copit.Core.Services.AutomationEvent Moved(string from, string to) =>
        new("agent.moved", new() { ["from"] = from, ["to"] = to }, null);

    [Theory]
    [InlineData("Review", "Done", true)]
    [InlineData("", "Done", true)]            // von beliebiger Spalte
    [InlineData("Review", "", true)]          // nach beliebiger Spalte
    [InlineData("InProgress", "Done", false)] // kam nicht aus Review
    [InlineData("Review", "Failed", false)]
    public void FromAndToAreChecked(string from, string to, bool hit)
        => Assert.Equal(hit, Copit.Core.Services.AutomationService.TriggerMatches(Rule(from, to), Moved("Review", "Done")));

    [Fact]
    public void TitlesAndEnumNamesAreBothAccepted()
    {
        Assert.True(Copit.Core.Services.AutomationService.SameColumn("In Progress", "InProgress"));
        Assert.True(Copit.Core.Services.AutomationService.SameColumn("inprogress", "In Progress"));
        Assert.False(Copit.Core.Services.AutomationService.SameColumn("Done", "Review"));
    }
}

public class PushBranchTests
{
    [Fact]
    public async Task PushesTheGivenBranchEvenIfAnotherIsCheckedOut()
    {
        var root = Path.Combine(Path.GetTempPath(), "copit-push-" + Guid.NewGuid().ToString("N")[..8]);
        var origin = Path.Combine(root, "origin.git"); var work = Path.Combine(root, "work");
        Directory.CreateDirectory(root);
        async Task Git(string dir, params string[] a)
        {
            var r = await Copit.Core.Services.ProcessRunner.RunAsync("git", ["-C", dir, .. a]);
            Assert.True(r.Ok, r.StdErr);
        }
        await Git(root, "init", "--bare", "-q", origin);
        await Git(root, "init", "-q", "-b", "main", work);
        await Git(work, "config", "user.email", "t@t"); await Git(work, "config", "user.name", "t");
        await Git(work, "commit", "--allow-empty", "-qm", "init");
        await Git(work, "remote", "add", "origin", origin);
        await Git(work, "branch", "feature/ST-1");   // Karte-Branch, nicht ausgecheckt

        var git = new Copit.Core.Services.GitService();
        await git.PushBranchAsync(work, "feature/ST-1");
        var r = await Copit.Core.Services.ProcessRunner.RunAsync("git", ["-C", origin, "branch", "--list", "feature/ST-1"]);
        Assert.Contains("feature/ST-1", r.StdOut);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.PushBranchAsync(work, "gibts/nicht"));
        try { Directory.Delete(root, true); } catch { }
    }
}

public class PushDefaultTests
{
    [Fact]
    public void PushDefaultsToTheCardBranch()
    {
        var push = Copit.Core.Services.AutomationService.Actions.Single(a => a.Type == "git.push");
        Assert.Equal("{{branch}}", push.Params.Single(p => p.Key == "branch").Default);
    }
}
