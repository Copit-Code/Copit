using Copit.Core.Models;

namespace Copit.Core.Tests;

/// <summary>
/// Buchen wie im bewährten Buchungstool: normale Jira-Worklog-API, Tempo übernimmt den Account vom Ticket.
/// Die Tempo-API wird nur benutzt, wenn ein anderer Account als der des Tickets gebucht werden soll.
/// </summary>
public class WorklogBookingTests
{
    // Account-Feld nur über den Namen erkennbar (kein Tempo-Feldtyp) – wie im alten Tool
    private const string Fields = """
        [ { "id": "summary", "name": "Summary", "schema": { "type": "string", "system": "summary" } },
          { "id": "customfield_10500", "name": "Account", "schema": { "type": "string" } } ]
        """;

    private static (int, string) Route(string method, string path, string ticketAccount) => (method, path) switch
    {
        ("GET", var p) when p.StartsWith("/rest/api/2/field") => (200, Fields),
        ("GET", var p) when p.StartsWith("/rest/api/2/issue/ST-1?") =>
            (200, """{"key":"ST-1","fields":{"summary":"S","customfield_10500":ACC}}""".Replace("ACC", ticketAccount.Length > 0 ? $"\"{ticketAccount}\"" : "null")),
        ("GET", var p) when p.StartsWith("/rest/api/2/myself") => (200, """{"name":"fabi","key":"JIRAUSER1"}"""),
        ("GET", var p) when p.StartsWith("/rest/tempo-accounts/1/account") && !p.Contains("/key/") =>
            (200, """[{"id":"7","key":"ACC-7","name":"Kunde A","status":"OPEN"},{"id":"8","key":"ACC-8","name":"Intern","status":"OPEN"}]"""),
        ("POST", "/rest/api/2/issue/ST-1/worklog") => (201, """{"id":"5001"}"""),
        ("POST", "/rest/tempo-timesheets/4/worklogs/") => (200, """[{"tempoWorklogId":1,"attributes":{"_Account_":{"value":"ACC-8"}}}]"""),
        _ => (404, "{}"),
    };

    [Fact]
    public async Task AccountOfTicketIsBookedViaJiraWorklogApi()
    {
        using var jira = new FakeJira((m, p, b) => Route(m, p, "Kunde A"));
        var client = jira.Client(new JiraSettings { UseTempo = true });

        // gemerkt als Key, am Ticket steht der Name → derselbe Account
        var booked = await client.AddWorklogAsync("ST-1", new DateTime(2026, 11, 18, 9, 0, 0), 3600, "Arbeit", "ACC-7");

        Assert.Equal("Kunde A", booked);
        Assert.Contains(jira.Calls, c => c.StartsWith("POST /rest/api/2/issue/ST-1/worklog") && c.Contains("\"timeSpentSeconds\":3600"));
        Assert.DoesNotContain(jira.Calls, c => c.Contains("tempo-timesheets"));
    }

    [Fact]
    public async Task WithoutAccountJiraApiIsUsedEvenWithTempoEnabled()
    {
        using var jira = new FakeJira((m, p, b) => Route(m, p, "Kunde A"));
        var booked = await jira.Client(new JiraSettings { UseTempo = true }).AddWorklogAsync("ST-1", DateTime.Now, 1800, "x", null);

        Assert.Null(booked);
        Assert.Contains(jira.Calls, c => c.StartsWith("POST /rest/api/2/issue/ST-1/worklog"));
        Assert.DoesNotContain(jira.Calls, c => c.Contains("tempo-timesheets"));
    }

    [Fact]
    public async Task DifferentAccountGoesThroughTempoWithAccountKey()
    {
        using var jira = new FakeJira((m, p, b) => Route(m, p, ""));   // Ticket ohne Account, Account gemerkt
        var booked = await jira.Client().AddWorklogAsync("ST-1", DateTime.Now, 1800, "x", "Intern");

        Assert.Equal("ACC-8", booked);
        Assert.Contains(jira.Calls, c => c.StartsWith("POST /rest/tempo-timesheets/4/worklogs/") && c.Contains("\"value\":\"ACC-8\""));
        Assert.DoesNotContain(jira.Calls, c => c.StartsWith("POST /rest/api/2/issue/ST-1/worklog"));
    }

    [Fact]
    public async Task AccountFieldIsDetectedByNameWithoutSetting()
    {
        using var jira = new FakeJira((m, p, b) => Route(m, p, "Kunde A"));
        var issue = await jira.Client().GetIssueAsync("ST-1");
        Assert.Equal("Kunde A", issue?.Account);
        Assert.Contains(jira.Calls, c => c.Contains("fields=") && c.Contains("customfield_10500"));
    }
}
