using System.Text.RegularExpressions;

namespace Copit.Core.Services;

/// <summary>Aufgelöster Befehl: ausführbare Datei + Argumente die vorangestellt werden müssen.</summary>
public record CliCommand(string FileName, IReadOnlyList<string> PrefixArgs, string DisplayPath);

/// <summary>
/// Findet claude / copilot / git auf Windows. npm-Shims (*.cmd) werden auf "node script.js" aufgelöst,
/// damit Argumente sicher (ohne cmd.exe Quoting) übergeben werden können.
/// </summary>
public static partial class CliLocator
{
    public static CliCommand? Resolve(string name, string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return FromFile(configuredPath);

        foreach (var dir in CandidateDirs())
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" })
            {
                var f = Path.Combine(dir, name + ext);
                if (File.Exists(f) && (ext != "" || OperatingSystem.IsWindows() == false))
                    return FromFile(f);
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(appData, "npm"),
            Path.Combine(local, "Programs", "claude"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
            Path.Combine(local, "pnpm"),
            @"C:\Program Files\Git\cmd",
            @"C:\Program Files\nodejs",
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        dirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static CliCommand FromFile(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            var resolved = TryResolveNpmShim(file);
            if (resolved != null) return resolved;
            return new CliCommand("cmd.exe", ["/d", "/s", "/c", file], file);
        }
        return new CliCommand(file, [], file);
    }

    private static CliCommand? TryResolveNpmShim(string cmdFile)
    {
        try
        {
            var text = File.ReadAllText(cmdFile);
            var m = ShimRegex().Match(text);
            if (!m.Success) return null;
            var dir = Path.GetDirectoryName(cmdFile)!;
            var script = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.TrimStart('\\', '/')));
            if (!File.Exists(script)) return null;
            var node = Path.Combine(dir, "node.exe");
            if (!File.Exists(node)) node = Resolve("node")?.FileName ?? "node";
            return new CliCommand(node, [script], cmdFile);
        }
        catch { return null; }
    }

    [GeneratedRegex(@"""%(?:~)?dp0%?\\?([^""]+?\.(?:js|mjs|cjs))""", RegexOptions.IgnoreCase)]
    private static partial Regex ShimRegex();
}
