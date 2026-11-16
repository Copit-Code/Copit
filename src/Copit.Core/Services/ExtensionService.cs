using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

// ---------------------------------------------------------------- Manifest (copit-extension.json)

public class ExtParam
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public bool Multiline { get; set; }
}

/// <summary>Automations-Aktion, die einen Befehl der Extension ausführt.</summary>
public class ExtAction
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Befehlszeile, z. B. "powershell -NoProfile -File {ext}/scripts/test.ps1" oder "node {ext}/run.js". {ext} = Extension-Ordner, {repo} = Repo.</summary>
    public string Run { get; set; } = "";
    public List<ExtParam> Params { get; set; } = [];
    public int TimeoutMinutes { get; set; } = 10;
}

/// <summary>Vorlage für „Neuer Agent“.</summary>
public class ExtAgentTemplate
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Auftrag mit Platzhaltern {{ticket}}, {{project}}, {{branch}}, {{ext}} (gilt auch für AllowedTools).</summary>
    public string Prompt { get; set; } = "";
    public string Mode { get; set; } = "Edit";
    public string? Branch { get; set; }
    /// <summary>Zusätzlich erlaubte Tools (Claude-Muster).</summary>
    public List<string> AllowedTools { get; set; } = [];
}

/// <summary>Vorlage im Tool (Schnellauswahl für Prompt und Server).</summary>
public class ExtToolPreset
{
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>Server, die bei dieser Vorlage vorausgewählt werden (Namen, Groß-/Kleinschreibung egal).</summary>
    public List<string> Servers { get; set; } = [];
}

/// <summary>Eigenes Tool einer Extension – erscheint als Eintrag in der Navigation.</summary>
public class ExtTool
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "grid";
    /// <summary>Art des Tools. Unterstützt: "mcp-prompt" (MCP-Server wählen + Auswertung prompten).</summary>
    public string Kind { get; set; } = "mcp-prompt";
    /// <summary>Zusätzliche Anweisungen an die KI für jede Ausführung.</summary>
    public string SystemPrompt { get; set; } = "";
    public string Placeholder { get; set; } = "";
    public List<ExtToolPreset> Presets { get; set; } = [];
    /// <summary>Darf die KI Dateien im Projektordner lesen (Voreinstellung, im Tool umschaltbar).</summary>
    public bool ReadFiles { get; set; }
}

public class ExtPermission
{
    public string Pattern { get; set; } = "";
    public string Level { get; set; } = "Allow";
}

public class ExtensionManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Homepage { get; set; } = "";
    public List<ExtAction> Actions { get; set; } = [];
    public List<ExtAgentTemplate> AgentTemplates { get; set; } = [];
    public List<Automation> Automations { get; set; } = [];
    public List<ExtPermission> Permissions { get; set; } = [];
    public List<ExtTool> Tools { get; set; } = [];
}

public class InstalledExtension
{
    public ExtensionManifest Manifest { get; set; } = new();
    /// <summary>"owner/repo[/pfad]" oder lokaler Ordner.</summary>
    public string Source { get; set; } = "";
    public bool IsLocal { get; set; }
    /// <summary>Release-Tag oder Branch.</summary>
    public string? Ref { get; set; }
    /// <summary>Commit-SHA (Branch-Installation) oder Tag (Release).</summary>
    public string? Revision { get; set; }
    public bool FromRelease { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public string? UpdateAvailable { get; set; }
    public string Dir => AppPaths.Dir(Path.Combine("extensions", Manifest.Id));
}

public class ExtensionsData { public List<InstalledExtension> Installed { get; set; } = []; }

public record ExtensionSource(string Owner, string Repo, string? SubPath, string? Ref)
{
    public string Display => $"{Owner}/{Repo}{(SubPath != null ? "/" + SubPath : "")}{(Ref != null ? "@" + Ref : "")}";
}

/// <summary>
/// Copit-Extensions: werden aus GitHub geladen (Release-Asset "*.copit.zip" oder Repo-Ordner mit copit-extension.json),
/// lokal unter %LOCALAPPDATA%\Copit\extensions abgelegt und automatisch aktualisiert.
/// </summary>
public class ExtensionService(DataService data, SettingsService settings)
{
    public const string ManifestFile = "copit-extension.json";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public List<InstalledExtension> Installed => data.Extensions.Value.Installed;
    public IEnumerable<InstalledExtension> Active => Installed.Where(e => e.Enabled);
    public event Action? Changed;

