using System.Text.Json.Nodes;
using Copit.Core.Storage;

namespace Copit.Core.Services;

public enum PermissionLevel { Deny, Ask, Allow }

/// <summary>Woher eine Regel stammt: Claude global (~/.claude), Projektdatei (.claude im Repo) oder Copit (per CLI-Flag).</summary>
public enum RuleSource { Global, Project, Copit }

public class PermissionRule
{
    public string Pattern { get; set; } = "";
    public PermissionLevel Level { get; set; }
    public RuleSource Source { get; set; }
    public string? Repo { get; set; }
    public bool Enabled { get; set; } = true;
    public string Id => $"{Source}|{Repo}|{Pattern}";

    public (string Title, string Tool) Describe()
    {
        var p = Pattern.IndexOf('(');
        if (p < 0 || !Pattern.EndsWith(')')) return (Pattern, Pattern);
        var inner = Pattern[(p + 1)..^1];
        return (inner.EndsWith(":*") ? inner[..^2] + " *" : inner, Pattern[..p]);
    }
}

public record PermissionOp(string Id, string Label, string Group, string[] Patterns, bool Risky = false);

public class PermissionData { public List<PermissionRule> Copit { get; set; } = []; public List<PermissionRule> Paused { get; set; } = []; public bool Seeded { get; set; } }

/// <summary>Permission-Regeln: liest/schreibt Claude-settings.json (global + Projekt) und eigene Copit-Regeln (CLI-Flags).</summary>
public class PermissionService
{
    public static readonly PermissionOp[] Ops =
    [
        new("git.read", "status / diff / log", "Git", ["Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git branch:*)"]),
        new("git.branch", "Branch erstellen / wechseln", "Git", ["Bash(git checkout:*)", "Bash(git switch:*)"]),
        new("git.add", "add / stage", "Git", ["Bash(git add:*)", "Bash(git restore:*)"]),
        new("git.commit", "commit", "Git", ["Bash(git commit:*)"]),
        new("git.push", "push", "Git", ["Bash(git push:*)"]),
        new("git.forcepush", "force push", "Git", ["Bash(git push --force:*)", "Bash(git push -f:*)"], true),
        new("git.merge", "merge / rebase", "Git", ["Bash(git merge:*)", "Bash(git rebase:*)"]),
        new("git.reset", "reset / clean", "Git", ["Bash(git reset:*)", "Bash(git clean:*)"], true),
        new("git.tag", "tag", "Git", ["Bash(git tag:*)"]),
        new("git.pull", "fetch / pull", "Git", ["Bash(git fetch:*)", "Bash(git pull:*)"]),
        new("files.read", "Dateien lesen", "Dateien", ["Read", "Grep", "Glob"]),
        new("files.write", "Dateien ändern", "Dateien", ["Edit", "Write", "MultiEdit", "NotebookEdit"]),
        new("shell.build", "Build / Tests", "Shell", ["Bash(npm:*)", "Bash(dotnet:*)", "Bash(composer:*)", "Bash(php:*)", "Bash(yarn:*)", "Bash(pnpm:*)"]),
        new("shell.any", "Beliebige Shell", "Shell", ["Bash"], true),
        new("net.fetch", "Web abrufen", "Netz", ["WebFetch", "WebSearch"]),
    ];

    private readonly JsonStore<PermissionData> _store = new(AppPaths.File("permissions.json"));

    public PermissionService()
    {
        if (!_store.Value.Seeded)
        {
            var r = new List<PermissionRule>();
            foreach (var (op, l) in new[] { ("git.read", PermissionLevel.Allow), ("files.read", PermissionLevel.Allow), ("files.write", PermissionLevel.Allow),
                         ("shell.build", PermissionLevel.Allow), ("git.branch", PermissionLevel.Allow), ("git.add", PermissionLevel.Allow), ("git.pull", PermissionLevel.Allow),
                         ("git.forcepush", PermissionLevel.Deny), ("git.reset", PermissionLevel.Deny) })
                r = SetMatrix(r, op, l, null);
            _store.Value.Copit = r;
            _store.Value.Seeded = true;
            _store.Save();
        }
    }

    public static string GlobalFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    public static string ProjectFile(string repo) => Path.Combine(repo, ".claude", "settings.json");

