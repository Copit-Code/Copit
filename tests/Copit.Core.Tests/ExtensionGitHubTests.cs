using Copit.Core.Services;
using Copit.Core.Storage;

namespace Copit.Core.Tests;

/// <summary>Lädt die Beispiel-Extension echt aus GitHub (öffentliches Repo). Nur mit COPIT_IT=1.</summary>
public class ExtensionGitHubTests
{
    [Fact]
    public async Task InstallsExampleFromPublicRepo()
    {
        if (Environment.GetEnvironmentVariable("COPIT_IT") != "1") return;
        AppPaths.Root = Directory.CreateTempSubdirectory("copit-ext-home").FullName;
        var settings = new SettingsService(new PlainProtector());
        var data = new DataService();
        var ext = new ExtensionService(data, settings);
        var p = await ext.PrepareAsync("Copit-Code/Copit/extensions/copit-dotnet");
        Assert.Equal("copit-dotnet", p.Manifest.Id);
        Assert.False(p.Release);
        Assert.Equal(40, p.Revision.Length);
        var installed = await ext.CommitAsync(p.Manifest, p.StagedDir, "Copit-Code/Copit/extensions/copit-dotnet", false, p.Ref, p.Revision, p.Release, new PermissionService(), false);
        Assert.True(File.Exists(Path.Combine(installed.Dir, "scripts", "test.ps1")));
        Assert.Contains(data.Automations.Value.Automations, a => a.Id == "ext:copit-dotnet:test-after-agent" && !a.Enabled);
        Assert.Contains(ext.ActionDefs, a => a.Type == "ext:copit-dotnet/test");
        Assert.Equal(0, await ext.CheckUpdatesAsync()); // gleicher Commit → kein Update
    }
}
