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