    /// <summary>"owner/repo", "owner/repo/pfad@ref" oder https://github.com/owner/repo/tree/ref/pfad.</summary>
    public static ExtensionSource? ParseSource(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.Length == 0) return null;
        var m = Regex.Match(s, @"^https?://github\.com/([^/]+)/([^/@#]+?)(?:\.git)?(?:/(?:tree|blob)/([^/]+)(?:/(.+))?)?$", RegexOptions.IgnoreCase);
        if (m.Success) return new(m.Groups[1].Value, m.Groups[2].Value, m.Groups[4].Success ? m.Groups[4].Value : null, m.Groups[3].Success ? m.Groups[3].Value : null);
        string? @ref = null;
        var at = s.LastIndexOf('@');
        if (at > 0) { @ref = s[(at + 1)..]; s = s[..at]; }
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(p => p is "." or "..")) return null;
        return new(parts[0], parts[1], parts.Length > 2 ? string.Join('/', parts.Skip(2)) : null, @ref);
    }

    public static readonly string[] KnownToolKinds = ["mcp-prompt"];

    public static ExtensionManifest ReadManifest(string dir)
    {
        var file = Path.Combine(dir, ManifestFile);
        if (!File.Exists(file)) throw new InvalidOperationException($"Keine {ManifestFile} gefunden.");
        var m = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(file), JsonOpts) ?? throw new InvalidOperationException("Manifest ist leer.");
        if (!Regex.IsMatch(m.Id ?? "", @"^[a-z0-9][a-z0-9\-\.]{1,60}$")) throw new InvalidOperationException("Manifest: \"id\" fehlt oder ist ungültig (a-z, 0-9, -, .).");
        if (string.IsNullOrWhiteSpace(m.Name)) m.Name = m.Id;
        foreach (var a in m.Actions)
            if (string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Run)) throw new InvalidOperationException("Manifest: jede Aktion braucht \"id\" und \"run\".");
        foreach (var t in m.Tools)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Title)) throw new InvalidOperationException("Manifest: jedes Tool braucht \"id\" und \"title\".");
            if (!KnownToolKinds.Contains(t.Kind)) throw new InvalidOperationException($"Manifest: Tool-Art \"{t.Kind}\" wird von dieser Copit-Version nicht unterstützt.");
        }
        return m;
    }

    /// <summary>Was die Extension mitbringt – für den Bestätigungsdialog vor der Installation.</summary>
    public static string Describe(ExtensionManifest m)
    {
        var lines = new List<string> { $"{m.Name} {m.Version}{(m.Author.Length > 0 ? " von " + m.Author : "")}" };
        if (m.Description.Length > 0) lines.Add(m.Description);
        if (m.Actions.Count > 0) lines.Add($"Führt Befehle aus: {string.Join("; ", m.Actions.Select(a => a.Run))}");
        if (m.AgentTemplates.Count > 0) lines.Add($"Agent-Vorlagen: {string.Join(", ", m.AgentTemplates.Select(t => t.Title))}");
        if (m.Tools.Count > 0) lines.Add($"Tools: {string.Join(", ", m.Tools.Select(t => t.Title))}");
        if (m.Automations.Count > 0) lines.Add($"Automationen (deaktiviert): {string.Join(", ", m.Automations.Select(a => a.Name))}");
        if (m.Permissions.Count > 0) lines.Add($"Regeln: {string.Join(", ", m.Permissions.Select(p => $"{p.Level} {p.Pattern}"))}");
        return string.Join("\n", lines);
    }

    private HttpClient Client(string accept = "application/vnd.github+json")
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Copit-Extensions", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        var token = settings.GetSecret(SecretKeys.GitHubToken);
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return http;
    }

    private async Task<JsonNode?> ApiAsync(string path, CancellationToken ct)
    {
        using var http = Client();
        using var r = await http.GetAsync("https://api.github.com/" + path, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub {(int)r.StatusCode}: {path}");
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Was würde installiert? (Release-Asset oder Branch-Stand)</summary>
    private async Task<(string Ref, string Revision, bool Release, string DownloadUrl)> ResolveAsync(ExtensionSource src, CancellationToken ct)
    {
        var repo = $"repos/{src.Owner}/{src.Repo}";
        // 1) Release mit *.copit.zip (nur ohne Unterordner)
        if (src.SubPath == null)
        {
            var rel = src.Ref != null ? await ApiAsync($"{repo}/releases/tags/{Uri.EscapeDataString(src.Ref)}", ct) : await ApiAsync($"{repo}/releases/latest", ct);
            var asset = (rel?["assets"] as JsonArray)?.FirstOrDefault(a => a?["name"]?.ToString()?.EndsWith(".copit.zip", StringComparison.OrdinalIgnoreCase) == true);
            if (rel != null && asset != null)
                return (rel["tag_name"]!.ToString(), rel["tag_name"]!.ToString(), true, asset["url"]!.ToString());
        }
        // 2) Repo-Stand (Branch/Tag/Default) als ZIP
        var info = await ApiAsync(repo, ct) ?? throw new InvalidOperationException($"Repository {src.Owner}/{src.Repo} nicht gefunden – privat? Dann unter Customize → Updates einen GitHub-Token hinterlegen.");
        var @ref = src.Ref ?? info["default_branch"]?.ToString() ?? "main";
        var commit = await ApiAsync($"{repo}/commits/{Uri.EscapeDataString(@ref)}", ct) ?? throw new InvalidOperationException($"Ref \"{@ref}\" nicht gefunden.");
        var sha = commit["sha"]!.ToString();
        return (@ref, sha, false, $"https://api.github.com/{repo}/zipball/{sha}");
    }

    private async Task<string> DownloadAndExtractAsync(string url, bool isAsset, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "copit-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, "ext.zip");
        using (var http = Client(isAsset ? "application/octet-stream" : "application/vnd.github+json"))
        using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            r.EnsureSuccessStatusCode();
            await using var f = File.Create(zip);
            await r.Content.CopyToAsync(f, ct);
        }
        var outDir = Path.Combine(tmp, "x");
        SafeExtract(zip, outDir);
        return outDir;
    }

    /// <summary>ZIP entpacken – Einträge außerhalb des Zielordners (Zip-Slip) werden abgelehnt.</summary>
    public static void SafeExtract(string zipFile, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zipFile);
        foreach (var e in z.Entries)
        {
            var dest = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unsicherer Pfad im Archiv: {e.FullName}");
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, true);
        }
    }

    /// <summary>Ordner mit dem Manifest im entpackten Archiv finden (GitHub-Zipball hat einen Wurzelordner).</summary>
    private static string FindRoot(string extracted, string? subPath)
    {
        var baseDir = File.Exists(Path.Combine(extracted, ManifestFile)) ? extracted
            : Directory.EnumerateDirectories(extracted).Count() == 1 && !File.Exists(Path.Combine(extracted, ManifestFile)) ? Directory.EnumerateDirectories(extracted).First() : extracted;
        var dir = subPath == null ? baseDir : Path.GetFullPath(Path.Combine(baseDir, subPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!dir.StartsWith(Path.GetFullPath(extracted), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Ungültiger Unterordner.");
        if (!File.Exists(Path.Combine(dir, ManifestFile))) throw new InvalidOperationException($"Keine {ManifestFile} in {(subPath ?? "der Repo-Wurzel")} gefunden.");
        return dir;
    }

    /// <summary>Lädt herunter und gibt das Manifest zur Bestätigung zurück – installiert noch nicht.</summary>
    public async Task<(ExtensionManifest Manifest, string StagedDir, ExtensionSource Source, string Ref, string Revision, bool Release)> PrepareAsync(string input, CancellationToken ct = default)
    {
        var src = ParseSource(input) ?? throw new InvalidOperationException("Quelle im Format owner/repo, owner/repo/ordner oder owner/repo@tag angeben.");
        var (@ref, rev, release, url) = await ResolveAsync(src, ct);
        var extracted = await DownloadAndExtractAsync(url, release, ct);
        var dir = FindRoot(extracted, release ? null : src.SubPath);
        return (ReadManifest(dir), dir, src, @ref, rev, release);
    }

    /// <summary>Installiert/aktualisiert aus einem vorbereiteten Ordner.</summary>
    public async Task<InstalledExtension> CommitAsync(ExtensionManifest m, string stagedDir, string source, bool isLocal, string? @ref, string? revision, bool release,
        PermissionService perms, bool applyPermissions)
    {
        var existing = Installed.FirstOrDefault(e => e.Manifest.Id == m.Id);
        var target = AppPaths.Dir(Path.Combine("extensions", m.Id));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        CopyDir(stagedDir, target);
        var ext = existing ?? new InstalledExtension();
        ext.Manifest = m; ext.Source = source; ext.IsLocal = isLocal; ext.Ref = @ref; ext.Revision = revision; ext.FromRelease = release;
        ext.InstalledAt = DateTime.Now; ext.UpdateAvailable = null;
        if (existing == null) Installed.Add(ext);
        // Automations-Rezepte (deaktiviert) einmalig übernehmen
        foreach (var a in m.Automations)
        {
            var id = $"ext:{m.Id}:{(string.IsNullOrWhiteSpace(a.Id) ? a.Name : a.Id)}";
            if (data.Automations.Value.Automations.Any(x => x.Id == id)) continue;
            a.Id = id; a.Enabled = false; a.Name = $"{a.Name} ({m.Name})";
            data.Automations.Value.Automations.Add(a);
        }
        if (applyPermissions)
            foreach (var p in m.Permissions)
                if (Enum.TryParse<PermissionLevel>(p.Level, true, out var lvl)) perms.Add(p.Pattern, lvl, RuleSource.Copit, null);
        await data.Automations.SaveAsync();
        await data.Extensions.SaveAsync();
        Changed?.Invoke();
        return ext;
    }

    public static (ExtensionManifest Manifest, string Dir) PrepareLocal(string folder) => (ReadManifest(folder), folder);

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or "node_modules")) continue;
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    public async Task UninstallAsync(InstalledExtension e, bool removeAutomations)
    {
        try { if (Directory.Exists(e.Dir)) Directory.Delete(e.Dir, true); } catch { }
        Installed.Remove(e);
        if (removeAutomations) data.Automations.Value.Automations.RemoveAll(a => a.Id.StartsWith($"ext:{e.Manifest.Id}:"));
        await data.Automations.SaveAsync();
        await data.Extensions.SaveAsync();
        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(InstalledExtension e, bool on) { e.Enabled = on; await data.Extensions.SaveAsync(); Changed?.Invoke(); }

    /// <summary>Prüft alle GitHub-Extensions auf neuere Releases/Commits.</summary>
    public async Task<int> CheckUpdatesAsync(CancellationToken ct = default)
    {
        var n = 0;
        foreach (var e in Installed.Where(e => !e.IsLocal))
        {
            try
            {
                var src = ParseSource(e.Source);
                if (src == null) continue;
                // Bei festem Tag nur prüfen, wenn auf Branch/Latest installiert
                var (_, rev, _, _) = await ResolveAsync(e.FromRelease && src.Ref == null ? src : src with { Ref = e.FromRelease ? null : src.Ref }, ct);
                e.UpdateAvailable = rev != e.Revision ? rev : null;
                if (e.UpdateAvailable != null) n++;
            }
            catch { }
        }
        await data.Extensions.SaveAsync();
        Changed?.Invoke();
        return n;
    }

    public async Task<InstalledExtension> UpdateAsync(InstalledExtension e, PermissionService perms, CancellationToken ct = default)
    {
        if (e.IsLocal)
        {
            var (m, dir) = PrepareLocal(e.Source);
            return await CommitAsync(m, dir, e.Source, true, null, null, false, perms, false);
        }
        var p = await PrepareAsync(e.Source, ct);
        return await CommitAsync(p.Manifest, p.StagedDir, e.Source, false, p.Ref, p.Revision, p.Release, perms, false);
    }

    // ------------------------------------------------------------ Beiträge

    /// <summary>Platzhalter in Agent-Vorlagen: {{ticket}}, {{project}}, {{branch}}, {{ext}} (Extension-Ordner mit '/', z. B. für Skriptaufrufe).</summary>
    public static string FillTemplate(string text, string ticket, string project, string branch, string extDir) =>
        text.Replace("{{ticket}}", ticket.Trim().ToUpperInvariant()).Replace("{{project}}", project).Replace("{{branch}}", branch)
            .Replace("{{ext}}", extDir.Replace('\\', '/').TrimEnd('/'));

    /// <summary>Tools aktiver Extensions; Route "ext:&lt;extension&gt;/&lt;tool&gt;".</summary>
    public IEnumerable<(InstalledExtension Ext, ExtTool Tool, string Route)> Tools =>
        Active.SelectMany(e => e.Manifest.Tools.Select(t => (e, t, $"ext:{e.Manifest.Id}/{t.Id}")));

    public IEnumerable<(InstalledExtension Ext, ExtAgentTemplate Template)> AgentTemplates =>
        Active.SelectMany(e => e.Manifest.AgentTemplates.Select(t => (e, t)));

    /// <summary>Aktionen für den Automation-Builder (Typ "ext:&lt;id&gt;/&lt;aktion&gt;").</summary>
    public IEnumerable<ActionDef> ActionDefs =>
        Active.SelectMany(e => e.Manifest.Actions.Select(a => new ActionDef($"ext:{e.Manifest.Id}/{a.Id}", a.Label.Length > 0 ? a.Label : a.Id, e.Manifest.Name,
            a.Params.Select(p => new ParamDef(p.Key, p.Label.Length > 0 ? p.Label : p.Key, p.Placeholder, p.Multiline)).ToArray())));

    /// <summary>Führt eine Extension-Aktion aus. Parameter und Variablen kommen als Umgebungsvariablen COPIT_PARAM_* / COPIT_VAR_*.</summary>
    public async Task<string> RunActionAsync(string type, IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> vars, string? cwd)
    {
        var m = Regex.Match(type, @"^ext:([^/]+)/(.+)$");
        var ext = Active.FirstOrDefault(e => e.Manifest.Id == m.Groups[1].Value) ?? throw new InvalidOperationException($"Extension {m.Groups[1].Value} nicht installiert oder deaktiviert.");
        var act = ext.Manifest.Actions.FirstOrDefault(a => a.Id == m.Groups[2].Value) ?? throw new InvalidOperationException($"Aktion {m.Groups[2].Value} fehlt in {ext.Manifest.Name}.");
        // {ext}/{repo} werden ohne Anführungszeichen ersetzt – im Manifest bei Bedarf selbst quoten: -File "{ext}/x.ps1"
        var cmd = act.Run.Replace("{ext}", ext.Dir).Replace("{repo}", cwd ?? "");
        var env = new Dictionary<string, string> { ["COPIT_EXT_DIR"] = ext.Dir, ["COPIT_REPO"] = cwd ?? "" };
        foreach (var (k, v) in parameters) env["COPIT_PARAM_" + Regex.Replace(k.ToUpperInvariant(), "[^A-Z0-9]", "_")] = v;
        foreach (var (k, v) in vars) env["COPIT_VAR_" + Regex.Replace(k.ToUpperInvariant(), "[^A-Z0-9]", "_")] = v;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(act.TimeoutMinutes, 1, 120)));
        var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", cmd], cwd ?? ext.Dir, env: env, ct: cts.Token);
        var outp = (r.StdOut + (r.Ok ? "" : "\n" + r.StdErr)).Trim();
        if (!r.Ok) throw new InvalidOperationException($"{act.Label}: Exit {r.ExitCode} – {(outp.Length > 300 ? outp[^300..] : outp)}");
        return outp.Length > 300 ? outp[^300..] : outp;
    }
}
