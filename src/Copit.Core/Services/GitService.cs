namespace Copit.Core.Services;

public record GitChange(string Status, string Path)
{
    public string Kind => Status.Trim() switch
    {
        "M" or "MM" or "AM" => "Geändert",
        "A" => "Neu",
        "D" => "Gelöscht",
        "R" => "Umbenannt",
        "??" => "Neu",
        "UU" => "Konflikt",
        _ => Status.Trim()
    };
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

public record GitStatus(string Branch, string? Upstream, int Ahead, int Behind, List<GitChange> Changes, bool IsRepo);

public record GitCommit(string Hash, DateTime Date, string Author, string Subject, string Repo);

/// <summary>Lokale Git-Operationen über git.exe.</summary>
public class GitService
{
    private CliCommand? _git;

    private CliCommand Git => _git ??= CliLocator.Resolve("git") ?? new CliCommand("git", [], "git");

    public async Task<ProcessResult> RunAsync(string dir, CancellationToken ct, params string[] args)
    {
        var all = new List<string>(Git.PrefixArgs) { "-c", "core.quotepath=off", "-c", "color.ui=never" };
        all.AddRange(args);
        return await ProcessRunner.RunAsync(Git.FileName, all, dir, ct: ct);
    }

    private async Task<string> OutAsync(string dir, CancellationToken ct, params string[] args)
    {
        var r = await RunAsync(dir, ct, args);
        if (!r.Ok) throw new InvalidOperationException((r.StdErr + r.StdOut).Trim());
        return r.StdOut;
    }

