using System.IO.Compression;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class DocsExtensionTests
{
    [Fact]
    public void ParsesExtensionSources()
    {
        Assert.Equal(new ExtensionSource("a", "b", null, null), ExtensionService.ParseSource("a/b"));
        Assert.Equal(new ExtensionSource("Copit-Code", "Copit", "extensions/copit-dotnet", null), ExtensionService.ParseSource("Copit-Code/Copit/extensions/copit-dotnet"));
        Assert.Equal(new ExtensionSource("a", "b", null, "v1.2.0"), ExtensionService.ParseSource("a/b@v1.2.0"));
        Assert.Equal(new ExtensionSource("a", "b", "x/y", "main"), ExtensionService.ParseSource("https://github.com/a/b/tree/main/x/y"));
        Assert.Null(ExtensionService.ParseSource("nur-ein-teil"));
        Assert.Null(ExtensionService.ParseSource("a/b/../c"));
    }

    [Fact]
    public void ZipSlipIsRejected()
    {
        var dir = Directory.CreateTempSubdirectory("copit-zip").FullName;
        var zip = Path.Combine(dir, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("../../boese.txt").Open())) w.Write("x");
        Assert.Throws<InvalidOperationException>(() => ExtensionService.SafeExtract(zip, Path.Combine(dir, "out")));
    }

    [Fact]
    public void ReadsBundledExampleManifest()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "extensions")) && Path.GetDirectoryName(root) is { } up) root = up;
        var m = ExtensionService.ReadManifest(Path.Combine(root, "extensions", "copit-dotnet"));
        Assert.Equal("copit-dotnet", m.Id);
        Assert.Equal(2, m.AgentTemplates.Count);
        Assert.Equal("ext:copit-dotnet/test", m.Automations[0].Actions[0].Type);
        Assert.Contains("dotnet test", ExtensionService.Describe(m));
    }

    [Fact]
    public void MergeKeepsTextOutsideGeneratedBlock()
    {
        var first = DocsService.Merge(null, "# Titel\nA", "\n## Zweck\n\nPlatzhalter\n");
        var edited = first.Replace("Platzhalter", "Von Hand geschrieben");
        var second = DocsService.Merge(edited, "# Titel\nB");
        Assert.Contains("# Titel\nB", second);
        Assert.DoesNotContain("# Titel\nA", second);
        Assert.Contains("Von Hand geschrieben", second);
    }

    [Fact]
    public void LinksAreRelativeAndTicketsLinkified()
    {
        Assert.Equal("../../src/App/Foo.cs", DocsService.Link("docs/architecture/app.md", "src/App/Foo.cs"));
        Assert.Equal("tickets/SHOP-1.md", DocsService.Link("docs/history/README.md", "docs/history/tickets/SHOP-1.md"));
        var known = new HashSet<string> { "SHOP-1" };
        Assert.Equal("Fix [SHOP-1](../history/tickets/SHOP-1.md) und ABC-9", DocsService.Linkify("Fix SHOP-1 und ABC-9", "docs/knowledge/README.md", "docs", known));
        Assert.Equal("https://gitlab.firma.at/grp/app/-/commit/abc", DocsService.CommitUrl("git@gitlab.firma.at:grp/app.git", "abc"));
        Assert.Equal("https://github.com/o/r/commit/abc", DocsService.CommitUrl("https://github.com/o/r.git", "abc"));
    }

    [Fact]
    public void ScansModulesAndDependencies()
    {
        var repo = Directory.CreateTempSubdirectory("copit-scan").FullName;
        Directory.CreateDirectory(Path.Combine(repo, "src", "Core"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "App"));
        File.WriteAllText(Path.Combine(repo, "src", "Core", "Core.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(repo, "src", "Core", "Service.cs"), "namespace Shop.Core;\npublic class BookingService {}\ninternal record Item;");
        File.WriteAllText(Path.Combine(repo, "src", "App", "App.csproj"), "<Project><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /><PackageReference Include=\"Serilog\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(repo, "src", "App", "Main.cs"), "namespace Shop.App;\npublic static class Program {}");
        var mods = DocsService.Scan(repo);
        var app = mods.Single(m => m.Name == "App");
        var core = mods.Single(m => m.Name == "Core");
        Assert.Contains(core.Slug, app.DependsOn);
        Assert.Contains("Serilog", app.Packages);
        Assert.Equal(["BookingService", "Item"], core.Files.Single().Types);
        Assert.Equal("Shop.Core", core.Files.Single().Namespace);
        Assert.Equal("C#", core.Language);
    }
}
