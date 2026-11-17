using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class DeliveryTests
{
    private static DeliveryStep S(string key, StepState st, string detail = "x") => new(key, key, st, detail);

    [Fact]
    public void FailedPipelineWinsTheHeadline()
    {
        var (o, h) = DeliveryService.Summarize([S("commit", StepState.Ok), S("push", StepState.Ok), S("pipeline", StepState.Fail)]);
        Assert.Equal(StepState.Fail, o);
        Assert.Equal("Pipeline fehlgeschlagen", h);
    }

    [Fact]
    public void UncommittedAndUnpushedAreReported()
    {
        Assert.StartsWith("Nicht committet", DeliveryService.Summarize([S("commit", StepState.Warn, "2 Änderungen nicht committet"), S("push", StepState.Ok)]).Item2);
        Assert.StartsWith("Nicht gepusht", DeliveryService.Summarize([S("commit", StepState.Ok), S("push", StepState.Warn, "1 Commit nicht gepusht")]).Item2);
    }

    [Fact]
    public void GreenWhenPushedAndPipelinePassed()
    {
        var (o, h) = DeliveryService.Summarize([S("commit", StepState.Ok), S("push", StepState.Ok), S("mr", StepState.Running), S("pipeline", StepState.Ok)]);
        Assert.Equal(StepState.Ok, o);
        Assert.Equal("Gepusht · Pipeline grün · MR offen", h);
        Assert.Equal("Fertig – gemergt, Ticket erledigt",
            DeliveryService.Summarize([S("push", StepState.Ok), S("mr", StepState.Ok), S("pipeline", StepState.Ok), S("jira", StepState.Ok)]).Item2);
        Assert.Equal(StepState.Running, DeliveryService.Summarize([S("push", StepState.Ok), S("pipeline", StepState.Running)]).Item1);
    }

    [Fact]
    public void PipelineStepNamesFailedJobs()
    {
        var p = new GitLabPipeline(42, "failed", "feature/x", "abc", null, null, "https://gl/p/42", null);
        var jobs = new List<GitLabJob>
        {
            new(1, "build", "build", "success", 30, "", false),
            new(2, "unit-tests", "test", "failed", 12, "", false),
            new(3, "lint", "test", "failed", 3, "", true),
        };
        var s = DeliveryService.PipelineStep(p, jobs);
        Assert.Equal(StepState.Fail, s.State);
        Assert.Equal("#42 fehlgeschlagen: unit-tests", s.Detail);
        Assert.Equal(StepState.Running, DeliveryService.PipelineStep(p with { Status = "running" }, jobs).State);
    }

    [Fact]
    public async Task GitStepsReflectCommitAndPush()
    {
        var root = Path.Combine(Path.GetTempPath(), "copit-deliv-" + Guid.NewGuid().ToString("N")[..8]);
        var origin = Path.Combine(root, "origin.git"); var work = Path.Combine(root, "work");
        Directory.CreateDirectory(root);
        async Task Git(string dir, params string[] a) { var r = await ProcessRunner.RunAsync("git", ["-C", dir, .. a]); Assert.True(r.Ok, r.StdErr); }
        await Git(root, "init", "--bare", "-q", origin);
        await Git(root, "init", "-q", "-b", "main", work);
        await Git(work, "config", "user.email", "t@t"); await Git(work, "config", "user.name", "t");
        await Git(work, "commit", "--allow-empty", "-qm", "init");
        await Git(work, "remote", "add", "origin", origin);
        await Git(work, "checkout", "-qb", "feature/ST-1");

        var git = new GitService();
        var svc = new DeliveryService(git, new ConnectionHub(new SettingsService(new PlainProtector())));
        var agent = new ChatSession { Branch = "feature/ST-1", Cwd = work };

        File.WriteAllText(Path.Combine(work, "a.txt"), "x");
        var d = await svc.CheckAsync(agent, work, null);
        Assert.Equal(StepState.Warn, d!.Step("commit")!.State);
        Assert.StartsWith("Nicht committet", d.Headline);

        await Git(work, "add", "-A"); await Git(work, "commit", "-qm", "ST-1: a");
        d = await svc.CheckAsync(agent, work, null);
        Assert.Equal(StepState.Ok, d!.Step("commit")!.State);
        Assert.Equal(StepState.Warn, d.Step("push")!.State);

        await git.PushBranchAsync(work, "feature/ST-1");
        d = await svc.CheckAsync(agent, work, null);
        Assert.Equal(StepState.Ok, d!.Step("push")!.State);
        Assert.Equal("Gepusht", d.Headline);

        await Git(work, "commit", "--allow-empty", "-qm", "noch einer");
        d = await svc.CheckAsync(agent, work, null);
        Assert.Equal("1 Commit nicht gepusht", d!.Step("push")!.Detail);
        try { Directory.Delete(root, true); } catch { }
    }
}

public class SessionDiffParseTests
{
    [Fact]
    public void ParsesUntrackedFileDiffWithoutGitHeader()
    {
        var files = DiffParser.Parse("--- /dev/null\n+++ b/neu.txt\n@@ -0,0 +1,2 @@\n+a\n+b\n");
        Assert.Single(files);
        Assert.Equal(2, files[0].Added);
    }
}
