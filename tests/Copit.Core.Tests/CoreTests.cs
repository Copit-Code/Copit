using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class ClaudeStreamParserTests
{
    [Fact]
    public void ParsesInitEvent()
    {
        var e = ClaudeStreamParser.Parse("""{"type":"system","subtype":"init","session_id":"abc","model":"claude-opus","tools":["Read","Edit"],"permissionMode":"acceptEdits"}""").Single();
        var init = Assert.IsType<AgentInit>(e);
        Assert.Equal("abc", init.SessionId);
        Assert.Equal(2, init.Tools.Count);
    }

    [Fact]
    public void ParsesAssistantTextToolUseAndUsage()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hallo"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}],"usage":{"input_tokens":10,"cache_read_input_tokens":1000,"cache_creation_input_tokens":5,"output_tokens":3}}}""";
        var events = ClaudeStreamParser.Parse(line).ToList();
        Assert.Equal("Hallo", Assert.IsType<AgentText>(events[0]).Text);
        var tool = Assert.IsType<AgentToolUse>(events[1]);
        Assert.Equal("Bash", tool.Name);
        Assert.Contains("ls", tool.InputJson);
        Assert.Equal(1015, Assert.IsType<AgentUsage>(events[2]).ContextTokens);
    }

    [Fact]
    public void ParsesDeltaToolResultAndResult()
    {
        Assert.Equal("Hi", Assert.IsType<AgentTextDelta>(ClaudeStreamParser.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}}""").Single()).Text);

        var tr = Assert.IsType<AgentToolResult>(ClaudeStreamParser.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"ok"}],"is_error":false}]}}""").Single());
        Assert.Equal("ok", tr.Content);

        var res = Assert.IsType<AgentResult>(ClaudeStreamParser.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"Fertig","session_id":"s1","total_cost_usd":0.12,"duration_ms":1200,"num_turns":3,"permission_denials":[{"tool_name":"Bash","tool_use_id":"x","tool_input":{"command":"rm -rf"}}]}""").Single());
        Assert.Equal("Fertig", res.Text);
        Assert.Equal(0.12, res.CostUsd, 3);
        Assert.Single(res.Denials);
        Assert.Equal("Bash", res.Denials[0].ToolName);
    }

    [Fact]
    public void IgnoresGarbage()
    {
        Assert.Empty(ClaudeStreamParser.Parse("not json"));
        Assert.Empty(ClaudeStreamParser.Parse("{broken"));
    }
}

public class ParsingTests
{
    [Fact]
    public void ExtractsQuestions()
    {
        var (clean, qs) = QuestionParser.Extract("Ich habe zwei Wege.\n<rueckfrage>\nFrage: Welche DB?\n- Postgres\n- SQLite\n</rueckfrage>");
        Assert.Equal("Ich habe zwei Wege.", clean);
        Assert.Single(qs);
        Assert.Equal("Welche DB?", qs[0].Text);
        Assert.Equal(["Postgres", "SQLite"], qs[0].Options);
    }

    [Fact]
    public void ParsesBookingPlanFromFencedJson()
    {
        var plan = TimeBookingService.ParsePlan("Hier:\n```json\n{\"summary\":\"Tag\",\"bookings\":[{\"issueKey\":\"abc-1\",\"hours\":\"2,5\",\"comment\":\"x\"},{\"issueKey\":\"ABC-2\",\"hours\":5.5}]}\n```");
        Assert.Equal("Tag", plan.Summary);
        Assert.Equal(2, plan.Bookings.Count);
        Assert.Equal("ABC-1", plan.Bookings[0].IssueKey);
        Assert.Equal(2.5, plan.Bookings[0].Hours);
    }

    [Fact]
    public void ParsesBareJsonArray()
    {
        var plan = TimeBookingService.ParsePlan("[{\"issueKey\":\"X-1\",\"hours\":1}] fertig");
        Assert.Single(plan.Bookings);
    }

    [Fact]
    public void NormalizeHitsTargetExactly()
    {
        var items = new List<BookingSuggestion> { new() { Hours = 3.1 }, new() { Hours = 2.2 }, new() { Hours = 1.3 } };
        TimeBookingService.Normalize(items, 7.5, 15);
        Assert.Equal(7.5, items.Sum(i => i.Hours), 3);
        Assert.All(items, i => Assert.Equal(0, i.Hours * 4 % 1, 3));
    }

    [Theory]
    [InlineData("git@gitlab.firma.local:team/sub/app.git", "team/sub/app")]
    [InlineData("https://gitlab.firma.local/team/app.git", "team/app")]
    [InlineData("ssh://git@gitlab.firma.local:2222/team/app.git", "team/app")]
    public void ParsesRemotePath(string remote, string expected) =>
        Assert.Equal(expected, GitLabClient.ProjectPathFromRemote(remote));

    [Fact]
    public void FormatsEstimates()
    {
        Assert.Equal("4h", JiraClient.FormatEstimate(4));
        Assert.Equal("1h 30m", JiraClient.FormatEstimate(1.5));
        Assert.Equal("45m", JiraClient.FormatEstimate(0.75));
    }

    [Fact]
    public void IssueBucketDetectsReview()
    {
        Assert.Equal(IssueBucket.Review, new JiraIssue { Status = "In Review", StatusCategory = "indeterminate" }.Bucket);
        Assert.Equal(IssueBucket.InProgress, new JiraIssue { Status = "In Arbeit", StatusCategory = "indeterminate" }.Bucket);
        Assert.Equal(IssueBucket.Done, new JiraIssue { Status = "Closed", StatusCategory = "done" }.Bucket);
    }

    [Fact]
    public void ParsesQuestionsAndUnassignedBlocks()
    {
        var plan = TimeBookingService.ParsePlan("{\"summary\":\"x\",\"bookings\":[{\"issueKey\":\"\",\"hours\":2,\"topic\":\"Bildgenerierung\",\"repo\":\"ai-backend\"}],\"questions\":[{\"question\":\"Wohin gehören Dailys?\",\"options\":[\"ORG-1\",\"HAW-2\"]}]}");
        Assert.Single(plan.Bookings);
        Assert.Equal("", plan.Bookings[0].IssueKey);
        Assert.Equal("ai-backend", plan.Bookings[0].Repo);
        Assert.Equal(["ORG-1", "HAW-2"], plan.Questions[0].Options);
    }

    [Fact]
    public void LearnsTopicsAndFacts()
    {
        var map = new List<TopicMapping>();
        TimeBookingService.LearnTopic(map, "Checkout", "shop", "SHOP-1");
        TimeBookingService.LearnTopic(map, "checkout", "shop", "SHOP-1");
        Assert.Equal(2, map[0].Hits);
        TimeBookingService.LearnTopic(map, "Checkout", "shop", "WEI-9", 3);
        Assert.Equal("WEI-9", map[0].Ticket);
        TimeBookingService.LearnTopic(map, "Checkout", "shop", "SHOP-1");
        Assert.Equal("WEI-9", map[0].Ticket);
        var facts = new List<TimeFact>();
        TimeBookingService.LearnFact(facts, "Dailys?", "ORG-1");
        TimeBookingService.LearnFact(facts, "dailys?", "ORG-2");
        Assert.Single(facts);
        Assert.Equal("ORG-2", facts[0].Answer);
    }

    [Fact]
    public void HumanEffortNeverPadsButCaps()
    {
        var items = new List<BookingSuggestion> { new() { IssueKey = "A-1", Hours = 1.1 }, new() { IssueKey = "A-2", Hours = 2.4 } };
        TimeBookingService.Cap(items, 8, 15);
        Assert.Equal(3.5, items.Sum(i => i.Hours), 3); // nicht auf 8 h aufgefüllt
        var big = new List<BookingSuggestion> { new() { IssueKey = "A-1", Hours = 6 }, new() { IssueKey = "A-2", Hours = 6 } };
        TimeBookingService.Cap(big, 8, 15);
        Assert.Equal(8, big.Sum(i => i.Hours), 3); // aber nie mehr als das Ziel
    }

    [Fact]
    public void UpdaterComparesVersions()
    {
        Assert.Equal(new Version(1, 2, 3), UpdateService.Parse("v1.2.3"));
        Assert.Equal(new Version(0, 3, 0), UpdateService.Parse("0.3.0+abc123"));
        Assert.Equal(new Version(1, 0, 0), UpdateService.Parse("1.0"));
        Assert.True(UpdateService.IsNewer(new Version(0, 3, 0), new Version(0, 2, 9)));
        Assert.False(UpdateService.IsNewer(new Version(0, 3, 0), new Version(0, 3, 0, 0)));
    }

    [Fact]
    public void AutomationDueOncePerDay()
    {
        var a = new Automation { Trigger = "schedule", TriggerParams = new() { ["time"] = "16:30", ["days"] = "Mo" } };
        var monday = new DateTime(2026, 11, 16, 16, 45, 0);
        Assert.True(AutomationService.IsDue(a, monday));
        a.LastRun = monday;
        Assert.False(AutomationService.IsDue(a, monday.AddMinutes(10)));
        Assert.False(AutomationService.IsDue(new Automation { Trigger = "schedule", TriggerParams = new() { ["time"] = "16:30", ["days"] = "Mo" } }, monday.AddDays(1)));
    }

    [Fact]
    public void AutomationTriggerAndConditions()
    {
        var a = new Automation { Trigger = "agent.moved", TriggerParams = new() { ["to"] = "InProgress" }, Conditions = [new() { Field = "ticket", Op = "matches", Value = "^SHOP-" }] };
        Assert.True(AutomationService.TriggerMatches(a, new("agent.moved", new() { ["to"] = "inprogress", ["ticket"] = "SHOP-12" })));
        Assert.False(AutomationService.TriggerMatches(a, new("agent.moved", new() { ["to"] = "Review", ["ticket"] = "SHOP-12" })));
        Assert.False(AutomationService.TriggerMatches(a, new("agent.moved", new() { ["to"] = "InProgress", ["ticket"] = "ABC-1" })));
        Assert.Equal("Fix SHOP-1 ", AutomationService.Render("Fix {{ ticket }} {{missing}}", new Dictionary<string, string> { ["ticket"] = "SHOP-1" }));
    }

    [Fact]
    public void PermissionPatternsForChainedCommands()
    {
        Assert.Equal(["git add", "git commit -m \"a && b\"", "npm test"], PermissionService.SplitCommands("git add . && git commit -m \"a && b\" | npm test").Select(x => x.Replace(" .", "")).ToArray());
        Assert.Equal(["Bash(git add:*)", "Bash(git commit:*)"], PermissionService.PatternsForDenial("Bash", "{\"command\":\"cd C:/x && git add . && git commit -m x\"}"));
        Assert.Equal(["Edit"], PermissionService.PatternsForDenial("Edit", "{}"));
        Assert.Equal("shell(git push)", PermissionService.ToCopilotTool("Bash(git push:*)"));
        Assert.Equal("write", PermissionService.ToCopilotTool("Edit"));
        Assert.Null(PermissionService.ToCopilotTool("WebFetch"));
    }

    [Fact]
    public void ActivityUnionAndSegments()
    {
        var t0 = new DateTime(2026, 11, 16, 9, 0, 0);
        var segs = ActivityService.SegmentsOf([t0, t0.AddMinutes(5), t0.AddMinutes(9), t0.AddMinutes(40), t0.AddMinutes(45)]);
        Assert.Equal(2, segs.Count);
        Assert.Equal(TimeSpan.FromMinutes(9), segs[0].Item2 - segs[0].Item1);
        // parallel: 9:00-10:00 und 9:30-10:30 → 1,5 h
        Assert.Equal(TimeSpan.FromMinutes(90), ActivityService.Union([(t0, t0.AddHours(1)), (t0.AddMinutes(30), t0.AddMinutes(90))]));
        Assert.Equal(25 / 1_000_000d * 1000 + 5 / 1_000_000d * 1000, Pricing.Cost(1000, 1000, 0, 0, "claude-opus-4-1"), 6);
        Assert.Equal("Opus 4.1", Pricing.PrettyModel("claude-opus-4-1-20250805"));
    }

    [Fact]
    public void BudgetEvaluatesWeekAndFolders()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0); // Mittwoch KW 47
        var recs = new List<UsageRecord>
        {
            new(new DateTime(2026, 11, 16, 9, 0, 0), new DateTime(2026, 11, 16, 11, 0, 0), 10, @"C:\GIT\Shop", "Shop", "CLI"),
            new(new DateTime(2026, 11, 17, 9, 0, 0), new DateTime(2026, 11, 17, 10, 0, 0), 5, @"C:\GIT\Shop\api", "Shop/api", "CLI"),
            new(new DateTime(2026, 11, 12, 9, 0, 0), new DateTime(2026, 11, 12, 10, 0, 0), 99, @"C:\GIT\Shop", "Shop", "CLI"),
            new(new DateTime(2026, 11, 17, 9, 0, 0), new DateTime(2026, 11, 17, 10, 0, 0), 7, @"C:\GIT\Other", "Other", "CLI"),
        };
        var cost = BudgetService.Evaluate(new Budget { Amount = 30, Folders = [@"C:\GIT\Shop"] }, recs, now);
        Assert.Equal(15, cost.Used, 3);
        Assert.Equal("KW 47", cost.PeriodLabel);
        var hours = BudgetService.Evaluate(new Budget { Type = BudgetType.Hours, Amount = 10 }, recs, now);
        Assert.Equal(4, hours.Used, 3);
        Assert.Equal([80], BudgetService.NewThresholds(new BudgetStatus(new Budget { WarnAt = [80, 100] }, 9, 0.9, "", "k")));
    }

    [Fact]
    public void CopilotPromptIncludesHistoryAndMode()
    {
        var p = CopilotCliRunner.ComposePrompt(new AgentRequest
        {
            Prompt = "Weiter", Mode = AgentMode.Plan,
            History = [new ChatMessage { Role = ChatRole.User, Text = "Baue X" }, new ChatMessage { Role = ChatRole.Assistant, Text = "Ok" }]
        });
        Assert.Contains("PLAN", p);
        Assert.Contains("Nutzer: Baue X", p);
        Assert.EndsWith("Weiter", p);
    }

    [Fact]
    public void ClaudeArgsContainResumeAndMode()
    {
        var args = ClaudeCliRunner.BuildArgs(new AgentRequest { Prompt = "x", Mode = AgentMode.Edit, ResumeSessionId = "s1", AllowedTools = ["Bash(git:*)"] }, "--max-turns 5");
        Assert.Contains("acceptEdits", args);
        Assert.Equal("s1", args[args.IndexOf("--resume") + 1]);
        Assert.Contains("Bash(git:*)", args);
        Assert.Equal("5", args[^1]);
    }
}
