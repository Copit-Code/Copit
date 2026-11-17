using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class SandboxTests
{
    [Fact]
    public void ProxyAllowsOnlyListAndBlocksInternalFirst()
    {
        var conf = SandboxService.SquidConf([".anthropic.com", "api.github.com", " ", "evil host"]);
        var lines = conf.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("acl allowed dstdomain .anthropic.com api.github.com", lines);
        Assert.Contains(lines, l => l.StartsWith("acl internal dst") && l.Contains("10.0.0.0/8") && l.Contains("192.168.0.0/16") && l.Contains("172.16.0.0/12"));
        // Reihenfolge: interne Ziele sperren, bevor die Allowlist greift; am Ende alles andere sperren
        Assert.True(lines.IndexOf("http_access deny internal") < lines.IndexOf("http_access allow allowed"));
        Assert.Equal("http_access deny all", lines.Last(l => l.StartsWith("http_access")));
        Assert.DoesNotContain("evil host", conf);
    }

    [Fact]
    public void RunArgsUseInternalNetworkAndMountOnlyWorkspace()
    {
        var home = Path.Combine(Path.GetTempPath(), "copit-home-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-x" };
        var a = SandboxService.RunArgs("copit-sandbox:latest", "copit-agent-abc", @"C:\sb\repo", AiProvider.Claude, "claude", ["-p", "--permission-mode", "bypassPermissions"],
            home, n => env.GetValueOrDefault(n), ("Fabian", "f@x"));

        Assert.Equal("run", a[0]);
        Assert.Equal(SandboxService.Network, a[a.IndexOf("--network") + 1]);
        Assert.DoesNotContain("host", a);
        Assert.Contains(@"C:\sb\repo:/work", a);
        Assert.Contains($"HTTPS_PROXY=http://{SandboxService.Proxy}:3128", a);
        Assert.Contains(Path.Combine(home, ".claude") + ":/home/node/.claude", a);
        Assert.Contains("ANTHROPIC_API_KEY", a);               // nur der Name – Wert kommt aus der Umgebung, nicht in die Kommandozeile
        Assert.DoesNotContain(a, x => x.Contains("sk-x"));
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", a);  // nicht gesetzt → nicht durchgereicht
        Assert.Contains("GIT_AUTHOR_NAME=Fabian", a);
        // Image, dann CLI, dann ihre Argumente
        var i = a.IndexOf("copit-sandbox:latest");
        Assert.Equal(["claude", "-p", "--permission-mode", "bypassPermissions"], a.Skip(i + 1));
        Directory.Delete(home, true);
    }

    [Fact]
    public void ContainerNameIsDockerSafe()
    {
        Assert.Equal("copit-agent-ab12cd34ef56", SandboxService.ContainerName("AB12-CD34_EF56-7890"));
    }

    [Fact]
    public async Task WorkspaceIsSeparateCloneOnBranchWithRealOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), "copit-sbws-" + Guid.NewGuid().ToString("N")[..8]);
        var remote = Path.Combine(root, "remote.git"); var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(root);
        async Task Git(string dir, params string[] a) { var r = await ProcessRunner.RunAsync("git", ["-C", dir, .. a]); Assert.True(r.Ok, r.StdErr); }
        await Git(root, "init", "--bare", "-q", remote);
        await Git(root, "init", "-q", "-b", "main", repo);
        await Git(repo, "config", "user.email", "t@t"); await Git(repo, "config", "user.name", "t");
        await Git(repo, "commit", "--allow-empty", "-qm", "init");
        await Git(repo, "remote", "add", "origin", remote);
        File.WriteAllText(Path.Combine(repo, "lokal.txt"), "nicht committet");

        var git = new GitService();
        var ws = await SandboxService.CreateWorkspaceAsync(git, repo, "feature/ST-9", "sess123456");
        try
        {
            Assert.NotEqual(repo, ws);
            Assert.False(File.Exists(Path.Combine(ws, "lokal.txt")));                // dein Arbeitsstand bleibt draußen
            Assert.Equal("feature/ST-9", (await git.GetStatusAsync(ws)).Branch);
            Assert.Equal(remote, (await git.GetRemoteUrlAsync(ws))?.TrimEnd('/'));   // Push vom Host geht ans echte Remote
            Assert.Equal("main", (await git.GetStatusAsync(repo)).Branch);            // Projekt-Repo unverändert
        }
        finally { try { Directory.Delete(root, true); Directory.Delete(ws, true); } catch { } }
    }
}
