using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Copit.Core.Services;

public record ReleaseInfo(string Tag, Version Version, string Name, string Notes, string HtmlUrl, string? InstallerName, string? InstallerApiUrl, long InstallerSize);

/// <summary>
/// Auto-Updater: prüft das neueste GitHub-Release (auch private Repos mit Token),
/// lädt den Installer (Copit-Setup-x.y.z.exe) und startet ihn still – er schließt Copit, aktualisiert und startet neu.
/// </summary>
public class UpdateService(SettingsService settings)
{
    public static Version Current
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Parse(info) ?? asm.GetName().Version ?? new Version(0, 0);
        }
    }

    /// <summary>"v1.2.3", "1.2.3+sha" oder "1.2" → Version (ohne Suffix).</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    public static bool IsNewer(Version remote, Version current) =>
        remote > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    private HttpClient Client(string accept)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Copit-Updater", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        var token = settings.GetSecret(SecretKeys.GitHubToken);
        if (!string.IsNullOrWhiteSpace(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return http;
    }

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct = default)
    {
        var repo = settings.Settings.Update.Repo.Trim().Trim('/');
        if (repo.Length == 0) return null;
        using var http = Client("application/vnd.github+json");
        using var resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(settings.GetSecret(SecretKeys.GitHubToken))
                ? $"Kein Release gefunden – {repo} ist privat? Dann einen GitHub-Token (Lesezugriff) hinterlegen."
                : $"Kein Release in {repo} gefunden (oder der Token hat keinen Zugriff).");
        resp.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        var tag = j["tag_name"]?.ToString() ?? "";
        var version = Parse(tag);
        if (version == null) return null;
        var asset = (j["assets"] as JsonArray ?? []).FirstOrDefault(a => a?["name"]?.ToString() is { } n && n.StartsWith("Copit-Setup", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        return new ReleaseInfo(tag, version, j["name"]?.ToString() ?? tag, j["body"]?.ToString() ?? "", j["html_url"]?.ToString() ?? "",
            asset?["name"]?.ToString(), asset?["url"]?.ToString(), asset?["size"]?.GetValue<long>() ?? 0);
    }

    /// <summary>Neueres Release als die laufende Version? (übersprungene Version zählt nicht.)</summary>
    public async Task<ReleaseInfo?> CheckAsync(bool ignoreSkipped = false, CancellationToken ct = default)
    {
        var r = await LatestAsync(ct);
        if (r == null || !IsNewer(r.Version, Current)) return null;
        if (!ignoreSkipped && settings.Settings.Update.SkippedVersion == r.Tag) return null;
        return r;
    }

    public async Task<string> DownloadAsync(ReleaseInfo r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (r.InstallerApiUrl == null) throw new InvalidOperationException("Das Release enthält keinen Installer (Copit-Setup-*.exe).");
        var dir = Path.Combine(Path.GetTempPath(), "copit-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.InstallerName!);
        using var http = Client("application/octet-stream");
        using var resp = await http.GetAsync(r.InstallerApiUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? r.InstallerSize;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }
        return file;
    }

    /// <summary>Installer still starten; er schließt Copit, installiert und startet Copit neu.</summary>
    public static void RunInstaller(string file) =>
        Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
}
