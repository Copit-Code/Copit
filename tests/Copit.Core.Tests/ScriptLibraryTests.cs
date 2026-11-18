using System.Diagnostics;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Core.Storage;

namespace Copit.Core.Tests;

public class ScriptLibraryTests
{
    [Fact]
    public void SeedsIndexesAndCountsUses()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("copit-scripts").FullName, "lib");
        ScriptLibrary.Seed(dir);
        File.WriteAllText(Path.Combine(dir, "logs.py"), "# Zweck: Fehler aus Logs zählen\n# Aufruf: logs.py <datei>\nprint(1)\n");
        var list = ScriptLibrary.List(dir);
        Assert.Equal(["dateien.ps1", "logs.py", "suche.ps1"], list.Select(s => s.Name));
        Assert.StartsWith("Text/Regex in Dateien suchen", list.Single(s => s.Name == "suche.ps1").Purpose);
        Assert.Equal("logs.py <datei>", list.Single(s => s.Name == "logs.py").Usage);

        var d = ScriptLibrary.Slash(dir);
        Assert.True(ScriptLibrary.RecordUse(dir, $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{d}/suche.ps1\" -Muster TODO"));
        Assert.True(ScriptLibrary.RecordUse(dir, $"python \"{dir}\\logs.py\" app.log".Replace('/', '\\')));
        Assert.False(ScriptLibrary.RecordUse(dir, "git status"));
        list = ScriptLibrary.List(dir);
        Assert.Equal(1, list.Single(s => s.Name == "suche.ps1").Uses);
        Assert.Equal(1, list.Single(s => s.Name == "logs.py").Uses);

        // Seed überschreibt keine vorhandene Bibliothek
        File.Delete(Path.Combine(dir, "dateien.ps1"));
        ScriptLibrary.Seed(dir);
        Assert.False(File.Exists(Path.Combine(dir, "dateien.ps1")));
    }

    [Fact]
    public void InstructionsAndPermissionsMatchInvocation()
    {
        var dir = Directory.CreateTempSubdirectory("copit-scripts2").FullName;
        var inv = ScriptLibrary.Invocation(dir, "suche.ps1");
        var allowed = ScriptLibrary.AllowedTools(dir);
        // Der dokumentierte Aufruf muss mit einem freigegebenen Präfix beginnen
        Assert.Contains(allowed, a => inv.StartsWith(a["Bash(".Length..^":*)".Length]));
        var text = ScriptLibrary.Instructions(dir, [new ScriptInfo("suche.ps1", "", "Suchen", "suche.ps1 -Muster x", 3, null)], canWrite: true);
        Assert.Contains("- suche.ps1: Suchen · Aufruf: suche.ps1 -Muster x", text);
        Assert.Contains("# Zweck:", text);
        Assert.DoesNotContain("# Zweck:", ScriptLibrary.Instructions(dir, [], canWrite: false));
    }

    [Fact]
    public void ScopeSelectsFolder()
    {
        AppPaths.Root = Directory.CreateTempSubdirectory("copit-home").FullName;
        var repo = Directory.CreateTempSubdirectory("copit-repo").FullName;
        Assert.Equal(Path.Combine(repo, ".copit", "scripts"), ScriptLibrary.Dir(new ScriptLibrarySettings { Scope = "project" }, repo));
        Assert.Equal(Path.Combine(AppPaths.Root, "scripts"), ScriptLibrary.Dir(new ScriptLibrarySettings { Scope = "global" }, repo));
        Assert.Equal(Path.Combine(AppPaths.Root, "scripts"), ScriptLibrary.Dir(new ScriptLibrarySettings { Scope = "project" }, null));
    }

    [Fact]
    public void SeededSearchScriptRuns()
    {
        var pwsh = new[] { "pwsh", "pwsh.exe" }.SelectMany(n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools")).Select(d => Path.Combine(d, n))).FirstOrDefault(File.Exists);
        if (pwsh == null) return;
        var root = Directory.CreateTempSubdirectory("copit-run").FullName;
        var lib = Path.Combine(root, "lib"); ScriptLibrary.Seed(lib);
        Directory.CreateDirectory(Path.Combine(root, "src")); Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class A {}\n// TODO: aufräumen\n");
        File.WriteAllText(Path.Combine(root, "bin", "b.cs"), "// TODO: ignorieren\n");
        // Ausgabe explizit als UTF-8, sonst hängt das „ä“ unter Windows von der Konsolen-Codepage ab
        var script = Path.Combine(lib, "suche.ps1").Replace("'", "''");
        var psi = new ProcessStartInfo(pwsh, ["-NoProfile", "-Command", $"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); & '{script}' -Muster TODO"])
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        Assert.True(p.ExitCode == 0, e);
        Assert.Contains("a.cs:2: // TODO: aufräumen", o);
        Assert.DoesNotContain("ignorieren", o);
    }
}
