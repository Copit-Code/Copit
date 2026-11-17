using System.Text.Json.Nodes;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

public class WorklogAccountTests
{
    private static JiraWorklog Log(string id, string key, string start, double hours) =>
        new() { Id = id, IssueKey = key, Started = DateTime.Parse(start), TimeSpentSeconds = (long)(hours * 3600) };

    [Fact]
    public void TempoAccountsAreMatchedByIdOrIssueStartAndDuration()
    {
        var logs = new List<JiraWorklog>
        {
            Log("101", "SHOP-1", "2026-11-16T08:00:00", 3),
            Log("102", "SHOP-2", "2026-11-16T11:00:00", 2),
            Log("103", "SHOP-3", "2026-11-16T13:00:00", 3),
            Log("104", "SHOP-4", "2026-11-16T16:00:00", 1),   // nicht in Tempo → unbekannt
        };
        var tempo = JsonNode.Parse("""
            [
              { "tempoWorklogId": 9001, "jiraWorklogId": 101, "issue": { "key": "SHOP-1" }, "timeSpentSeconds": 10800, "started": "2026-11-16 08:00:00.000",
                "attributes": { "_Account_": { "name": "Account", "value": "ACC-1" } } },
              { "tempoWorklogId": 9002, "jiraWorklogId": 102, "issue": { "key": "SHOP-2" }, "timeSpentSeconds": 7200, "started": "2026-11-16 11:00:00.000",
                "attributes": {} },
              { "tempoWorklogId": 9003, "issue": { "key": "SHOP-3" }, "timeSpentSeconds": 10800, "started": "2026-11-16 13:00:00.000",
                "attributes": { "_Account_": { "value": "ACC-3" } } }
            ]
            """)!.AsArray();

        JiraClient.ApplyTempoAccounts(logs, tempo, "_Account_");

        Assert.Equal(("ACC-1", true, "9001"), (logs[0].Account, logs[0].AccountChecked, logs[0].TempoId));
        Assert.Equal((null, true), (logs[1].Account, logs[1].AccountChecked));        // in Tempo, aber ohne Account
        Assert.Equal(("ACC-3", "9003"), (logs[2].Account, logs[2].TempoId));          // ohne Jira-ID: Ticket + Start + Dauer
        Assert.False(logs[3].AccountChecked);

        var h = TimeBookingService.Split(logs);
        Assert.Equal((6.0, 2.0, 1.0, 9.0), (h.WithAccount, h.WithoutAccount, h.Unknown, h.Total));
    }

    [Fact]
    public void SplitTreatsAllWithAccountAsGreen()
    {
        var h = TimeBookingService.Split([
            new JiraWorklog { TimeSpentSeconds = 5 * 3600, Account = "A", AccountChecked = true },
            new JiraWorklog { TimeSpentSeconds = 3 * 3600, Account = "B", AccountChecked = true },
        ]);
        Assert.Equal((8.0, 0.0, 0.0), (h.WithAccount, h.WithoutAccount, h.Unknown));
    }
}

public class CreateIssueFieldTests
{
    [Fact]
    public void FindsFieldsNotOnScreen()
    {
        var msg = """HTTP 400 Bad Request: {"errorMessages":[],"errors":{"description":"Field 'description' cannot be set. It is not on the appropriate screen, or unknown.","timetracking":"Field 'timetracking' cannot be set. It is not on the appropriate screen, or unknown.","summary":"You must specify a summary of the issue."}}""";
        Assert.Equal(["description", "timetracking"], JiraClient.UnsettableFields(msg));
        Assert.Empty(JiraClient.UnsettableFields("HTTP 400: kein JSON"));
        Assert.Empty(JiraClient.UnsettableFields("""HTTP 400: {"errors":{"summary":"required"}}"""));
    }
}

