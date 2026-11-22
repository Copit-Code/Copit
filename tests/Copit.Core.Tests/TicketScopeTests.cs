using System.Text.Json.Nodes;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

/// <summary>Mehrere Projekte teilen sich ein Epic (z. B. StationService und ProductionCollector in GeneralDevelopment).</summary>
public class TicketScopeTests
{
    private static readonly CopitProject Station = new() { Id = "ss", Name = "StationService", EpicKey = "GD-1" };
    private static readonly CopitProject Collector = new() { Id = "pc", Name = "ProductionCollector", EpicKey = "gd-1 " };
    private static readonly CopitProject Other = new() { Id = "x", Name = "Shop", EpicKey = "SHOP-1" };
    private static readonly List<CopitProject> All = [Station, Collector, Other];

    private static Dictionary<string, ISet<string>> Sets(params (string Id, string[] Keys)[] x) =>
        x.ToDictionary(e => e.Id, e => (ISet<string>)e.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static TicketOwner Of(string key, CopitProject p, Dictionary<string, string>? assign = null,
        Dictionary<string, ISet<string>>? jira = null, Dictionary<string, ISet<string>>? repo = null) =>
        TicketScope.Classify(key, p, TicketScope.Siblings(All, p), assign ?? [], jira ?? [], repo ?? []);

    [Fact]
    public void SiblingsShareTheEpicIgnoringCaseAndSpaces()
    {
        Assert.Equal(["ss", "pc"], TicketScope.Siblings(All, Station).Select(p => p.Id));
        Assert.Single(TicketScope.Siblings(All, Other));
    }

    [Fact]
    public void ProjectWithOwnEpicAndNoFilterSeesEverything()
    {
        Assert.False(TicketScope.NeedsScoping(Other, TicketScope.Siblings(All, Other)));
        Assert.Equal(TicketOwner.Own, Of("SHOP-7", Other));
    }

    [Fact]
    public void ManualAssignmentWinsOverEverything()
    {
        var assign = new Dictionary<string, string> { ["GD-5"] = "pc" };
        var jira = Sets(("ss", ["GD-5"]));
        Assert.Equal(TicketOwner.Other, Of("GD-5", Station, assign, jira));
        Assert.Equal(TicketOwner.Own, Of("GD-5", Collector, assign, jira));
    }

    [Fact]
    public void AssignmentToUnknownProjectIsIgnored()
    {
        var assign = new Dictionary<string, string> { ["GD-5"] = "deleted" };
        Assert.Equal(TicketOwner.Unassigned, Of("GD-5", Station, assign));
    }

    [Fact]
    public void JiraFilterDecidesBeforeCode()
    {
        var jira = Sets(("pc", ["GD-2"]));
        var repo = Sets(("ss", ["GD-2"]));
        Assert.Equal(TicketOwner.Other, Of("GD-2", Station, jira: jira, repo: repo));
        Assert.Equal(TicketOwner.Own, Of("GD-2", Collector, jira: jira, repo: repo));
    }

    [Fact]
    public void CodeInRepoAssignsWhenUnambiguous()
    {
        var repo = Sets(("ss", ["GD-3", "GD-4"]), ("pc", ["GD-4"]));
        Assert.Equal(TicketOwner.Own, Of("GD-3", Station, repo: repo));
        Assert.Equal(TicketOwner.Other, Of("GD-3", Collector, repo: repo));
        Assert.Equal(TicketOwner.Unassigned, Of("GD-4", Station, repo: repo));   // in beiden Repos → unklar
        Assert.Equal(TicketOwner.Unassigned, Of("GD-9", Station, repo: repo));   // nirgends
    }

    [Fact]
    public void SingleProjectWithFilterHidesNonMatching()
    {
        var solo = new CopitProject { Id = "solo", EpicKey = "AB-1", TicketComponent = "Api" };
        var sib = TicketScope.Siblings([solo], solo);
        Assert.True(TicketScope.NeedsScoping(solo, sib));
        Assert.Equal(TicketOwner.Own, TicketScope.Classify("AB-2", solo, sib, new Dictionary<string, string>(), Sets(("solo", ["AB-2"])), Sets()));
        Assert.Equal(TicketOwner.Other, TicketScope.Classify("AB-3", solo, sib, new Dictionary<string, string>(), Sets(("solo", ["AB-2"])), Sets()));
    }

    [Fact]
    public void FilterJqlCombinesComponentLabelAndJql()
    {
        Assert.Null(TicketScope.FilterJql(Station));
        var p = new CopitProject { TicketComponent = "Production \"Collector\"", TicketLabel = "pc", TicketJql = "summary ~ x" };
        Assert.Equal("component = \"Production \\\"Collector\\\"\" AND labels = \"pc\" AND (summary ~ x)", TicketScope.FilterJql(p));
    }

    [Fact]
    public void NewTicketsGetComponentAndLabel()
    {
        var fields = new Dictionary<string, JsonNode?>();
        TicketScope.AddCreateFields(new CopitProject { TicketComponent = "StationService", TicketLabel = "station service" }, fields);
        Assert.Equal("""[{"name":"StationService"}]""", fields["components"]!.ToJsonString());
        Assert.Equal("""["station_service"]""", fields["labels"]!.ToJsonString());
    }

    [Fact]
    public async Task EpicQueryIsCombinedWithProjectFilter()
    {
        using var jira = new FakeJira((m, p, b) => p.StartsWith("/rest/api/2/search") ? (200, """{"issues":[],"total":0}""") : (404, "{}"));
        await jira.Client().GetEpicIssuesAsync("GD-1", default, "component = \"StationService\"");
        Assert.Contains(jira.Calls, c => c.Contains("OR parent = \\u0022GD-1\\u0022) AND (component = \\u0022StationService\\u0022) ORDER BY") || c.Contains("OR parent = \\\"GD-1\\\") AND (component = \\\"StationService\\\") ORDER BY"));
    }
}
