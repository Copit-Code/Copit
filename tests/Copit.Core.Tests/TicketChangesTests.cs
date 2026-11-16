using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class TicketChangesTests
{
    [Fact]
    public void ParsesUnifiedDiffWithLineNumbers()
    {
        var diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 111..222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -10,4 +10,5 @@ public class A
             int x = 1;
            -int y = 2;
            +int y = 3;
            +int z = 4;
             return;
            \ No newline at end of file
            diff --git a/old.txt b/new.txt
            similarity index 90%
            rename from old.txt
            rename to new.txt
            diff --git a/img.png b/img.png
            new file mode 100644
            Binary files /dev/null and b/img.png differ
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal(3, files.Count);
        var a = files[0];
        Assert.Equal(("src/A.cs", "M", 2, 1), (a.Path, a.Status, a.Added, a.Removed));
        Assert.Equal("public class A", a.Hunks[0].Context);
        var lines = a.Hunks[0].Lines;
        Assert.Equal((DiffLineKind.Context, (int?)10, (int?)10), (lines[0].Kind, lines[0].OldNo, lines[0].NewNo));
        Assert.Equal((DiffLineKind.Removed, (int?)11, (int?)null), (lines[1].Kind, lines[1].OldNo, lines[1].NewNo));
        Assert.Equal((DiffLineKind.Added, (int?)null, (int?)12), (lines[3].Kind, lines[3].OldNo, lines[3].NewNo));
        Assert.Equal((int?)13, lines[4].NewNo);
        Assert.Equal(DiffLineKind.Note, lines[5].Kind);
        Assert.Equal(("new.txt", "old.txt", "R"), (files[1].Path, files[1].OldPath, files[1].Status));
        Assert.True(files[2].Binary);
        Assert.Equal("A", files[2].Status);
    }

    [Fact]
    public void ParsesUntrackedFileFromGetDiff()
    {
        var f = DiffParser.Parse("+++ docs/neu.md (neue Datei)\n+# Titel\n+Text").Single();
        Assert.Equal(("docs/neu.md", "A", 2), (f.Path, f.Status, f.Added));
        Assert.Equal((int?)2, f.Hunks[0].Lines[1].NewNo);
    }

    private static async Task Git(string dir, params string[] args)
    {
        var r = await new GitService().RunAsync(dir, default, ["-c", "user.name=Test", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", .. args]);
        Assert.True(r.ExitCode == 0, r.StdErr);
    }

    [Fact]
    public async Task CollectsCommitsBranchesAndWorkspaces()
    {
        var repo = Directory.CreateTempSubdirectory("copit-tc").FullName;
        await Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "eins\n");
        await Git(repo, "add", "."); await Git(repo, "commit", "-q", "-m", "Start");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "eins\nzwei\n");
        await Git(repo, "commit", "-q", "-am", "SHOP-1: zweite Zeile");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "anderes Ticket\n");
        await Git(repo, "add", "."); await Git(repo, "commit", "-q", "-m", "SHOP-12: fremd");
        await Git(repo, "checkout", "-q", "-b", "feature/SHOP-1");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "eins\nzwei\ndrei\n");
        await Git(repo, "mv", "b.txt", "c.txt");
        await Git(repo, "commit", "-q", "-am", "weiter ohne Key");
        File.WriteAllText(Path.Combine(repo, "neu.txt"), "offen\n");

        var svc = new TicketChangesService(new GitService());
        var agent = new ChatSession { Title = "Agent", Ticket = "shop-1", Cwd = repo };
        var t = await svc.LoadAsync(repo, "shop-1", [agent]);
        Assert.Equal(["SHOP-1: zweite Zeile", "weiter ohne Key"], t.Commits.Select(c => c.Subject));
        Assert.Contains("feature/SHOP-1", t.Branches);
        Assert.Single(t.Workspaces);

        var net = await svc.NetDiffAsync(repo, t);
        var a = net.Single(f => f.Path == "a.txt");
        Assert.Equal((2, 0), (a.Added, a.Removed));      // Gesamt: zwei + drei
        Assert.Contains(net, f => f.Path == "c.txt");

        var ws = await svc.WorkspaceDiffAsync(repo);
        Assert.Contains(ws, f => f.Path == "neu.txt" && f.Status == "A");

        var sums = await svc.SummariesAsync(repo);
        Assert.Contains(sums, s => s.Ticket == "SHOP-1" && s.Commits >= 1);
        Assert.Contains(sums, s => s.Ticket == "SHOP-12");
    }
}
