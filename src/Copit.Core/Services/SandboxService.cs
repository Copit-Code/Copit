using System.Security.Cryptography;
using System.Text;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

/// <summary>
/// Agents in einer Docker-Sandbox laufen lassen:
/// <list type="bullet">
/// <item>Container hängt nur im internen Netz <see cref="Network"/> (<c>docker network create --internal</c>) – keine Route nach außen, kein Intranet.</item>
/// <item>Einziger Ausgang ist der Proxy-Container <see cref="Proxy"/> (Squid). Er lässt nur die KI-Endpunkte durch (Allowlist)
///       und sperrt private/interne Adressbereiche ausdrücklich – auch wenn ein erlaubter Name intern aufgelöst würde.</item>
/// <item>Gemountet wird nur der Arbeitsordner (eigene Repo-Kopie) und die Login-Daten der CLI.</item>
/// </list>
/// </summary>
public class SandboxService(SettingsService settings)
{
    public const string Network = "copit-sandbox";
    public const string Proxy = "copit-egress";
    public const string ProxyImage = "ubuntu/squid:latest";
    public const string DefaultImage = "copit-sandbox:latest";

    /// <summary>Standard-Allowlist: nur was Claude Code und GitHub Copilot CLI zum Arbeiten brauchen.</summary>
    public static readonly string[] DefaultAllowedHosts =
    [
        ".anthropic.com", "claude.ai",
        ".githubcopilot.com", "api.github.com", "github.com", "copilot-proxy.githubusercontent.com",
    ];

