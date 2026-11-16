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
    public void AutomationDueOncePerDay()
    {
        var a = new Automation { Kind = AutomationKind.BookingCheck, TimeOfDay = "16:30", Days = [DayOfWeek.Monday] };
        var monday = new DateTime(2026, 11, 16, 16, 45, 0);
        Assert.True(AutomationService.IsDue(a, monday));
        a.LastRun = monday;
        Assert.False(AutomationService.IsDue(a, monday.AddMinutes(10)));
        Assert.False(AutomationService.IsDue(new Automation { TimeOfDay = "16:30", Days = [DayOfWeek.Monday] }, monday.AddDays(1)));
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
