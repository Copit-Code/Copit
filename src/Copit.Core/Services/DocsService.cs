using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Copit.Core.Models;

namespace Copit.Core.Services;

/// <summary>Einstellungen der Doku pro Projekt.</summary>
public class DocsProjectSettings
{
    public bool Architecture { get; set; } = true;
    public bool History { get; set; } = true;
    public bool Knowledge { get; set; } = true;
    /// <summary>Zeitraum für Commits/Tickets im Verlauf.</summary>
    public int HistoryDays { get; set; } = 180;
    /// <summary>Jira-Worklogs (alle Personen) in die Ticket-Seiten aufnehmen.</summary>
    public bool IncludeWorklogs { get; set; } = true;
    public string Folder { get; set; } = "docs";
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";
}

public class DocsData { public Dictionary<string, DocsProjectSettings> Projects { get; set; } = []; }

public record DocsResult(int Pages, int Tickets, int Modules, int Commits, List<string> Warnings, string IndexPath);

// ---------------------------------------------------------------- Repo-Analyse

public class ModuleInfo
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    /// <summary>Pfad relativ zum Repo, mit '/'.</summary>
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Language { get; set; } = "";
    public List<SourceFile> Files { get; set; } = [];
    public HashSet<string> DependsOn { get; set; } = [];
    public List<string> Packages { get; set; } = [];
}

public record SourceFile(string Path, List<string> Types, string? Namespace, int Lines);

public record CommitInfo(string Sha, string Author, DateTime Date, string Subject, List<string> Files, List<string> Keys);