    private static JsonObject ReadJson(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []; } catch { return []; }
    }

    public static List<PermissionRule> ReadClaude(string file, RuleSource src, string? repo)
    {
        var p = ReadJson(file)["permissions"];
        var outp = new List<PermissionRule>();
        foreach (var (key, level) in new[] { ("deny", PermissionLevel.Deny), ("ask", PermissionLevel.Ask), ("allow", PermissionLevel.Allow) })
            foreach (var x in p?[key]?.AsArray() ?? [])
                if (x?.ToString() is { Length: > 0 } pat) outp.Add(new PermissionRule { Pattern = pat, Level = level, Source = src, Repo = repo });
        return outp;
    }

    public static void WriteClaude(string file, string pattern, PermissionLevel? level)
    {
        var data = ReadJson(file);
        var perms = data["permissions"] as JsonObject ?? new JsonObject();
        data["permissions"] = perms;
        foreach (var key in new[] { "deny", "ask", "allow" })
        {
            var arr = perms[key] as JsonArray ?? [];
            var keep = new JsonArray(arr.Where(x => x?.ToString() != pattern).Select(x => (JsonNode?)JsonValue.Create(x!.ToString())).ToArray());
            if (keep.Count > 0) perms[key] = keep; else perms.Remove(key);
        }
        if (level != null)
        {
            var key = level.Value.ToString().ToLowerInvariant();
            var arr = perms[key] as JsonArray ?? [];
            arr.Add(pattern);
            perms[key] = arr;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Alle Regeln: Claude global, Projektdateien, pausierte und Copit-Regeln.</summary>
    public List<PermissionRule> All(IEnumerable<string> repos)
    {
        var list = ReadClaude(GlobalFile, RuleSource.Global, null);
        foreach (var r in repos) list.AddRange(ReadClaude(ProjectFile(r), RuleSource.Project, r));
        list.AddRange(_store.Value.Paused.Select(p => { p.Enabled = false; return p; }));
        list.AddRange(_store.Value.Copit);
        return list;
    }

    public List<PermissionRule> CopitRules => _store.Value.Copit;

    public void SaveCopit(List<PermissionRule> rules) { _store.Value.Copit = rules; _store.Save(); }

    public void SetLevel(PermissionRule r, PermissionLevel level)
    {
        if (r.Source == RuleSource.Copit) { var x = _store.Value.Copit.FirstOrDefault(c => c.Id == r.Id); if (x != null) x.Level = level; _store.Save(); }
        else if (!r.Enabled) { var x = _store.Value.Paused.FirstOrDefault(c => c.Id == r.Id); if (x != null) x.Level = level; _store.Save(); }
        else WriteClaude(FileOf(r), r.Pattern, level);
    }

    public void Toggle(PermissionRule r)
    {
        if (r.Source == RuleSource.Copit) { var x = _store.Value.Copit.FirstOrDefault(c => c.Id == r.Id); if (x != null) x.Enabled = !x.Enabled; _store.Save(); return; }
        if (r.Enabled) { WriteClaude(FileOf(r), r.Pattern, null); _store.Value.Paused.Add(new PermissionRule { Pattern = r.Pattern, Level = r.Level, Source = r.Source, Repo = r.Repo, Enabled = false }); }
        else { WriteClaude(FileOf(r), r.Pattern, r.Level); _store.Value.Paused.RemoveAll(p => p.Id == r.Id); }
        _store.Save();
    }

    public void Delete(PermissionRule r)
    {
        if (r.Source == RuleSource.Copit) _store.Value.Copit.RemoveAll(c => c.Id == r.Id);
        else if (!r.Enabled) _store.Value.Paused.RemoveAll(c => c.Id == r.Id);
        else WriteClaude(FileOf(r), r.Pattern, null);
        _store.Save();
    }

    public void Add(string pattern, PermissionLevel level, RuleSource src, string? repo)
    {
        if (src == RuleSource.Copit)
        {
            _store.Value.Copit.RemoveAll(c => c.Pattern == pattern && c.Repo == repo);
            _store.Value.Copit.Add(new PermissionRule { Pattern = pattern, Level = level, Source = src, Repo = repo });
            _store.Save();
        }
        else WriteClaude(src == RuleSource.Global ? GlobalFile : ProjectFile(repo!), pattern, level);
    }

    private static string FileOf(PermissionRule r) => r.Source == RuleSource.Global ? GlobalFile : ProjectFile(r.Repo!);

    private static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copit-Regeln (global + Repo) → CLI-Flags. Deny schlägt Allow.</summary>
    public (List<string> Allowed, List<string> Disallowed) Flags(string? repo, bool includeClaudeFiles = false)
    {
        var rules = _store.Value.Copit.Where(r => r.Enabled && (r.Repo == null || Same(r.Repo, repo))).ToList();
        if (includeClaudeFiles)
        {
            rules.AddRange(ReadClaude(GlobalFile, RuleSource.Global, null));
            if (repo != null) rules.AddRange(ReadClaude(ProjectFile(repo), RuleSource.Project, repo));
        }
        var deny = rules.Where(r => r.Level == PermissionLevel.Deny).Select(r => r.Pattern).ToHashSet();
        var allow = rules.Where(r => r.Level == PermissionLevel.Allow && !deny.Contains(r.Pattern)).Select(r => r.Pattern).Distinct().ToList();
        return (allow, deny.ToList());
    }

    public PermissionLevel Matrix(string opId, string? repo)
    {
        var op = Ops.First(o => o.Id == opId);
        var own = _store.Value.Copit.Where(r => r.Enabled && op.Patterns.Contains(r.Pattern) && (repo == null ? r.Repo == null : Same(r.Repo, repo))).ToList();
        if (own.Any(r => r.Level == PermissionLevel.Deny)) return PermissionLevel.Deny;
        if (own.Count > 0 && own.All(r => r.Level == PermissionLevel.Allow)) return PermissionLevel.Allow;
        return PermissionLevel.Ask;
    }

    public void SetMatrix(string opId, PermissionLevel level, string? repo)
    {
        _store.Value.Copit = SetMatrix(_store.Value.Copit, opId, level, repo);
        _store.Save();
    }

    private static List<PermissionRule> SetMatrix(List<PermissionRule> rules, string opId, PermissionLevel level, string? repo)
    {
        var op = Ops.First(o => o.Id == opId);
        var rest = rules.Where(r => !(op.Patterns.Contains(r.Pattern) && (repo == null ? r.Repo == null : Same(r.Repo, repo)))).ToList();
        if (level != PermissionLevel.Ask) rest.AddRange(op.Patterns.Select(p => new PermissionRule { Pattern = p, Level = level, Source = RuleSource.Copit, Repo = repo }));
        return rest;
    }

    /// <summary>Shell-Kette (&amp;&amp;, ||, ;, |) in Teilbefehle zerlegen – Anführungszeichen respektieren.</summary>
    public static List<string> SplitCommands(string cmd)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        for (int i = 0; i < cmd.Length; i++)
        {
            var c = cmd[i];
            if (quote != '\0') { if (c == quote && (i == 0 || cmd[i - 1] != '\\')) quote = '\0'; cur.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; cur.Append(c); continue; }
            if (i + 1 < cmd.Length && (cmd.Substring(i, 2) is "&&" or "||")) { parts.Add(cur.ToString()); cur.Clear(); i++; continue; }
            if (c is ';' or '|' or '\n') { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        parts.Add(cur.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static readonly HashSet<string> TwoWord = ["git", "npm", "npx", "dotnet", "yarn", "pnpm", "composer", "php", "docker", "kubectl", "gh", "glab"];

    /// <summary>Freigabe-Anfrage → nötige Muster (z. B. Bash(git add:*), Bash(git commit:*)).</summary>
    public static List<string> PatternsForDenial(string tool, string inputJson)
    {
        if (tool != "Bash") return [tool];
        try
        {
            var cmd = JsonNode.Parse(inputJson)?["command"]?.ToString() ?? "";
            var outp = new List<string>();
            foreach (var part in SplitCommands(cmd))
            {
                var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"', '\'')).ToArray();
                if (words.Length == 0) continue;
                var exe = System.Text.RegularExpressions.Regex.Replace(words[0], @"\.(exe|cmd)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (exe is "cd" or "pushd" or "popd") continue;
                var sub = words.Length > 1 && !words[1].StartsWith('-') ? words[1] : "";
                var p = $"Bash({(TwoWord.Contains(exe) && sub != "" ? $"{exe} {sub}" : exe)}:*)";
                if (!outp.Contains(p)) outp.Add(p);
            }
            return outp.Count > 0 ? outp : ["Bash"];
        }
        catch { return ["Bash"]; }
    }

    /// <summary>Claude-Muster → Copilot-Tool-Muster.</summary>
    public static string? ToCopilotTool(string p)
    {
        if (p is "Edit" or "Write" or "MultiEdit") return "write";
        if (p == "Bash") return "shell";
        var m = System.Text.RegularExpressions.Regex.Match(p, @"^Bash\((.+?)(?::\*)?\)$");
        return m.Success ? $"shell({m.Groups[1].Value})" : null;
    }
}