    public static bool IsRepo(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
        (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")));

    public async Task<GitStatus> GetStatusAsync(string dir, CancellationToken ct = default)
    {
        if (!IsRepo(dir)) return new GitStatus("", null, 0, 0, [], false);
        var r = await RunAsync(dir, ct, "status", "--porcelain=v1", "-b", "-uall");
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        string branch = "", upstream = "";
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        foreach (var l in lines)
        {
            if (l.StartsWith("## "))
            {
                var head = l[3..];
                var bracket = head.IndexOf(" [", StringComparison.Ordinal);
                var info = bracket >= 0 ? head[(bracket + 2)..].TrimEnd(']') : "";
                if (bracket >= 0) head = head[..bracket];
                var dots = head.IndexOf("...", StringComparison.Ordinal);
                if (dots >= 0) { branch = head[..dots]; upstream = head[(dots + 3)..]; }
                else branch = head.Replace("No commits yet on ", "");
                foreach (var part in info.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (part.StartsWith("ahead ")) int.TryParse(part[6..], out ahead);
                    if (part.StartsWith("behind ")) int.TryParse(part[7..], out behind);
                }
                continue;
            }
            if (l.Length < 4) continue;
            var status = l[..2];
            var path = l[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            changes.Add(new GitChange(status, path.Trim('"')));
        }
        return new GitStatus(branch, upstream.Length > 0 ? upstream : null, ahead, behind, changes, true);
    }

    public async Task<string> GetDiffAsync(string dir, string? path = null, bool staged = false, CancellationToken ct = default)
    {
        var args = new List<string> { "diff", "--no-ext-diff" };
        if (staged) args.Add("--cached");
        if (path != null) { args.Add("--"); args.Add(path); }
        var diff = (await RunAsync(dir, ct, [.. args])).StdOut;
        if (string.IsNullOrWhiteSpace(diff) && path != null)
        {
            // Untracked Datei: als "neu" anzeigen
            var full = Path.Combine(dir, path);
            if (File.Exists(full))
            {
                var content = await File.ReadAllLinesAsync(full, ct);
                return $"+++ {path} (neue Datei)\n" + string.Join('\n', content.Take(800).Select(l => "+" + l));
            }
        }
        return diff;
    }

    /// <summary>Kompletter Diff (inkl. untracked Dateien in Kurzform) für Commit-Messages / Reviews.</summary>
    public async Task<string> GetFullDiffAsync(string dir, int maxChars = 80_000, CancellationToken ct = default)
    {
        var diff = (await RunAsync(dir, ct, "diff", "HEAD", "--no-ext-diff", "--stat", "-p")).StdOut;
        var status = await GetStatusAsync(dir, ct);
        foreach (var c in status.Changes.Where(c => c.Status == "??").Take(20))
            diff += "\n" + await GetDiffAsync(dir, c.Path, ct: ct);
        return diff.Length > maxChars ? diff[..maxChars] + "\n... (gekürzt)" : diff;
    }

    public async Task<string> GetBranchDiffAsync(string dir, string target, int maxChars = 120_000, CancellationToken ct = default)
    {
        var diff = (await RunAsync(dir, ct, "diff", $"{target}...HEAD", "--no-ext-diff")).StdOut;
        return diff.Length > maxChars ? diff[..maxChars] + "\n... (gekürzt)" : diff;
    }

    public async Task<List<string>> GetBranchesAsync(string dir, CancellationToken ct = default)
    {
        var o = await OutAsync(dir, ct, "branch", "--format=%(refname:short)", "--sort=-committerdate");
        return o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public Task<string> CheckoutAsync(string dir, string branch, CancellationToken ct = default) => OutAsync(dir, ct, "checkout", branch);
    public Task<string> CreateBranchAsync(string dir, string branch, CancellationToken ct = default) => OutAsync(dir, ct, "checkout", "-b", branch);
    public Task<string> FetchAsync(string dir, CancellationToken ct = default) => OutAsync(dir, ct, "fetch", "--prune");
    public Task<string> PullAsync(string dir, CancellationToken ct = default) => OutAsync(dir, ct, "pull", "--rebase", "--autostash");

    public async Task<string> CommitAllAsync(string dir, string message, CancellationToken ct = default)
    {
        await OutAsync(dir, ct, "add", "-A");
        return await OutAsync(dir, ct, "commit", "-m", message);
    }

    public async Task<string> PushAsync(string dir, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(dir, ct);
        var r = status.Upstream == null
            ? await RunAsync(dir, ct, "push", "-u", "origin", status.Branch)
            : await RunAsync(dir, ct, "push");
        if (!r.Ok) throw new InvalidOperationException((r.StdErr + r.StdOut).Trim());
        return (r.StdErr + r.StdOut).Trim();
    }

    public async Task<string> DiscardAsync(string dir, string path, CancellationToken ct = default)
    {
        var r = await RunAsync(dir, ct, "checkout", "--", path);
        if (!r.Ok) await RunAsync(dir, ct, "clean", "-f", "--", path);
        return "";
    }

    public async Task<string?> GetRemoteUrlAsync(string dir, CancellationToken ct = default)
    {
        if (!IsRepo(dir)) return null;
        var r = await RunAsync(dir, ct, "remote", "get-url", "origin");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    public async Task<string> GetUserEmailAsync(string dir, CancellationToken ct = default)
        => (await RunAsync(dir, ct, "config", "user.email")).StdOut.Trim();

    public async Task<List<GitCommit>> GetCommitsAsync(string dir, DateTime since, DateTime until, string? author = null, CancellationToken ct = default)
    {
        if (!IsRepo(dir)) return [];
        var args = new List<string> { "log", "--all", "--no-merges", $"--since={since:yyyy-MM-dd HH:mm}", $"--until={until:yyyy-MM-dd HH:mm}",
            "--pretty=format:%h\x1f%aI\x1f%an\x1f%s" };
        if (!string.IsNullOrWhiteSpace(author)) args.Add($"--author={author}");
        var r = await RunAsync(dir, ct, [.. args]);
        var repo = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\x1f'))
            .Where(p => p.Length == 4)
            .Select(p => new GitCommit(p[0], DateTime.TryParse(p[1], out var d) ? d : DateTime.MinValue, p[2], p[3], repo))
            .ToList();
    }

    public async Task<List<GitCommit>> GetRecentCommitsAsync(string dir, int count = 15, CancellationToken ct = default)
    {
        if (!IsRepo(dir)) return [];
        var r = await RunAsync(dir, ct, "log", $"-{count}", "--pretty=format:%h\x1f%aI\x1f%an\x1f%s");
        var repo = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\x1f')).Where(p => p.Length == 4)
            .Select(p => new GitCommit(p[0], DateTime.TryParse(p[1], out var d) ? d : DateTime.MinValue, p[2], p[3], repo)).ToList();
    }

    public async Task<List<string>> GetTagsAsync(string dir, CancellationToken ct = default)
    {
        var r = await RunAsync(dir, ct, "tag", "--sort=-creatordate");
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30).ToList();
    }

    public async Task<string> GetLogRangeAsync(string dir, string from, string to = "HEAD", CancellationToken ct = default)
    {
        var range = string.IsNullOrWhiteSpace(from) ? to : $"{from}..{to}";
        var r = await RunAsync(dir, ct, "log", range, "--no-merges", "-200", "--pretty=format:- %s (%an, %h)");
        return r.StdOut;
    }
}