public class CreateIssueRetryTests
{
    [Fact]
    public async Task DropsFieldsNotOnScreenAndAddsDescriptionAsComment()
    {
        // Mini-Jira: Anlege-Maske ohne description, Bearbeiten-Maske ebenfalls ohne → Kommentar
        var port = new Random().Next(30000, 40000);
        using var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var calls = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                var ctx = await http.GetContextAsync();
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                calls.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} {body}");
                string reply; int code;
                if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.EndsWith("/issue") && body.Contains("\"description\""))
                { code = 400; reply = """{"errorMessages":[],"errors":{"description":"Field 'description' cannot be set. It is not on the appropriate screen, or unknown."}}"""; }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.EndsWith("/issue")) { code = 201; reply = """{"id":"1","key":"SHOP-9"}"""; }
                else if (ctx.Request.HttpMethod == "PUT") { code = 400; reply = """{"errors":{"description":"Field 'description' cannot be set."}}"""; }
                else { code = 201; reply = """{"id":"5"}"""; }
                ctx.Response.StatusCode = code;
                var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var jira = new JiraClient(new JiraSettings { BaseUrl = $"http://127.0.0.1:{port}" }, "token");
        var key = await jira.CreateIssueAsync("SHOP", "Neue Funktion", "Beschreibung mit Details", "Task", 0, null);
        await server;
        Assert.Equal("SHOP-9", key);
        Assert.Equal(4, calls.Count);
        Assert.DoesNotContain("\"description\"", calls[1]);                         // zweiter Versuch ohne Beschreibung
        Assert.StartsWith("PUT /rest/api/2/issue/SHOP-9", calls[2]);
        Assert.StartsWith("POST /rest/api/2/issue/SHOP-9/comment", calls[3]);
        Assert.Contains("Beschreibung mit Details", calls[3]);
        Assert.Contains(jira.LastCreateNotes, n => n.Contains("als Kommentar"));
    }
}

public class IssueTypeTests
{
    [Theory]
    [InlineData("10002", "id", "10002")]
    [InlineData("Aufgabe (10002)", "id", "10002")]
    [InlineData("Aufgabe · 10002", "id", "10002")]
    [InlineData("Aufgabe", "name", "Aufgabe")]
    [InlineData(" Task ", "name", "Task")]
    public void IssueTypeRefAcceptsNameOrId(string input, string key, string value)
    {
        var r = JiraClient.IssueTypeRef(input);
        Assert.Equal(value, r[key]!.ToString());
    }

    [Fact]
    public async Task UnknownTypeNameIsResolvedToIdFromProject()
    {
        var port = new Random().Next(40001, 50000);
        using var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var calls = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var ctx = await http.GetContextAsync();
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var path = ctx.Request.Url!.AbsolutePath;
                calls.Add($"{ctx.Request.HttpMethod} {path} {body}");
                (int code, string reply) = (ctx.Request.HttpMethod, path) switch
                {
                    ("POST", _) when body.Contains("\"name\":\"Task\"") => (400, """{"errorMessages":[],"errors":{"issuetype":"Geben Sie einen gültigen Vorgangstyp an"}}"""),
                    ("GET", var p) when p.Contains("/issuetypes") => (200, """{"values":[{"id":"10001","name":"Story","subtask":false},{"id":"10002","name":"Aufgabe","subtask":false},{"id":"10003","name":"Unteraufgabe","subtask":true}]}"""),
                    ("POST", _) => (201, """{"id":"7","key":"SHOP-7"}"""),
                    _ => (404, "{}"),
                };
                ctx.Response.StatusCode = code;
                var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var jira = new JiraClient(new JiraSettings { BaseUrl = $"http://127.0.0.1:{port}" }, "token");
        var key = await jira.CreateIssueAsync("SHOP", "Titel", "", "Task", 0, null);
        await server;
        Assert.Equal("SHOP-7", key);
        Assert.Contains("\"issuetype\":{\"id\":\"10002\"}", calls[2]);   // „Task“ → Aufgabe (10002)
        Assert.Contains(jira.LastCreateNotes, n => n.Contains("Aufgabe (10002)"));
    }
}
