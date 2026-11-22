using System.Globalization;
using System.Text.RegularExpressions;
using Copit.Core.Models;

namespace Copit.Core.Services;

// ---------------------------------------------------------------- Unified-Diff als Struktur

public enum DiffLineKind { Context, Added, Removed, Note }

public record DiffLine(DiffLineKind Kind, int? OldNo, int? NewNo, string Text);

public class DiffHunk
{
    public string Header { get; set; } = "";
    /// <summary>Funktion/Klasse aus dem Hunk-Kopf (Text nach dem zweiten @@).</summary>
    public string Context { get; set; } = "";
    public List<DiffLine> Lines { get; } = [];
}

public class FileDiff
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    /// <summary>A = neu, D = gelöscht, R = umbenannt, M = geändert.</summary>
    public string Status { get; set; } = "M";
    public bool Binary { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public List<DiffHunk> Hunks { get; } = [];
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

public static partial class DiffParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkRx();
    [GeneratedRegex(@"^diff --git a/(.+?) b/(.+)$")]
    private static partial Regex GitHeadRx();

    /// <summary>Zerlegt einen (git) Unified-Diff in Dateien, Hunks und Zeilen mit alten/neuen Zeilennummern.</summary>
    public static List<FileDiff> Parse(string? diff)
    {
        var files = new List<FileDiff>();
        FileDiff? f = null; DiffHunk? h = null;
        int oldNo = 0, newNo = 0;
        foreach (var raw in (diff ?? "").Replace("\r", "").Split('\n'))
        {
            var l = raw;
            if (l.StartsWith("diff --git "))
            {
                var m = GitHeadRx().Match(l);
                f = new FileDiff { Path = m.Success ? m.Groups[2].Value : l[11..] };
                if (m.Success && m.Groups[1].Value != m.Groups[2].Value) f.OldPath = m.Groups[1].Value;
                files.Add(f); h = null; continue;
            }
            if (f == null && (l.StartsWith("--- ") || l.StartsWith("+++ ")))
            {
                // Diff ohne "diff --git"-Kopf (z. B. neue Datei aus GetDiffAsync)
                f = new FileDiff { Path = l[4..].Replace(" (neue Datei)", "").Trim() };
                if (l.Contains("(neue Datei)")) { f.Status = "A"; h = new DiffHunk { Header = "neue Datei" }; f.Hunks.Add(h); newNo = 1; }
                files.Add(f);
                continue;
            }
            if (f == null) continue;
            if (h == null || !(l.StartsWith('+') || l.StartsWith('-') || l.StartsWith(' ') || l.StartsWith('\\') || l.StartsWith("@@")))
            {
                if (l.StartsWith("new file mode")) { f.Status = "A"; continue; }
                if (l.StartsWith("deleted file mode")) { f.Status = "D"; continue; }
                if (l.StartsWith("rename from ")) { f.OldPath = l[12..]; f.Status = "R"; continue; }
                if (l.StartsWith("rename to ")) { f.Path = l[10..]; f.Status = "R"; continue; }
                if (l.StartsWith("Binary files ") || l.StartsWith("GIT binary patch")) { f.Binary = true; continue; }
                if (l.StartsWith("--- ") || l.StartsWith("+++ ") || l.StartsWith("index ") || l.StartsWith("similarity ") || l.StartsWith("old mode") || l.StartsWith("new mode")) continue;
            }
            var hm = HunkRx().Match(l);
            if (hm.Success)
            {
                h = new DiffHunk { Header = l, Context = hm.Groups[5].Value.Trim() };
                f.Hunks.Add(h);
                oldNo = int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
                newNo = int.Parse(hm.Groups[3].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (h == null) continue;
            if (l.StartsWith('+')) { h.Lines.Add(new DiffLine(DiffLineKind.Added, null, newNo++, l[1..])); f.Added++; }
            else if (l.StartsWith('-')) { h.Lines.Add(new DiffLine(DiffLineKind.Removed, oldNo++, null, l[1..])); f.Removed++; }
            else if (l.StartsWith('\\')) h.Lines.Add(new DiffLine(DiffLineKind.Note, null, null, l.TrimStart('\\', ' ')));
            else if (l.StartsWith(' ')) h.Lines.Add(new DiffLine(DiffLineKind.Context, oldNo++, newNo++, l[1..]));
        }
        return files;
    }
}

// ---------------------------------------------------------------- Änderungen eines Tickets

public record TicketCommit(string Sha, string Author, DateTime Date, string Subject, int Added, int Removed, int Files)
{
    public string Short => Sha.Length > 8 ? Sha[..8] : Sha;
}

/// <summary>Noch nicht committete Arbeit eines Agents am Ticket (eigener Worktree oder Repo).</summary>
public record TicketWorkspace(string Title, string Dir, string? AgentId);

public class TicketChanges
{
    public string Ticket { get; set; } = "";
    public List<TicketCommit> Commits { get; } = [];
    public List<string> Branches { get; } = [];
    public List<TicketWorkspace> Workspaces { get; } = [];
    public int Added => Commits.Sum(c => c.Added);
    public int Removed => Commits.Sum(c => c.Removed);
    public int FileCount { get; set; }
    public DateTime? Last => Commits.Count > 0 ? Commits.Max(c => c.Date) : null;
}

/// <summary>Übersichtszeile für „Änderungen pro Ticket“.</summary>
public record TicketSummary(string Ticket, int Commits, int Files, int Added, int Removed, DateTime Last, List<string> Authors);

/// <summary>
/// Sammelt alle Code-Änderungen zu einem Ticket: Commits mit dem Key in der Message oder auf Branches mit dem Key im Namen
/// (alle Branches, auch remote), dazu nicht committete Änderungen von Agents, die am Ticket arbeiten.
/// </summary>
public partial class TicketChangesService(GitService git)
{
    private const char Rs = '\u001e', Us = '\u001f';

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]+-\d+\b")]
    private static partial Regex KeyRx();

    /// <summary>Tickets mit Commits im Zeitraum (für die Übersicht), neueste zuerst.</summary>
    public async Task<List<TicketSummary>> SummariesAsync(string repo, int days = 90, CancellationToken ct = default)
    {
        var r = await git.RunAsync(repo, ct, "log", "--all", $"--since={days}.days", "--date=iso-strict", $"--pretty=format:{Rs}%H{Us}%an{Us}%ad{Us}%s{Us}%D", "--numstat", "-n", "4000");
        var map = new Dictionary<string, (HashSet<string> Shas, HashSet<string> Files, int Add, int Del, DateTime Last, HashSet<string> Authors)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ParseLog(r.StdOut))
        {
            var keys = KeyRx().Matches(c.Subject.ToUpperInvariant() + " " + c.Refs.ToUpperInvariant()).Select(m => m.Value).Distinct();
            foreach (var k in keys)
            {
                if (!map.TryGetValue(k, out var e)) e = ([], new(StringComparer.OrdinalIgnoreCase), 0, 0, DateTime.MinValue, []);
                if (e.Shas.Add(c.Sha)) { e.Add += c.Added; e.Del += c.Removed; }
                foreach (var f in c.Files) e.Files.Add(f);
                if (c.Date > e.Last) e.Last = c.Date;
                e.Authors.Add(c.Author);
                map[k] = e;
            }
        }
        return [.. map.Select(x => new TicketSummary(x.Key, x.Value.Shas.Count, x.Value.Files.Count, x.Value.Add, x.Value.Del, x.Value.Last, [.. x.Value.Authors]))
            .OrderByDescending(x => x.Last)];
    }

    private static readonly Dictionary<string, (DateTime At, HashSet<string> Keys)> _keyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alle Ticket-Keys, die im Repo vorkommen: Commit-Betreffs und Refs (alle Branches, auch remote) sowie Branch-Namen. 5 Minuten gecacht.</summary>
    public async Task<HashSet<string>> RepoTicketKeysAsync(string repo, int days = 365, CancellationToken ct = default)
    {
        lock (_keyCache)
            if (_keyCache.TryGetValue(repo, out var c) && DateTime.Now - c.At < TimeSpan.FromMinutes(5)) return [.. c.Keys];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var log = await git.RunAsync(repo, ct, "log", "--all", $"--since={days}.days", "--pretty=format:%s %D", "-n", "20000");
        foreach (Match m in KeyRx().Matches(log.StdOut.ToUpperInvariant())) keys.Add(m.Value);
        var branches = await git.RunAsync(repo, ct, "branch", "-a", "--format=%(refname:short)");
        foreach (Match m in KeyRx().Matches(branches.StdOut.ToUpperInvariant())) keys.Add(m.Value);
        lock (_keyCache) _keyCache[repo] = (DateTime.Now, keys);
        return [.. keys];
    }

    private record LogEntry(string Sha, string Author, DateTime Date, string Subject, string Refs, int Added, int Removed, List<string> Files);

    private static IEnumerable<LogEntry> ParseLog(string outp)
    {
        // Git unter Windows liefert CRLF – sonst hängt \r an Betreff und Dateinamen
        foreach (var block in outp.Replace("\r", "").Split(Rs, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var head = lines[0].Split(Us);
            if (head.Length < 4) continue;
            DateTime.TryParse(head[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            int add = 0, del = 0; var files = new List<string>();
            foreach (var l in lines.Skip(1))
            {
                var p = l.Split('\t');
                if (p.Length < 3) continue;
                if (int.TryParse(p[0], out var a)) add += a;
                if (int.TryParse(p[1], out var d)) del += d;
                files.Add(p[2]);
            }
            yield return new LogEntry(head[0], head[1], date, head[3], head.Length > 4 ? head[4] : "", add, del, files);
        }
    }

    /// <summary>Commits, Branches und Arbeitsstände zum Ticket.</summary>
    public async Task<TicketChanges> LoadAsync(string repo, string ticket, IEnumerable<ChatSession>? agents = null, CancellationToken ct = default)
    {
        ticket = ticket.Trim().ToUpperInvariant();
        var res = new TicketChanges { Ticket = ticket };
        // Branches mit dem Key im Namen
        var refs = (await git.RunAsync(repo, ct, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes")).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b.Contains(ticket, StringComparison.OrdinalIgnoreCase) && !b.EndsWith("/HEAD")).ToList();
        res.Branches.AddRange(refs);
        var baseRef = await DefaultBranchAsync(repo, ct);
        var seen = new HashSet<string>();
        // 1) Commits mit Key in der Message (alle Branches)
        //    git sucht grob (Teilstring), exakt gefiltert wird hier – damit SHOP-1 nicht SHOP-12 trifft
        var cand = await git.RunAsync(repo, ct, "log", "--all", "-i", "-F", $"--grep={ticket}", $"--pretty=format:{Rs}%H{Us}%B", "-n", "500");
        var exact = new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(ticket)}(?!\d)", RegexOptions.IgnoreCase);
        var shas = cand.StdOut.Replace("\r", "").Split(Rs, StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split(Us, 2)).Where(p => p.Length == 2 && exact.IsMatch(p[1])).Select(p => p[0].Trim()).ToList();
        if (shas.Count > 0)
        {
            var byMsg = await git.RunAsync(repo, ct, ["show", "--no-walk=unsorted", "--date=iso-strict", $"--pretty=format:{Rs}%H{Us}%an{Us}%ad{Us}%s", "--numstat", .. shas]);
            foreach (var c in ParseLog(byMsg.StdOut)) if (seen.Add(c.Sha)) res.Commits.Add(new TicketCommit(c.Sha, c.Author, c.Date, c.Subject, c.Added, c.Removed, c.Files.Count));
        }
        // 2) Commits auf Ticket-Branches, die noch nicht im Hauptbranch sind
        foreach (var b in refs)
        {
            var range = baseRef != null ? $"{baseRef}..{b}" : b;
            var log = await git.RunAsync(repo, ct, "log", range, "--date=iso-strict", $"--pretty=format:{Rs}%H{Us}%an{Us}%ad{Us}%s", "--numstat", "-n", "300");
            foreach (var c in ParseLog(log.StdOut)) if (seen.Add(c.Sha)) res.Commits.Add(new TicketCommit(c.Sha, c.Author, c.Date, c.Subject, c.Added, c.Removed, c.Files.Count));
        }
        res.Commits.Sort((a, b) => a.Date.CompareTo(b.Date));
        // 3) Agents am Ticket mit nicht committeten Änderungen
        foreach (var a in agents ?? [])
        {
            if (!string.Equals(a.Ticket?.Trim(), ticket, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(a.Cwd) || !GitService.IsRepo(a.Cwd)) continue;
            var st = await git.GetStatusAsync(a.Cwd, ct);
            if (st.Changes.Count > 0 && !res.Workspaces.Any(w => string.Equals(w.Dir, a.Cwd, StringComparison.OrdinalIgnoreCase)))
                res.Workspaces.Add(new TicketWorkspace(a.Title, a.Cwd, a.Id));
        }
        if (res.Commits.Count > 0) res.FileCount = (await NetDiffAsync(repo, res, ct)).Count;
        return res;
    }

    public async Task<string?> DefaultBranchAsync(string repo, CancellationToken ct = default)
    {
        var head = (await git.RunAsync(repo, ct, "symbolic-ref", "--short", "refs/remotes/origin/HEAD")).StdOut.Trim();
        if (head.Length > 0) return head;
        foreach (var b in new[] { "origin/main", "origin/master", "main", "master", "develop" })
            if ((await git.RunAsync(repo, ct, "rev-parse", "--verify", "--quiet", b)).ExitCode == 0) return b;
        return null;
    }

    /// <summary>
    /// Gesamtänderung des Tickets je Datei: alle Ticket-Commits nacheinander zu einem Patch zusammengefasst
    /// (pro Datei: Stand vor dem ersten bis nach dem letzten Ticket-Commit, der die Datei berührt).
    /// </summary>
    public async Task<List<FileDiff>> NetDiffAsync(string repo, TicketChanges t, CancellationToken ct = default)
    {
        if (t.Commits.Count == 0) return [];
        // Dateien je Commit
        var touched = new Dictionary<string, (string First, string Last)>(StringComparer.Ordinal);
        foreach (var c in t.Commits)
        {
            var names = (await git.RunAsync(repo, ct, "show", "--format=", "--name-only", "--no-renames", c.Sha)).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var n in names) touched[n] = touched.TryGetValue(n, out var e) ? (e.First, c.Sha) : (c.Sha, c.Sha);
        }
        var result = new List<FileDiff>();
        foreach (var grp in touched.GroupBy(x => x.Value))
        {
            var (first, last) = grp.Key;
            var parent = (await git.RunAsync(repo, ct, "rev-parse", "--verify", "--quiet", first + "^")).ExitCode == 0 ? first + "^" : EmptyTree;
            var args = new List<string> { "diff", "--no-ext-diff", "-M", parent, last, "--" };
            args.AddRange(grp.Select(x => x.Key));
            result.AddRange(DiffParser.Parse((await git.RunAsync(repo, ct, [.. args])).StdOut));
        }
        return [.. result.GroupBy(f => f.Path).Select(g => g.First()).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public async Task<List<FileDiff>> CommitDiffAsync(string repo, string sha, CancellationToken ct = default) =>
        DiffParser.Parse((await git.RunAsync(repo, ct, "show", "--format=", "--no-ext-diff", "-M", sha)).StdOut);

    /// <summary>Nicht committete Änderungen (inkl. neuer Dateien) in einem Arbeitsordner.</summary>
    public async Task<List<FileDiff>> WorkspaceDiffAsync(string dir, CancellationToken ct = default)
    {
        var files = DiffParser.Parse((await git.RunAsync(dir, ct, "diff", "HEAD", "--no-ext-diff", "-M")).StdOut);
        var st = await git.GetStatusAsync(dir, ct);
        foreach (var c in st.Changes.Where(c => c.Status == "??").Take(50))
            files.AddRange(DiffParser.Parse(await git.GetDiffAsync(dir, c.Path, ct: ct)));
        return [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }
}