/// <summary>
/// Vernetzte Projekt-Doku als Markdown unter /docs im Repo:
/// Code &amp; Architektur (Module, Abhängigkeiten, Typen), Arbeit &amp; Verlauf (Tickets ↔ Commits ↔ MRs ↔ Agent-Sessions ↔ Zeitbuchungen)
/// und Projektwissen (Konventionen, Zuordnungen, Rückfragen-Wissen, Notizen, Entscheidungen).
/// Generierte Blöcke stehen zwischen Markern – alles außerhalb (z. B. vom Doku-Agent oder von Hand) bleibt erhalten.
/// </summary>
public partial class DocsService(DataService data, ConnectionHub hub, GitService git, SessionStore sessions, SettingsService settings)
{
    public const string GenStart = "<!-- copit:generated:start – dieser Block wird von Copit neu erzeugt -->";
    public const string GenEnd = "<!-- copit:generated:end -->";
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static readonly HashSet<string> IgnoreDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "dist", "dist-test", "out", "build", ".vs", ".idea", ".vscode", "artifacts",
        "packages", "coverage", ".copit", "TestResults", "vendor", "__pycache__", ".venv", "venv", "target", ".next", ".claude",
    };
    private static readonly HashSet<string> SourceExt = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".go", ".php", ".java", ".kt", ".xaml", ".vue", ".svelte", ".rs", ".rb", ".sql" };
    private static readonly string[] Markers = ["*.csproj", "*.fsproj", "package.json", "pyproject.toml", "go.mod", "composer.json", "pom.xml", "build.gradle", "Cargo.toml"];

    public DocsProjectSettings For(CopitProject p)
    {
        if (!data.Docs.Value.Projects.TryGetValue(p.Id, out var s)) data.Docs.Value.Projects[p.Id] = s = new DocsProjectSettings();
        return s;
    }

    // ================================================================ Analyse

    public static string Slug(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant().Replace('.', '-'), @"[^a-z0-9\-]+", "-"), "-{2,}", "-").Trim('-') is { Length: > 0 } x ? x : "modul";

    private static string Rel(string root, string full) => System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');

    private static IEnumerable<string> Walk(string dir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir).ToList(); } catch { yield break; }
        foreach (var f in files) yield return f;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch { yield break; }
        foreach (var d in subs)
        {
            var name = System.IO.Path.GetFileName(d);
            if (IgnoreDirs.Contains(name) || name.StartsWith('.')) continue;
            foreach (var f in Walk(d)) yield return f;
        }
    }

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+(?:(?:sealed|static|abstract|partial|readonly|ref)\s+)*(class|record|interface|enum|struct)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex CsType();
    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex CsNamespace();
    [GeneratedRegex(@"^\s*export\s+(?:default\s+)?(?:abstract\s+)?(?:class|function|interface|type|const|enum|async\s+function)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex TsExport();
    [GeneratedRegex(@"^(?:class|def)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex PyType();
    [GeneratedRegex(@"^\s*(?:abstract\s+|final\s+)?(?:class|interface|trait|enum)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex PhpType();
    [GeneratedRegex(@"(?:import|export)[^'""]*?from\s*['""](\.{1,2}/[^'""]+)['""]|require\(\s*['""](\.{1,2}/[^'""]+)['""]\s*\)")]
    private static partial Regex TsImport();
    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsProjRef();
    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsPkgRef();

    /// <summary>Module (Projektdateien) mit Dateien, Typen und Abhängigkeiten.</summary>
    public static List<ModuleInfo> Scan(string repo)
    {
        var all = Walk(repo).ToList();
        var markerDirs = all.Where(f => Markers.Any(m => m.StartsWith("*.") ? f.EndsWith(m[1..], StringComparison.OrdinalIgnoreCase) : System.IO.Path.GetFileName(f).Equals(m, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(f => System.IO.Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // Ohne Projektdateien: Ordner der ersten Ebene als Module
        if (markerDirs.Count == 0)
            foreach (var d in Directory.EnumerateDirectories(repo).Where(d => !IgnoreDirs.Contains(System.IO.Path.GetFileName(d)) && !System.IO.Path.GetFileName(d).StartsWith('.')))
                markerDirs[d] = [];
        var modules = new List<ModuleInfo>();
        foreach (var (dir, markers) in markerDirs.OrderBy(x => x.Key.Length))
        {
            var rel = Rel(repo, dir);
            var name = markers.FirstOrDefault(m => m.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) is { } proj ? System.IO.Path.GetFileNameWithoutExtension(proj)
                : rel == "." ? System.IO.Path.GetFileName(repo.TrimEnd('\\', '/')) : rel.Split('/').Last();
            if (markers.FirstOrDefault(m => m.EndsWith("package.json")) is { } pj)
                try { if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pj))?["name"]?.ToString() is { Length: > 0 } n) name = n; } catch { }
            var kind = markers.Select(m => System.IO.Path.GetExtension(m) is { Length: > 0 } e && e != ".json" && e != ".toml" && e != ".mod" && e != ".xml" && e != ".gradle" ? e.TrimStart('.') : System.IO.Path.GetFileName(m)).FirstOrDefault() ?? "Ordner";
            var m2 = new ModuleInfo { Name = name, Path = rel == "." ? "" : rel, Kind = kind };
            var slug = Slug(name);
            while (modules.Any(x => x.Slug == slug)) slug += "-2";
            m2.Slug = slug;
            modules.Add(m2);
        }
        // Datei → tiefstes Modul
        ModuleInfo? Owner(string relFile) => modules.Where(m => m.Path == "" || relFile.StartsWith(m.Path + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Path.Length).FirstOrDefault();
        var langCount = new Dictionary<ModuleInfo, Dictionary<string, int>>();
        foreach (var f in all.Where(f => SourceExt.Contains(System.IO.Path.GetExtension(f))))
        {
            var rel = Rel(repo, f);
            var owner = Owner(rel);
            if (owner == null) continue;
            string text;
            try { var fi = new FileInfo(f); if (fi.Length > 600_000) continue; text = File.ReadAllText(f); } catch { continue; }
            var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            var types = ext switch
            {
                ".cs" => CsType().Matches(text).Select(m => m.Groups[2].Value),
                ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" => TsExport().Matches(text).Select(m => m.Groups[1].Value),
                ".py" => PyType().Matches(text).Select(m => m.Groups[1].Value).Where(n => !n.StartsWith('_')),
                ".php" or ".java" or ".kt" => PhpType().Matches(text).Select(m => m.Groups[1].Value),
                _ => [],
            };
            var ns = ext == ".cs" ? CsNamespace().Match(text) is { Success: true } nm ? nm.Groups[1].Value : null : null;
            owner.Files.Add(new SourceFile(rel, types.Distinct().Take(12).ToList(), ns, text.Count(c => c == '\n') + 1));
            if (!langCount.TryGetValue(owner, out var lc)) langCount[owner] = lc = [];
            lc[ext] = lc.GetValueOrDefault(ext) + 1;
            // relative Importe über Modulgrenzen
            if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs")
                foreach (Match im in TsImport().Matches(text))
                {
                    var target = im.Groups[1].Success && im.Groups[1].Value.Length > 0 ? im.Groups[1].Value : im.Groups[2].Value;
                    var resolved = Rel(repo, System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f)!, target)));
                    if (Owner(resolved) is { } other && other != owner) owner.DependsOn.Add(other.Slug);
                }
        }
        foreach (var (m, lc) in langCount)
            m.Language = lc.OrderByDescending(x => x.Value).First().Key.TrimStart('.') switch { "cs" => "C#", "ts" or "tsx" => "TypeScript", "js" or "mjs" or "jsx" => "JavaScript", "py" => "Python", "php" => "PHP", "go" => "Go", "java" => "Java", "kt" => "Kotlin", "xaml" => "XAML", var x => x };
        // Projekt-/Paketabhängigkeiten
        foreach (var (dir, markers) in markerDirs)
        {
            var m = modules.First(x => x.Path == (Rel(repo, dir) == "." ? "" : Rel(repo, dir)));
            foreach (var mk in markers)
            {
                string text;
                try { text = File.ReadAllText(mk); } catch { continue; }
                if (mk.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match r in CsProjRef().Matches(text))
                    {
                        var target = Rel(repo, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, r.Groups[1].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar))))!);
                        if (modules.FirstOrDefault(x => x.Path.Equals(target, StringComparison.OrdinalIgnoreCase)) is { } t && t != m) m.DependsOn.Add(t.Slug);
                    }
                    m.Packages.AddRange(CsPkgRef().Matches(text).Select(x => x.Groups[1].Value));
                }
                else if (mk.EndsWith("package.json"))
                {
                    try
                    {
                        var j = System.Text.Json.Nodes.JsonNode.Parse(text);
                        foreach (var key in new[] { "dependencies", "devDependencies" })
                            foreach (var (dep, _) in j?[key]?.AsObject() ?? [])
                            {
                                if (modules.FirstOrDefault(x => x.Name == dep) is { } t && t != m) m.DependsOn.Add(t.Slug);
                                else if (key == "dependencies") m.Packages.Add(dep);
                            }
                    }
                    catch { }
                }
            }
        }
        // Gleichnamige Module (z. B. "Copit" und "copit") über den Pfad unterscheiden
        foreach (var g in modules.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var mm in g) mm.Name = $"{mm.Name} ({(mm.Path == "" ? "/" : mm.Path)})";
        return modules.Where(m => m.Files.Count > 0 || m.DependsOn.Count > 0).ToList();
    }

    /// <summary>Commits (alle Branches) mit Dateien und Ticket-Keys.</summary>
    public async Task<List<CommitInfo>> CommitsAsync(string repo, int days, CancellationToken ct)
    {
        var r = await git.RunAsync(repo, ct, "log", "--all", $"--since={days}.days", "--date=iso-strict", "--pretty=format:\u001e%H\u001f%an\u001f%ad\u001f%s", "--name-only", "-n", "3000");
        var outp = new List<CommitInfo>();
        foreach (var block in r.StdOut.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;
            var head = lines[0].Split('\u001f');
            if (head.Length < 4) continue;
            DateTime.TryParse(head[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
            var keys = TimeBookingService.IssueKeyRegex().Matches(head[3]).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            outp.Add(new CommitInfo(head[0], head[1], date, head[3], lines.Skip(1).ToList(), keys));
        }
        return outp;
    }

    // ================================================================ Schreiben

    /// <summary>Ersetzt nur den generierten Block; Rest der Datei bleibt erhalten. Neue Datei: Block + Platzhalter.</summary>
    public static string Merge(string? existing, string generated, string? tail = null)
    {
        var block = $"{GenStart}\n{generated.TrimEnd()}\n{GenEnd}";
        if (string.IsNullOrEmpty(existing)) return block + "\n" + (tail ?? "");
        var a = existing.IndexOf("<!-- copit:generated:start", StringComparison.Ordinal);
        var b = existing.IndexOf(GenEnd, StringComparison.Ordinal);
        if (a >= 0 && b > a) return existing[..a] + block + existing[(b + GenEnd.Length)..];
        return block + "\n\n" + existing;
    }

    private int _written;

    private void Write(string root, string relPath, string generated, string? tail = null)
    {
        var path = System.IO.Path.Combine(root, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        var merged = Merge(old, generated, tail);
        if (merged != old) { File.WriteAllText(path, merged, new UTF8Encoding(false)); _written++; }
    }

    /// <summary>Relativer Link von einer Doku-Seite zu einem Pfad im Repo.</summary>
    public static string Link(string fromDocRel, string toRepoRel)
    {
        var fromDir = System.IO.Path.GetDirectoryName(fromDocRel.Replace('/', System.IO.Path.DirectorySeparatorChar)) ?? "";
        var rel = System.IO.Path.GetRelativePath(string.IsNullOrEmpty(fromDir) ? "." : fromDir, toRepoRel.Replace('/', System.IO.Path.DirectorySeparatorChar)).Replace('\\', '/');
        return Uri.EscapeDataString(rel).Replace("%2F", "/").Replace("%2E", ".").Replace("%2D", "-").Replace("%5F", "_");
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
    private static string Short(string s, int n) => s.Length > n ? s[..n].TrimEnd() + "…" : s;
    private static string H(double hours) => hours.ToString("0.##", De) + " h";

    /// <summary>Web-URL zu einem Commit (GitLab/GitHub/Bitbucket) aus der Remote-URL.</summary>
    public static string? CommitUrl(string? remote, string sha)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var path = GitLabClient.ProjectPathFromRemote(remote);
        string? host = null;
        var u = remote.Trim();
        if (u.StartsWith("git@")) host = u[4..u.IndexOf(':')];
        else if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) host = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        if (host == null || path == null) return null;
        return host.Contains("github") ? $"https://{host}/{path}/commit/{sha}" : host.Contains("bitbucket") ? $"https://{host}/{path}/commits/{sha}" : $"https://{host}/{path}/-/commit/{sha}";
    }

    /// <summary>Ticket-Keys im Text auf Ticket-Seiten verlinken (nur bekannte).</summary>
    public static string Linkify(string text, string fromDocRel, string folder, ISet<string> known) =>
        TimeBookingService.IssueKeyRegex().Replace(text, m =>
        {
            var k = m.Value.ToUpperInvariant();
            return known.Contains(k) ? $"[{k}]({Link(fromDocRel, $"{folder}/history/tickets/{k}.md")})" : m.Value;
        });

    // ================================================================ Erzeugen

    public async Task<DocsResult> GenerateAsync(CopitProject p, Action<string> progress, CancellationToken ct = default)
    {
        var s = For(p);
        var repo = p.SourceFolder;
        if (string.IsNullOrWhiteSpace(repo) || !Directory.Exists(repo)) throw new InvalidOperationException("Projekt hat keinen gültigen Quellordner.");
        var folder = string.IsNullOrWhiteSpace(s.Folder) ? "docs" : s.Folder.Trim('/', '\\');
        var root = repo;
        _written = 0;
        var warnings = new List<string>();
        var isGit = GitService.IsRepo(repo);
        var remote = isGit ? await git.GetRemoteUrlAsync(repo, ct) : null;

        progress("Code analysieren");
        var modules = s.Architecture ? await Task.Run(() => Scan(repo), ct) : [];
        ModuleInfo? ModuleOf(string file) => modules.Where(m => m.Path == "" || file.StartsWith(m.Path + "/", StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Path.Length).FirstOrDefault();

        // ---------- Daten für den Verlauf
        progress("Git-Verlauf lesen");
        var commits = isGit && (s.History || s.Architecture) ? await CommitsAsync(repo, s.HistoryDays, ct) : [];
        var agents = sessions.ForProject(p.Id).ToList();
        var keys = new HashSet<string>(commits.SelectMany(c => c.Keys), StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents.Where(a => a.Ticket != null)) keys.Add(a.Ticket!.ToUpperInvariant());
        var branches = isGit ? (await git.RunAsync(repo, ct, "branch", "-a", "--format=%(refname:short)")).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [];
        foreach (var b in branches) foreach (Match m in TimeBookingService.IssueKeyRegex().Matches(b)) keys.Add(m.Value.ToUpperInvariant());

        var issues = new Dictionary<string, JiraIssue>(StringComparer.OrdinalIgnoreCase);
        JiraIssue? epic = null;
        if (s.History && hub.Jira is { } jira)
        {
            progress("Jira-Tickets laden");
            try
            {
                if (!string.IsNullOrWhiteSpace(p.EpicKey))
                {
                    epic = await jira.GetIssueAsync(p.EpicKey, ct);
                    var epicIssues = await jira.GetEpicIssuesAsync(p.EpicKey, ct);
                    // Geteiltes Epic: nur Tickets dieses Projekts (und nicht zugeordnete) in die Doku
                    var scoped = await new TicketScopeService(hub, data, sessions, new TicketChangesService(git)).ScopeAsync(p, epicIssues, ct);
                    foreach (var i in epicIssues.Where(i => scoped.Visible(i.Key))) { issues[i.Key] = i; keys.Add(i.Key); }
                }
                var missing = keys.Where(k => !issues.ContainsKey(k)).ToList();
                for (int i = 0; i < missing.Count; i += 80)
                {
                    var chunk = string.Join(",", missing.Skip(i).Take(80));
                    try { foreach (var x in await jira.SearchAsync($"key in ({chunk})", 100, ct)) issues[x.Key] = x; }
                    catch { foreach (var k in missing.Skip(i).Take(80)) if (await jira.GetIssueAsync(k, ct) is { } x) issues[x.Key] = x; }
                }
            }
            catch (Exception e) { warnings.Add("Jira: " + e.Message); }
        }
        else if (s.History && hub.Jira == null) warnings.Add("Jira nicht konfiguriert – Ticket-Seiten ohne Titel/Status.");
        // Nur echte Tickets (bekannt aus Jira) bzw. ohne Jira alles mit Commits/Sessions
        var tickets = keys.Where(k => issues.ContainsKey(k) || hub.Jira == null).OrderBy(k => k).ToList();
        var known = new HashSet<string>(tickets, StringComparer.OrdinalIgnoreCase);

        // ---------- Arbeit & Verlauf
        var ticketFiles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in tickets) ticketFiles[k] = commits.Where(c => c.Keys.Contains(k)).SelectMany(c => c.Files).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (s.History)
        {
            var gitlabProject = !string.IsNullOrWhiteSpace(p.GitLabProject) ? p.GitLabProject : remote != null ? GitLabClient.ProjectPathFromRemote(remote) : null;
            var n = 0;
            foreach (var k in tickets)
            {
                ct.ThrowIfCancellationRequested();
                progress($"Ticket-Seiten {++n}/{tickets.Count}");
                var rel = $"{folder}/history/tickets/{k}.md";
                var issue = issues.GetValueOrDefault(k);
                var tc = commits.Where(c => c.Keys.Contains(k)).OrderByDescending(c => c.Date).ToList();
                var ta = agents.Where(a => string.Equals(a.Ticket, k, StringComparison.OrdinalIgnoreCase) || (a.Branch?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                var tb = branches.Where(b => b.Contains(k, StringComparison.OrdinalIgnoreCase)).Select(b => b.StartsWith("origin/") ? b[7..] : b).Distinct().ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"# {k}{(issue != null ? " – " + issue.Summary : "")}");
                sb.AppendLine();
                sb.AppendLine($"[← Verlauf]({Link(rel, $"{folder}/history/README.md")}) · [Doku-Start]({Link(rel, $"{folder}/README.md")}){(hub.Jira != null ? $" · [In Jira öffnen]({hub.Jira.IssueUrl(k)})" : "")}");
                sb.AppendLine();
                if (issue != null)
                {
                    sb.AppendLine("| Status | Typ | Zuständig | Account | Schätzung | Gebucht |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    sb.AppendLine($"| {Esc(issue.Status)} | {Esc(issue.IssueType)} | {Esc(issue.Assignee)} | {Esc(issue.Account ?? "–")} | {(issue.OriginalEstimateSeconds > 0 ? H(issue.OriginalEstimateSeconds / 3600.0) : "–")} | {(issue.TimeSpentSeconds > 0 ? H(issue.TimeSpentSeconds / 3600.0) : "–")} |");
                    sb.AppendLine();
                    if (epic != null && !k.Equals(epic.Key, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine($"Epic: [{epic.Key} – {Esc(epic.Summary)}]({Link(rel, $"{folder}/history/README.md")}#epic)\n");
                    if (!string.IsNullOrWhiteSpace(issue.Description))
                        sb.AppendLine("## Beschreibung\n\n" + Short(issue.Description.Trim(), 1200).Replace("\r", "") + "\n");
                }
                // Code-Bezug
                var files = ticketFiles.GetValueOrDefault(k) ?? [];
                if (files.Count > 0)
                {
                    sb.AppendLine("## Betroffener Code\n");
                    foreach (var g in files.Where(f => File.Exists(System.IO.Path.Combine(repo, f))).GroupBy(f => ModuleOf(f)?.Slug ?? "").OrderByDescending(g => g.Count()).Take(12))
                    {
                        var mod = modules.FirstOrDefault(m => m.Slug == g.Key);
                        sb.AppendLine(mod != null ? $"- Modul [{mod.Name}]({Link(rel, $"{folder}/architecture/{mod.Slug}.md")}): " : "- ");
                        foreach (var f in g.OrderBy(x => x).Take(15)) sb.AppendLine($"  - [`{f}`]({Link(rel, f)})");
                    }
                    sb.AppendLine();
                }
                if (tc.Count > 0)
                {
                    sb.AppendLine($"## Commits ({tc.Count})\n");
                    sb.AppendLine("| Datum | Commit | Nachricht | Autor |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var c in tc.Take(40))
                    {
                        var url = CommitUrl(remote, c.Sha);
                        sb.AppendLine($"| {c.Date:dd.MM.yyyy} | {(url != null ? $"[`{c.Sha[..8]}`]({url})" : $"`{c.Sha[..8]}`")} | {Esc(Linkify(c.Subject, rel, folder, known))} | {Esc(c.Author)} |");
                    }
                    sb.AppendLine();
                }
                // Branches & MRs
                if (tb.Count > 0)
                {
                    sb.AppendLine("## Branches & Merge Requests\n");
                    foreach (var b in tb) sb.AppendLine($"- Branch `{b}`");
                    if (hub.GitLab is { } gl && gitlabProject != null)
                    {
                        foreach (var b in tb.Take(4))
                        {
                            try
                            {
                                foreach (var mr in await gl.GetMergeRequestsAsync(gitlabProject, "all", b, ct))
                                    sb.AppendLine($"- MR [!{mr.Iid} {Esc(mr.Title)}]({mr.WebUrl}) · {mr.State} · {mr.SourceBranch} → {mr.TargetBranch}");
                            }
                            catch { }
                        }
                    }
                    sb.AppendLine();
                }
                if (ta.Count > 0)
                {
                    sb.AppendLine("## KI-Agent-Sessions\n");
                    foreach (var a in ta.OrderByDescending(a => a.CreatedAt))
                    {
                        sb.AppendLine($"### {Esc(a.Title)} ({a.CreatedAt:dd.MM.yyyy})\n");
                        sb.AppendLine($"{a.Provider} · Agent-Zeit {TimeSpan.FromMilliseconds(a.AgentMs):h\\:mm} h · {(a.Cost > 0 ? $"${a.Cost:0.00} · " : "")}+{a.LinesAdded}/−{a.LinesRemoved} Zeilen in {a.FilesChanged} Dateien · Status {AgentStatusInfo.Title(a.Status)}\n");
                        if (!string.IsNullOrWhiteSpace(a.Summary)) sb.AppendLine("> " + Linkify(Short(a.Summary!, 600), rel, folder, known).Replace("\n", "\n> ") + "\n");
                    }
                }
                if (s.IncludeWorklogs && hub.Jira is { } j2 && issue != null)
                {
                    try
                    {
                        var wl = await j2.GetIssueWorklogsAsync(k, ct);
                        if (wl.Count > 0)
                        {
                            sb.AppendLine($"## Zeitbuchungen ({H(wl.Sum(w => w.Seconds) / 3600.0)})\n");
                            sb.AppendLine("| Datum | Person | Zeit | Text |");
                            sb.AppendLine("|---|---|---|---|");
                            foreach (var w in wl.OrderByDescending(w => w.Started).Take(60))
                                sb.AppendLine($"| {w.Started:dd.MM.yyyy} | {Esc(w.Author)} | {H(w.Seconds / 3600.0)} | {Esc(Short(w.Comment, 160))} |");
                            sb.AppendLine();
                        }
                    }
                    catch { }
                }
                // Verwandte Tickets (gemeinsame Dateien)
                if (files.Count > 0)
                {
                    var related = ticketFiles.Where(x => !x.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                        .Select(x => (Key: x.Key, Shared: x.Value.Count(files.Contains))).Where(x => x.Shared > 0).OrderByDescending(x => x.Shared).Take(6).ToList();
                    if (related.Count > 0)
                    {
                        sb.AppendLine("## Verwandte Tickets\n");
                        foreach (var r in related)
                            sb.AppendLine($"- [{r.Key}]({Link(rel, $"{folder}/history/tickets/{r.Key}.md")}){(issues.GetValueOrDefault(r.Key) is { } ri ? " – " + Esc(ri.Summary) : "")} · {r.Shared} gemeinsame Dateien");
                        sb.AppendLine();
                    }
                }
                Write(root, rel, sb.ToString(), "\n## Notizen\n\n_Eigene Ergänzungen hier – bleiben beim Aktualisieren erhalten._\n");
            }

            // Verlauf-Übersicht
            var hrel = $"{folder}/history/README.md";
            var hs = new StringBuilder();
            hs.AppendLine("# Arbeit & Verlauf\n");
            hs.AppendLine($"[← Doku-Start]({Link(hrel, $"{folder}/README.md")}) · [Architektur]({Link(hrel, $"{folder}/architecture/README.md")}) · [Projektwissen]({Link(hrel, $"{folder}/knowledge/README.md")})\n");
            if (epic != null) hs.AppendLine($"<a id=\"epic\"></a>**Epic [{epic.Key}]({hub.Jira?.IssueUrl(epic.Key)}):** {Esc(epic.Summary)} · {epic.Status}\n");
            hs.AppendLine($"{tickets.Count} Tickets · {commits.Count} Commits der letzten {s.HistoryDays} Tage · {commits.Count(c => c.Keys.Count == 0)} Commits ohne Ticket-Bezug · {agents.Count} KI-Sessions\n");
            foreach (var g in tickets.GroupBy(k => issues.GetValueOrDefault(k)?.Bucket ?? IssueBucket.Open).OrderBy(g => g.Key switch { IssueBucket.InProgress => 0, IssueBucket.Review => 1, IssueBucket.Open => 2, _ => 3 }))
            {
                hs.AppendLine($"## {g.Key switch { IssueBucket.InProgress => "In Arbeit", IssueBucket.Review => "Review / Test", IssueBucket.Done => "Erledigt", _ => "Offen" }} ({g.Count()})\n");
                hs.AppendLine("| Ticket | Titel | Status | Commits | Letzte Aktivität |");
                hs.AppendLine("|---|---|---|---|---|");
                foreach (var k in g.OrderByDescending(k => commits.Where(c => c.Keys.Contains(k)).Select(c => c.Date).DefaultIfEmpty(issues.GetValueOrDefault(k)?.Updated ?? DateTime.MinValue).Max()))
                {
                    var i = issues.GetValueOrDefault(k);
                    var last = commits.Where(c => c.Keys.Contains(k)).Select(c => c.Date).DefaultIfEmpty(i?.Updated ?? DateTime.MinValue).Max();
                    hs.AppendLine($"| [{k}]({Link(hrel, $"{folder}/history/tickets/{k}.md")}) | {Esc(Short(i?.Summary ?? "", 90))} | {Esc(i?.Status ?? "")} | {commits.Count(c => c.Keys.Contains(k))} | {(last > DateTime.MinValue ? last.ToString("dd.MM.yyyy") : "")} |");
                }
                hs.AppendLine();
            }
            // Zeitleiste nach Monat
            if (commits.Count > 0)
            {
                hs.AppendLine("## Zeitleiste\n");
                foreach (var mg in commits.GroupBy(c => new DateTime(c.Date.Year, c.Date.Month, 1)).OrderByDescending(g => g.Key).Take(12))
                {
                    var ks = mg.SelectMany(c => c.Keys).Where(known.Contains).Distinct().ToList();
                    hs.AppendLine($"- **{mg.Key.ToString("MMMM yyyy", De)}** – {mg.Count()} Commits{(ks.Count > 0 ? ": " + string.Join(", ", ks.Take(20).Select(k => $"[{k}]({Link(hrel, $"{folder}/history/tickets/{k}.md")})")) : "")}");
                }
                hs.AppendLine();
            }
            Write(root, hrel, hs.ToString());
        }

        // ---------- Code & Architektur
        if (s.Architecture && modules.Count > 0)
        {
            progress($"Architektur-Seiten ({modules.Count} Module)");
            var arel = $"{folder}/architecture/README.md";
            var asb = new StringBuilder();
            asb.AppendLine("# Code & Architektur\n");
            asb.AppendLine($"[← Doku-Start]({Link(arel, $"{folder}/README.md")}) · [Verlauf]({Link(arel, $"{folder}/history/README.md")}) · [Projektwissen]({Link(arel, $"{folder}/knowledge/README.md")})\n");
            asb.AppendLine("## Module\n");
            asb.AppendLine("| Modul | Pfad | Sprache | Dateien | Zeilen | nutzt |");
            asb.AppendLine("|---|---|---|---|---|---|");
            foreach (var m in modules.OrderBy(m => m.Path))
                asb.AppendLine($"| [{Esc(m.Name)}]({Link(arel, $"{folder}/architecture/{m.Slug}.md")}) | [`{(m.Path == "" ? "/" : m.Path)}`]({Link(arel, m.Path == "" ? "." : m.Path)}) | {m.Language} | {m.Files.Count} | {m.Files.Sum(f => f.Lines):N0} | {string.Join(", ", m.DependsOn.Select(d => modules.FirstOrDefault(x => x.Slug == d)?.Name ?? d))} |");
            asb.AppendLine();
            var edges = modules.SelectMany(m => m.DependsOn.Select(d => (m.Slug, d))).ToList();
            if (edges.Count > 0)
            {
                asb.AppendLine("## Abhängigkeiten\n");
                asb.AppendLine("```mermaid\ngraph LR");
                foreach (var m in modules) asb.AppendLine($"  {m.Slug.Replace('-', '_')}[\"{m.Name.Replace("\"", "'")}\"]");
                foreach (var (a, b) in edges) asb.AppendLine($"  {a.Replace('-', '_')} --> {b.Replace('-', '_')}");
                asb.AppendLine("```\n");
            }
            Write(root, arel, asb.ToString(), "\n## Überblick\n\n_Architektur-Überblick – wird vom Doku-Agent ergänzt oder von Hand gepflegt._\n");

            foreach (var m in modules)
            {
                var rel = $"{folder}/architecture/{m.Slug}.md";
                var sb = new StringBuilder();
                sb.AppendLine($"# {m.Name}\n");
                sb.AppendLine($"[← Architektur]({Link(rel, arel)}) · Pfad [`{(m.Path == "" ? "/" : m.Path)}`]({Link(rel, m.Path == "" ? "." : m.Path)}) · {m.Kind} · {m.Language} · {m.Files.Count} Dateien · {m.Files.Sum(f => f.Lines):N0} Zeilen\n");
                var usedBy = modules.Where(x => x.DependsOn.Contains(m.Slug)).ToList();
                if (m.DependsOn.Count > 0 || usedBy.Count > 0 || m.Packages.Count > 0)
                {
                    sb.AppendLine("## Abhängigkeiten\n");
                    if (m.DependsOn.Count > 0) sb.AppendLine("- **nutzt:** " + string.Join(", ", m.DependsOn.Select(d => modules.First(x => x.Slug == d)).Select(x => $"[{x.Name}]({x.Slug}.md)")));
                    if (usedBy.Count > 0) sb.AppendLine("- **genutzt von:** " + string.Join(", ", usedBy.Select(x => $"[{x.Name}]({x.Slug}.md)")));
                    if (m.Packages.Count > 0) sb.AppendLine("- **Pakete:** " + string.Join(", ", m.Packages.Distinct().Take(25).Select(x => $"`{x}`")));
                    sb.AppendLine();
                }
                var nss = m.Files.Where(f => f.Namespace != null).Select(f => f.Namespace!).Distinct().OrderBy(x => x).ToList();
                if (nss.Count > 0) sb.AppendLine("**Namespaces:** " + string.Join(", ", nss.Take(20).Select(x => $"`{x}`")) + "\n");
                sb.AppendLine("## Dateien\n");
                sb.AppendLine("| Datei | Typen / Exporte | Zeilen |");
                sb.AppendLine("|---|---|---|");
                foreach (var f in m.Files.OrderBy(f => f.Path).Take(250))
                {
                    var shown = m.Path == "" ? f.Path : f.Path[(m.Path.Length + 1)..];
                    sb.AppendLine($"| [`{shown}`]({Link(rel, f.Path)}) | {string.Join(", ", f.Types.Select(t => $"`{t}`"))} | {f.Lines} |");
                }
                if (m.Files.Count > 250) sb.AppendLine($"| … {m.Files.Count - 250} weitere | | |");
                sb.AppendLine();
                // Tickets, die dieses Modul berührt haben
                var touching = ticketFiles.Where(t => t.Value.Any(f => ModuleOf(f) == m)).Select(t => t.Key).ToList();
                var recent = commits.Where(c => c.Files.Any(f => ModuleOf(f) == m)).OrderByDescending(c => c.Date).ToList();
                if (touching.Count > 0 || recent.Count > 0)
                {
                    sb.AppendLine("## Änderungen\n");
                    if (touching.Count > 0)
                        sb.AppendLine("**Tickets:** " + string.Join(", ", touching.OrderByDescending(k => recent.Where(c => c.Keys.Contains(k)).Select(c => c.Date).DefaultIfEmpty().Max()).Take(30)
                            .Select(k => s.History ? $"[{k}]({Link(rel, $"{folder}/history/tickets/{k}.md")})" : k)) + "\n");
                    foreach (var c in recent.Take(8))
                    {
                        var url = CommitUrl(remote, c.Sha);
                        sb.AppendLine($"- {c.Date:dd.MM.yyyy} {(url != null ? $"[`{c.Sha[..8]}`]({url})" : $"`{c.Sha[..8]}`")} {Esc(s.History ? Linkify(c.Subject, rel, folder, known) : c.Subject)} – {Esc(c.Author)}");
                    }
                    sb.AppendLine();
                }
                Write(root, rel, sb.ToString(), "\n## Zweck\n\n_Wofür ist dieses Modul da? – wird vom Doku-Agent ergänzt oder von Hand gepflegt._\n\n## Wichtige Abläufe\n\n_–_\n");
            }
        }

        // ---------- Projektwissen
        if (s.Knowledge)
        {
            progress("Projektwissen");
            var krel = $"{folder}/knowledge/README.md";
            var ks = new StringBuilder();
            ks.AppendLine("# Projektwissen\n");
            ks.AppendLine($"[← Doku-Start]({Link(krel, $"{folder}/README.md")}) · [Architektur]({Link(krel, $"{folder}/architecture/README.md")}) · [Verlauf]({Link(krel, $"{folder}/history/README.md")})\n");
            if (!string.IsNullOrWhiteSpace(p.Instructions))
                ks.AppendLine("## Konventionen & Anweisungen\n\n" + Linkify(p.Instructions.Trim(), krel, folder, known) + "\n");
            var repoName = System.IO.Path.GetFileName(repo.TrimEnd('\\', '/'));
            var projKey = p.JiraProjectKey;
            bool Relevant(string text) => (projKey.Length > 0 && text.Contains(projKey + "-", StringComparison.OrdinalIgnoreCase))
                                          || text.Contains(repoName, StringComparison.OrdinalIgnoreCase) || (p.Name.Length > 2 && text.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                                          || (!string.IsNullOrEmpty(p.EpicKey) && text.Contains(p.EpicKey, StringComparison.OrdinalIgnoreCase));
            var topics = data.TimeMemory.Value.Topics.Where(t => Relevant($"{t.Topic} {t.Repo} {t.Ticket}")).ToList();
            if (topics.Count > 0)
            {
                ks.AppendLine("## Zuordnungen Thema → Ticket\n");
                ks.AppendLine("Gelernt aus der Zeitbuchung: Arbeit zu diesen Themen wird auf das jeweilige Ticket gebucht.\n");
                ks.AppendLine("| Thema | Repo | Ticket | |");
                ks.AppendLine("|---|---|---|---|");
                foreach (var t in topics.OrderByDescending(t => t.Manual).ThenByDescending(t => t.Hits))
                    ks.AppendLine($"| {Esc(t.Topic)} | {Esc(t.Repo ?? "")} | {Linkify(t.Ticket, krel, folder, known)} | {(t.Manual ? "fest" : $"{t.Hits}× gebucht")} |");
                ks.AppendLine();
            }
            var facts = data.TimeMemory.Value.Facts.Where(f => Relevant($"{f.Question} {f.Answer}")).ToList();
            if (facts.Count > 0)
            {
                ks.AppendLine("## Antworten auf Rückfragen\n");
                foreach (var f in facts) ks.AppendLine($"- **{Esc(f.Question)}** → {Linkify(f.Answer, krel, folder, known)}");
                ks.AppendLine();
            }
            var rules = new PermissionService().All([repo]).Where(r => r.Enabled && (r.Repo != null || r.Source == RuleSource.Copit)).ToList();
            if (rules.Count > 0)
            {
                ks.AppendLine("## Agent-Berechtigungen\n");
                foreach (var g in rules.GroupBy(r => r.Level).OrderBy(g => g.Key))
                    ks.AppendLine($"- **{g.Key}:** " + string.Join(", ", g.Select(r => $"`{r.Pattern}`").Distinct().Take(30)));
                ks.AppendLine();
            }
            var notes = data.NotesFor(p.Id).ToList();
            if (notes.Count > 0)
            {
                var nrel = $"{folder}/knowledge/notes.md";
                var ns = new StringBuilder();
                ns.AppendLine("# Notizen\n");
                ns.AppendLine($"[← Projektwissen]({Link(nrel, krel)})\n");
                foreach (var g in notes.GroupBy(n => n.Date).OrderByDescending(g => g.Key))
                {
                    ns.AppendLine($"## {g.Key.ToDateTime(TimeOnly.MinValue).ToString("dddd, dd.MM.yyyy", De)}\n");
                    foreach (var n in g) ns.AppendLine($"- {(n.Pinned ? "**[angeheftet]** " : "")}{Linkify(n.Text.Replace("\n", " "), nrel, folder, known)}");
                    ns.AppendLine();
                }
                Write(root, nrel, ns.ToString());
                ks.AppendLine($"## Notizen\n\n[{notes.Count} Projekt-Notizen]({Link(krel, nrel)}), neueste vom {notes.Max(n => n.Date):dd.MM.yyyy}.\n");
            }
            var decDir = System.IO.Path.Combine(root, folder, "knowledge", "decisions");
            var decisions = Directory.Exists(decDir) ? Directory.EnumerateFiles(decDir, "*.md").Where(f => !f.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList() : [];
            ks.AppendLine("## Entscheidungen\n");
            if (decisions.Count == 0) ks.AppendLine("_Noch keine. Der Doku-Agent legt hier Entscheidungen (ADR) aus Agent-Sessions und Notizen an._\n");
            foreach (var d in decisions)
            {
                var title = File.ReadLines(d).FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? System.IO.Path.GetFileNameWithoutExtension(d);
                ks.AppendLine($"- [{Esc(title)}]({Link(krel, Rel(root, d))})");
            }
            ks.AppendLine();
            Write(root, krel, ks.ToString());
        }

        // ---------- Start-Seite
        var irel = $"{folder}/README.md";
        var ix = new StringBuilder();
        ix.AppendLine($"# {p.Name} – Projektdoku\n");
        ix.AppendLine($"Automatisch von Copit erzeugt und verlinkt · Stand {DateTime.Now:dd.MM.yyyy HH:mm}{(!string.IsNullOrEmpty(p.EpicKey) ? $" · Epic {p.EpicKey}" : "")}\n");
        if (s.Architecture) ix.AppendLine($"- **[Code & Architektur]({Link(irel, $"{folder}/architecture/README.md")})** – {modules.Count} Module mit Abhängigkeiten, Dateien und Typen");
        if (s.History) ix.AppendLine($"- **[Arbeit & Verlauf]({Link(irel, $"{folder}/history/README.md")})** – {tickets.Count} Tickets ↔ {commits.Count} Commits ↔ MRs ↔ {agents.Count} KI-Sessions ↔ Zeitbuchungen");
        if (s.Knowledge) ix.AppendLine($"- **[Projektwissen]({Link(irel, $"{folder}/knowledge/README.md")})** – Konventionen, Zuordnungen, Rückfragen-Wissen, Notizen, Entscheidungen");
        ix.AppendLine();
        var active = tickets.Where(k => issues.GetValueOrDefault(k)?.Bucket is IssueBucket.InProgress or IssueBucket.Review).Take(10).ToList();
        if (active.Count > 0)
        {
            ix.AppendLine("## Gerade in Arbeit\n");
            foreach (var k in active) ix.AppendLine($"- [{k}]({Link(irel, $"{folder}/history/tickets/{k}.md")}) – {Esc(issues[k].Summary)} ({issues[k].Status})");
            ix.AppendLine();
        }
        Write(root, irel, ix.ToString(), "\n## Über dieses Projekt\n\n_Kurzbeschreibung – bleibt beim Aktualisieren erhalten._\n");

        s.LastRun = DateTime.Now;
        s.LastResult = $"{_written} Seiten aktualisiert · {modules.Count} Module · {tickets.Count} Tickets · {commits.Count} Commits";
        await data.Docs.SaveAsync();
        return new DocsResult(_written, tickets.Count, modules.Count, commits.Count, warnings, System.IO.Path.Combine(root, folder, "README.md"));
    }

    // ================================================================ Doku-Agent

    /// <summary>Auftrag für den Doku-Agent: Texte außerhalb der generierten Blöcke ergänzen, Entscheidungen festhalten.</summary>
    public string AgentPrompt(CopitProject p)
    {
        var s = For(p);
        var folder = string.IsNullOrWhiteSpace(s.Folder) ? "docs" : s.Folder.Trim('/', '\\');
        var recent = sessions.ForProject(p.Id).Where(a => !string.IsNullOrWhiteSpace(a.Summary) && !a.Title.StartsWith("Doku")).OrderByDescending(a => a.UpdatedAt).Take(15).ToList();
        var notes = data.NotesFor(p.Id).Take(30).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Du pflegst die verlinkte Projektdoku unter `{folder}/` in diesem Repository. Copit hat gerade die Fakten (Module, Dateien, Tickets, Commits) erzeugt.");
        sb.AppendLine();
        sb.AppendLine("REGELN:");
        sb.AppendLine($"- Ändere AUSSCHLIESSLICH Dateien unter `{folder}/`. Keinen Code anfassen.");
        sb.AppendLine("- Blöcke zwischen `<!-- copit:generated:start` und `<!-- copit:generated:end -->` NICHT verändern – sie werden neu erzeugt.");
        sb.AppendLine("- Alles außerhalb dieser Blöcke darfst du schreiben und verbessern; vorhandene manuelle Inhalte erhalten und nur ergänzen.");
        sb.AppendLine("- Nur relative Markdown-Links, die wirklich existieren (auf Doku-Seiten und Quelldateien). Ticket-Keys auf ihre Seite unter history/tickets/ verlinken, wenn sie existiert.");
        sb.AppendLine("- Deutsch, sachlich, knapp. Keine Emojis.");
        sb.AppendLine();
        sb.AppendLine("AUFGABEN:");
        sb.AppendLine($"1. `{folder}/architecture/*.md`: Ersetze die Platzhalter unter „Zweck“ und „Wichtige Abläufe“ durch eine echte Beschreibung (lies dazu den Code des Moduls). Verlinke zentrale Dateien und andere Module.");
        sb.AppendLine($"2. `{folder}/architecture/README.md`: Schreibe unter „Überblick“ eine Architektur-Zusammenfassung: Schichten, Datenfluss, wichtigste Einstiegspunkte – mit Links auf Modul-Seiten.");
        sb.AppendLine($"3. `{folder}/knowledge/decisions/`: Lege für jede wichtige technische Entscheidung aus den Sessions/Notizen unten eine Datei `JJJJ-MM-TT-kurzer-titel.md` an (Format: # Titel, Kontext, Entscheidung, Konsequenzen, Links auf Tickets/Module). Bestehende nicht duplizieren.");
        sb.AppendLine($"4. `{folder}/README.md`: Ergänze unter „Über dieses Projekt“ 3–6 Sätze, was das Projekt tut, falls dort noch der Platzhalter steht.");
        sb.AppendLine("Am Ende: kurze Liste der geänderten Seiten.");
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LETZTE AGENT-SESSIONS (Zusammenfassungen):");
            foreach (var a in recent) sb.AppendLine($"- [{a.UpdatedAt:dd.MM.yyyy}]{(a.Ticket != null ? " " + a.Ticket : "")} {a.Title}: {Short(a.Summary!.Replace("\n", " "), 400)}");
        }
        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("PROJEKT-NOTIZEN:");
            foreach (var n in notes) sb.AppendLine($"- [{n.Date:dd.MM.yyyy}] {Short(n.Text.Replace("\n", " "), 300)}");
        }
        return sb.ToString();
    }

    /// <summary>Erlaubte Tools für den Doku-Agent: lesen überall, schreiben nur unter docs/.</summary>
    public static List<string> AgentTools(string folder) =>
        ["Read", "Grep", "Glob", $"Edit({folder}/**)", $"Write({folder}/**)", $"MultiEdit({folder}/**)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git diff:*)"];
}