    public const string Dockerfile = """
        FROM node:22-bookworm-slim
        RUN apt-get update \
         && apt-get install -y --no-install-recommends git ca-certificates ripgrep python3 \
         && rm -rf /var/lib/apt/lists/*
        RUN npm install -g @anthropic-ai/claude-code @github/copilot && npm cache clean --force
        USER node
        ENV HOME=/home/node
        WORKDIR /work
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SandboxSettings S => settings.Settings.Ai.Sandbox;
    public string Image => string.IsNullOrWhiteSpace(S.Image) ? DefaultImage : S.Image.Trim();
    public IReadOnlyList<string> AllowedHosts => S.AllowedHosts.Count > 0 ? S.AllowedHosts : DefaultAllowedHosts;

    private static Task<ProcessResult> Docker(CancellationToken ct, params string[] args) => ProcessRunner.RunAsync("docker", args, ct: ct);

    public async Task<(bool Ok, string Info)> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await Docker(ct, "version", "--format", "{{.Server.Version}}");
            return r.Ok ? (true, "Docker " + r.StdOut.Trim()) : (false, "Docker läuft nicht: " + (r.StdErr + r.StdOut).Trim());
        }
        catch (Exception e) { return (false, "Docker nicht gefunden – Docker Desktop installieren und starten. (" + e.Message + ")"); }
    }

    /// <summary>Image, internes Netz und Egress-Proxy bereitstellen (idempotent).</summary>
    public async Task EnsureAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (ok, info) = await CheckAsync(ct);
            if (!ok) throw new InvalidOperationException(info);
            var dir = AppPaths.Dir("sandbox");

            // 1) Image
            if (!(await Docker(ct, "image", "inspect", Image)).Ok)
            {
                if (Image != DefaultImage) throw new InvalidOperationException($"Sandbox-Image „{Image}“ nicht gefunden – erst bauen/pullen oder in den Einstellungen leeren.");
                progress?.Invoke("Sandbox-Image wird gebaut (einmalig, dauert ein paar Minuten)…");
                File.WriteAllText(Path.Combine(dir, "Dockerfile"), Dockerfile.Replace("\r", ""));
                var b = await ProcessRunner.RunAsync("docker", ["build", "-t", DefaultImage, dir], ct: ct);
                if (!b.Ok) throw new InvalidOperationException("Sandbox-Image konnte nicht gebaut werden: " + Tail(b.StdErr + b.StdOut));
            }

            // 2) Internes Netz ohne Route nach außen
            if (!(await Docker(ct, "network", "inspect", Network)).Ok)
            {
                progress?.Invoke("Internes Sandbox-Netz wird angelegt…");
                var n = await Docker(ct, "network", "create", "--internal", Network);
                if (!n.Ok) throw new InvalidOperationException("Sandbox-Netz: " + Tail(n.StdErr + n.StdOut));
            }

            // 3) Egress-Proxy mit Allowlist
            var conf = SquidConf(AllowedHosts);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conf)))[..12].ToLowerInvariant();
            var confPath = Path.Combine(dir, "squid.conf");
            File.WriteAllText(confPath, conf);
            var label = await Docker(ct, "inspect", "-f", "{{index .Config.Labels \"copit.conf\"}}|{{.State.Running}}", Proxy);
            var current = label.Ok ? label.StdOut.Trim() : "";
            if (current != $"{hash}|true")
            {
                progress?.Invoke("Sandbox-Proxy wird gestartet…");
                await Docker(ct, "rm", "-f", Proxy);
                var run = await Docker(ct, "run", "-d", "--name", Proxy, "--restart", "unless-stopped", "--label", "copit.conf=" + hash,
                    "-v", $"{confPath}:/etc/squid/squid.conf:ro", ProxyImage);
                if (!run.Ok) throw new InvalidOperationException("Sandbox-Proxy: " + Tail(run.StdErr + run.StdOut));
                var con = await Docker(ct, "network", "connect", Network, Proxy);
                if (!con.Ok) throw new InvalidOperationException("Sandbox-Proxy ans Netz hängen: " + Tail(con.StdErr + con.StdOut));
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Squid-Konfiguration: nur Allowlist über HTTPS, private/interne Ziele immer gesperrt.</summary>
    public static string SquidConf(IEnumerable<string> hosts)
    {
        var list = hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0 && !h.Contains(' ')).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Von Copit erzeugt – Sandbox-Egress (nur Allowlist)");
        sb.AppendLine("http_port 3128");
        sb.AppendLine("acl internal dst 0.0.0.0/8 10.0.0.0/8 100.64.0.0/10 127.0.0.0/8 169.254.0.0/16 172.16.0.0/12 192.168.0.0/16 fc00::/7 fe80::/10 ::1");
        sb.AppendLine("acl SSL_ports port 443");
        sb.AppendLine("acl CONNECT method CONNECT");
        if (list.Count > 0) sb.AppendLine("acl allowed dstdomain " + string.Join(' ', list));
        sb.AppendLine("http_access deny internal");
        sb.AppendLine("http_access deny !SSL_ports");
        sb.AppendLine("http_access deny CONNECT !SSL_ports");
        if (list.Count > 0) sb.AppendLine("http_access allow allowed");
        sb.AppendLine("http_access deny all");
        sb.AppendLine("cache deny all");
        sb.AppendLine("access_log stdio:/dev/stdout");
        return sb.ToString();
    }

    /// <summary>Name des Agent-Containers (für Abbruch per <c>docker rm -f</c>).</summary>
    public static string ContainerName(string sessionId) => "copit-agent-" + new string(sessionId.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToLowerInvariant();

    /// <summary>
    /// <c>docker run</c>-Argumente: CLI <paramref name="exe"/> mit <paramref name="cliArgs"/> im Container,
    /// Arbeitsordner unter /work, nur internes Netz + Proxy.
    /// </summary>
    public static List<string> RunArgs(string image, string containerName, string workDir, AiProvider provider, string exe, IEnumerable<string> cliArgs,
        string userHome, Func<string, string?> env, (string Name, string Email)? gitUser)
    {
        var a = new List<string>
        {
            "run", "--rm", "-i", "--init", "--name", containerName,
            "--network", Network,
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "512",
            "-e", $"HTTPS_PROXY=http://{Proxy}:3128", "-e", $"HTTP_PROXY=http://{Proxy}:3128",
            "-e", $"https_proxy=http://{Proxy}:3128", "-e", $"http_proxy=http://{Proxy}:3128",
            "-e", "NO_PROXY=localhost,127.0.0.1", "-e", "no_proxy=localhost,127.0.0.1",
            // Repo-Kopie gehört auf dem Host einem anderen Benutzer → Git sonst „dubious ownership“
            "-e", "GIT_CONFIG_COUNT=1", "-e", "GIT_CONFIG_KEY_0=safe.directory", "-e", "GIT_CONFIG_VALUE_0=*",
            "-e", "CI=1",
            "-v", $"{workDir}:/work", "-w", "/work",
        };
        if (gitUser is { } g)
        {
            a.AddRange(["-e", $"GIT_AUTHOR_NAME={g.Name}", "-e", $"GIT_AUTHOR_EMAIL={g.Email}", "-e", $"GIT_COMMITTER_NAME={g.Name}", "-e", $"GIT_COMMITTER_EMAIL={g.Email}"]);
        }
        if (provider == AiProvider.Claude)
        {
            var claudeDir = Path.Combine(userHome, ".claude");
            if (Directory.Exists(claudeDir)) a.AddRange(["-v", $"{claudeDir}:/home/node/.claude"]);
            var claudeJson = Path.Combine(userHome, ".claude.json");
            if (File.Exists(claudeJson)) a.AddRange(["-v", $"{claudeJson}:/home/node/.claude.json"]);
            foreach (var n in new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN" }) if (!string.IsNullOrEmpty(env(n))) a.AddRange(["-e", n]);
        }
        else
        {
            var copilotDir = Path.Combine(userHome, ".copilot");
            if (Directory.Exists(copilotDir)) a.AddRange(["-v", $"{copilotDir}:/home/node/.copilot"]);
            foreach (var n in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) if (!string.IsNullOrEmpty(env(n))) a.AddRange(["-e", n]);
        }
        a.Add(image);
        a.Add(exe);
        a.AddRange(cliArgs);
        return a;
    }

    /// <summary>Container eines Agents hart beenden (Abbruch).</summary>
    public static Task KillAsync(string containerName) => ProcessRunner.RunAsync("docker", ["rm", "-f", containerName]);

    /// <summary>
    /// Eigene Repo-Kopie für die Sandbox (kein Worktree – dessen .git-Verweis zeigt auf einen Host-Pfad, den der Container nicht kennt).
    /// origin zeigt danach auf das echte Remote, damit Push/Pipeline-Anzeige vom Host aus funktionieren.
    /// </summary>
    public static async Task<string> CreateWorkspaceAsync(GitService git, string sourceRepo, string branch, string sessionId, CancellationToken ct = default)
    {
        var safe = string.Concat(branch.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var dir = Path.Combine(AppPaths.Dir("sandboxes"), $"{Path.GetFileName(sourceRepo.TrimEnd('\\', '/'))}-{safe}-{sessionId[..Math.Min(6, sessionId.Length)]}");
        var clone = await ProcessRunner.RunAsync("git", ["clone", "--no-hardlinks", "--quiet", sourceRepo, dir], ct: ct);
        if (!clone.Ok) throw new InvalidOperationException("Sandbox-Kopie: " + Tail(clone.StdErr + clone.StdOut));
        var exists = (await git.RunAsync(dir, ct, "rev-parse", "--verify", "--quiet", "refs/remotes/origin/" + branch)).Ok;
        var co = exists ? await git.RunAsync(dir, ct, "checkout", "-q", "-B", branch, "origin/" + branch)
                        : await git.RunAsync(dir, ct, "checkout", "-q", "-b", branch);
        if (!co.Ok) throw new InvalidOperationException("Sandbox-Branch: " + Tail(co.StdErr + co.StdOut));
        // origin auf das echte Remote des Projekts zeigen lassen (Push läuft auf dem Host, nicht im Container)
        var url = await git.GetRemoteUrlAsync(sourceRepo, ct);
        if (!string.IsNullOrWhiteSpace(url))
        {
            await git.RunAsync(dir, ct, "remote", "set-url", "origin", url);
            _ = git.RunAsync(dir, CancellationToken.None, "fetch", "--quiet", "origin"); // Remote-Stand für den Lieferstatus, im Hintergrund
        }
        return dir;
    }

    private static string Tail(string s) { s = s.Trim(); return s.Length > 400 ? "…" + s[^400..] : s; }
}
