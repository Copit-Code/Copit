using System.Text.RegularExpressions;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Core.Storage;

namespace Copit.Core.Tests;

/// <summary>Erzeugt die Doku für das Copit-Repo selbst (in einen ignorierten Ordner) und prüft jeden relativen Link. Nur mit COPIT_IT=1.</summary>
public class DocsIntegrationTests
{
    [Fact]
    public async Task GeneratesLinkedDocsForThisRepo()
    {
        if (Environment.GetEnvironmentVariable("COPIT_IT") != "1") return;
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "Copit.slnx")) && Path.GetDirectoryName(repo) is { } up) repo = up;
        AppPaths.Root = Directory.CreateTempSubdirectory("copit-docs-home").FullName;

        var settings = new SettingsService(new PlainProtector());
        var data = new DataService();
        var docs = new DocsService(data, new ConnectionHub(settings), new GitService(), new SessionStore(), settings);
        var project = new CopitProject { Name = "Copit", SourceFolder = repo, Instructions = "Deutsch, keine Emojis." };
        data.Projects.Value.Projects.Add(project);
        var folder = "artifacts/docs-test";
        docs.For(project).Folder = folder;
        var outDir = Path.Combine(repo, "artifacts", "docs-test");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);

        var r = await docs.GenerateAsync(project, _ => { });
        Assert.True(r.Modules >= 3, $"Module: {r.Modules}");
        Assert.True(r.Commits >= 1);
        Assert.True(File.Exists(Path.Combine(outDir, "README.md")));
        Assert.True(File.Exists(Path.Combine(outDir, "architecture", "README.md")));

        // Jeder relative Link muss auf eine existierende Datei/einen Ordner zeigen
        var broken = new List<string>();
        foreach (var md in Directory.EnumerateFiles(outDir, "*.md", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(md), @"\]\(([^)#\s]+)(?:#[^)]*)?\)"))
            {
                var target = Uri.UnescapeDataString(m.Groups[1].Value);
                if (target.StartsWith("http")) continue;
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(md)!, target.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(full) && !Directory.Exists(full)) broken.Add($"{Path.GetRelativePath(outDir, md)} → {target}");
            }
        Assert.True(broken.Count == 0, "Kaputte Links:\n" + string.Join("\n", broken.Take(20)));

        // Zweiter Lauf: eigener Text außerhalb des Blocks bleibt erhalten
        var arch = Path.Combine(outDir, "architecture", "README.md");
        File.WriteAllText(arch, File.ReadAllText(arch).Replace("_Architektur-Überblick – wird vom Doku-Agent ergänzt oder von Hand gepflegt._", "Handgeschrieben."));
        await docs.GenerateAsync(project, _ => { });
        Assert.Contains("Handgeschrieben.", File.ReadAllText(arch));
    }
}
