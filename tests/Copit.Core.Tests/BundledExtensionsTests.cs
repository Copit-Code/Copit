using Copit.Core.Services;

namespace Copit.Core.Tests;

/// <summary>Die mitgelieferten Extensions unter /extensions müssen gültige Manifeste haben.</summary>
public class BundledExtensionsTests
{
    private static string Dir(string id)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "extensions"))) d = d.Parent;
        return Path.Combine(d!.FullName, "extensions", id);
    }

    [Theory]
    [InlineData("copit-dotnet")]
    [InlineData("copit-obsidian")]
    [InlineData("copit-mcp")]
    public void ManifestIsValid(string id)
    {
        var m = ExtensionService.ReadManifest(Dir(id));
        Assert.Equal(id, m.Id);
        foreach (var a in m.Actions)
        {
            var script = System.Text.RegularExpressions.Regex.Match(a.Run, @"\{ext\}/([^""\s]+)").Groups[1].Value;
            Assert.True(File.Exists(Path.Combine(Dir(id), script)), $"Skript fehlt: {script}");
        }
        foreach (var au in m.Automations)
            Assert.All(au.Actions, x => Assert.Contains(m.Actions, a => x.Type == $"ext:{id}/{a.Id}"));
        Assert.All(m.Automations, au => Assert.Contains(AutomationService.Triggers, t => t.Type == au.Trigger));
    }

    [Fact]
    public void TemplatePlaceholdersAreFilled()
    {
        var m = ExtensionService.ReadManifest(Dir("copit-obsidian"));
        var t = m.AgentTemplates.Single(x => x.Id == "vault-context");
        var prompt = ExtensionService.FillTemplate(t.Prompt, "shop-1", "Shop", "", @"C:\Users\me\AppData\Local\Copit\extensions\copit-obsidian");
        Assert.Contains("C:/Users/me/AppData/Local/Copit/extensions/copit-obsidian/scripts/context.ps1", prompt);
        Assert.Contains("-Ticket \"SHOP-1\"", prompt);
        Assert.DoesNotContain("{{", prompt);
        var tool = ExtensionService.FillTemplate(t.AllowedTools[0], "SHOP-1", "Shop", "", "/x/ext/");
        Assert.Equal("Bash(powershell -NoProfile -ExecutionPolicy Bypass -File \"/x/ext/scripts/context.ps1\":*)", tool);
    }
}
