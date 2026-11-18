using System.Net;
using System.Text.Json.Nodes;
using Copit.Core.Models;
using Copit.Core.Services;

namespace Copit.Core.Tests;

/// <summary>Minimaler Jira-Server für Tests: beantwortet Anfragen über eine Routing-Funktion und protokolliert sie.</summary>
internal sealed class FakeJira : IDisposable
{
    private HttpListener _http = new();
    private readonly CancellationTokenSource _cts = new();
    public List<string> Calls { get; } = [];
    public string Url { get; private set; } = "";

    public FakeJira(Func<string, string, string, (int Code, string Body)> route)
    {
        // Freien Port suchen: unter Windows sind Teile des Bereichs reserviert oder von parallelen Tests belegt
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 45000);
            Url = $"http://127.0.0.1:{port}";
            _http = new HttpListener();   // nach fehlgeschlagenem Start() ist eine Instanz unter Windows unbrauchbar
            _http.Prefixes.Add(Url + "/");
            try { _http.Start(); break; }
            catch (HttpListenerException) when (attempt < 30) { _http.Close(); }
        }
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var path = ctx.Request.Url!.PathAndQuery;
                lock (Calls) Calls.Add($"{ctx.Request.HttpMethod} {path} {body}");
                var (code, reply) = route(ctx.Request.HttpMethod, path, body);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(reply);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public JiraClient Client(JiraSettings? s = null)
    {
        s ??= new JiraSettings();
        s.BaseUrl = Url;
        return new JiraClient(s, "token");
    }

    public void Dispose() { _cts.Cancel(); _http.Stop(); }
}

public class JiraCreateTests
{
    private const string Fields = """
        [ { "id": "summary", "name": "Summary", "schema": { "type": "string", "system": "summary" } },
          { "id": "customfield_10101", "name": "Epic Link", "schema": { "type": "any", "custom": "com.pyxis.greenhopper.jira:gh-epic-link" } },
          { "id": "customfield_10401", "name": "Account", "schema": { "type": "option2", "custom": "com.tempoplugin.tempo-accounts:accounts.customfield" } } ]
        """;

    [Fact]
    public async Task EpicIsLinkedViaDetectedEpicLinkFieldInsteadOfParent()
    {
        using var jira = new FakeJira((method, path, body) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/field") => (200, Fields),
            ("POST", "/rest/api/2/issue") when body.Contains("\"customfield_10101\":\"ST-381\"") => (201, """{"key":"ST-999"}"""),
            ("POST", "/rest/api/2/issue") => (400, """{"errors":{"issuetype":"Issue type is not a sub-task but parent issue key or id specified."}}"""),
            _ => (404, "{}"),
        });
        var key = await jira.Client().CreateIssueAsync("ST", "Titel", "", "10100", 0, "ST-381");
        Assert.Equal("ST-999", key);
        // Ohne Einstellung wird das Feld erkannt und gleich verwendet – kein parent
        Assert.DoesNotContain(jira.Calls, c => c.Contains("\"parent\""));
    }

    [Fact]
    public async Task ParentRejectedFallsBackToEpicLinkField()
    {
        // Feld-Erkennung schlägt zunächst fehl (z. B. keine Rechte auf /field) → parent; Jira lehnt ab → Epic Link aus Einstellung
        var fieldCalls = 0;
        using var jira = new FakeJira((method, path, body) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/field") => ++fieldCalls == 1 ? (500, "{}") : (200, Fields),
            ("POST", "/rest/api/2/issue") when body.Contains("\"parent\"") => (400, """{"errors":{"issuetype":"Issue type is not a sub-task but parent issue key or id specified."}}"""),
            ("POST", "/rest/api/2/issue") when body.Contains("customfield_10101") => (201, """{"key":"ST-1000"}"""),
            _ => (404, "{}"),
        });
        var client = jira.Client();
        var key = await client.CreateIssueAsync("ST", "Titel", "", "10100", 0, "ST-381");
        Assert.Equal("ST-1000", key);
        Assert.Contains(client.LastCreateNotes, n => n.Contains("customfield_10101"));
    }

    [Fact]
    public async Task ErrorShowsJirasOriginalText()
    {
        using var jira = new FakeJira((method, path, _) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/field") => (200, "[]"),
            ("POST", "/rest/api/2/issue") => (400, """{"errorMessages":["Sie sind nicht berechtigt."],"errors":{"customfield_11501":"Availability ist erforderlich."}}"""),
            _ => (404, "{}"),
        });
        var e = await Assert.ThrowsAsync<ApiException>(() => jira.Client().CreateIssueAsync("ST", "Titel", "", "10100", 0, null));
        Assert.Contains("Sie sind nicht berechtigt.", e.Message);
        Assert.Contains("customfield_11501: Availability ist erforderlich.", e.Message);
    }

    [Fact]
    public void ParsesRequiredFieldsAndBuildsValues()
    {
        var meta = JsonNode.Parse("""
            { "values": [
              { "fieldId": "summary", "name": "Summary", "required": true, "hasDefaultValue": false, "schema": { "type": "string" } },
              { "fieldId": "customfield_11501", "name": "Availability", "required": true, "hasDefaultValue": false,
                "schema": { "type": "array", "items": "option", "custom": "com.atlassian.jira.plugin.system.customfieldtypes:multicheckboxes" },
                "allowedValues": [ { "id": "11100", "value": "Needed for market start" }, { "id": "11101", "value": "Later" } ] },
              { "fieldId": "customfield_11713", "name": "Licensing", "required": true, "hasDefaultValue": false,
                "schema": { "type": "option" }, "allowedValues": [ { "id": "11323", "value": "No" }, { "id": "11324", "value": "Yes" } ] },
              { "fieldId": "customfield_10801", "name": "Test Priority", "required": true, "hasDefaultValue": true, "schema": { "type": "option" } },
              { "fieldId": "customfield_10101", "name": "Epic Link", "required": true, "hasDefaultValue": false, "schema": { "type": "any" } }
            ] }
            """)!["values"]!.AsArray();
        var fields = meta.Select(v => JiraClient.ParseCreateField(v!["fieldId"]!.ToString(), v)).ToList();
        var missing = JiraClient.MissingRequired(fields, new JiraFieldIds("customfield_10101", null, null));
        Assert.Equal(["Availability", "Licensing"], missing.Select(f => f.Name));   // summary/Epic Link/Default ausgenommen

        Assert.Equal("""[{"id":"11100"}]""", JiraClient.FieldValue(missing[0], "Needed for market start")!.ToJsonString());
        Assert.Equal("""{"id":"11323"}""", JiraClient.FieldValue(missing[1], "no")!.ToJsonString());
        Assert.Null(JiraClient.FieldValue(missing[1], "Vielleicht"));
    }

    [Fact]
    public async Task CheckReportsMissingPermissionAndRequiredFields()
    {
        using var jira = new FakeJira((method, path, _) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/mypermissions") => (200, """{"permissions":{"CREATE_ISSUES":{"havePermission":false}}}"""),
            ("GET", var p) when p.StartsWith("/rest/api/2/field") => (200, Fields),
            ("GET", var p) when p.StartsWith("/rest/api/2/issue/createmeta/ST/issuetypes/10100") =>
                (200, """{"values":[{"fieldId":"customfield_11713","name":"Licensing","required":true,"hasDefaultValue":false,"schema":{"type":"option"},"allowedValues":[{"id":"11323","value":"No"}]}]}"""),
            ("GET", var p) when p.StartsWith("/rest/api/2/issue/createmeta/ST/issuetypes") => (200, """{"values":[{"id":"10100","name":"Task","subtask":false}]}"""),
            _ => (404, "{}"),
        });
        var check = await jira.Client().CheckCreateAsync("ST", "Task · 10100");
        Assert.False(check.CanCreate);
        Assert.Equal("10100", check.Type?.Id);
        Assert.Equal("customfield_10101", check.Fields.EpicLink);
        Assert.Equal("customfield_10401", check.Fields.Account);
        Assert.Equal(["Licensing"], check.Required.Select(f => f.Name));
        Assert.Contains(check.Problems, p => p.Contains("Keine Berechtigung"));
    }

    [Fact]
    public void ErrorTextIsReadable()
    {
        Assert.Equal("issuetype: kaputt", JiraClient.JiraErrorText("""HTTP 400 Bad Request: {"errorMessages":[],"errors":{"issuetype":"kaputt"}}"""));
        Assert.Equal("kein JSON", JiraClient.JiraErrorText("kein JSON"));
    }
}

public class TempoAccountFieldTests
{
    private const string Accounts = """
        [ { "id": 140896, "key": "100_26980_3800_140896", "name": "26980_Kostenstellenaufträge 2026 - 1.2.72 - 5802_MAN Components THT/ATS", "status": "OPEN" },
          { "id": 2, "key": "OTHER", "name": "Anderes", "status": "OPEN" } ]
        """;

    private static FakeJira Jira() => new((method, path, body) => (method, path) switch
    {
        ("GET", var p) when p.StartsWith("/rest/api/2/field") => (200, "[]"),
        ("GET", var p) when p.StartsWith("/rest/tempo-accounts/1/account/key/") => (404, "{}"),
        ("GET", var p) when p.StartsWith("/rest/tempo-accounts/1/account") => (200, Accounts),
        // Tempo verlangt die ID – Key/Name führen zu „Account id is null“
        ("POST", "/rest/api/2/issue") when body.Contains("\"customfield_10401\":140896") => (201, """{"key":"ST-1001"}"""),
        ("POST", "/rest/api/2/issue") => (400, """{"errorMessages":["Account id is null"],"errors":{}}"""),
        ("GET", var p) when p.StartsWith("/rest/api/2/issue/ST-1001") => (200, """{"key":"ST-1001","fields":{"summary":"x","customfield_10401":{"id":140896,"key":"100_26980_3800_140896","value":"26980_Kostenstellenaufträge 2026"}}}"""),
        _ => (404, "{}"),
    });

    [Theory]
    [InlineData("100_26980_3800_140896")]                                                     // Key
    [InlineData("26980_Kostenstellenaufträge 2026 - 1.2.72 - 5802_MAN Components THT/ATS")]  // Anzeige-Name (z. B. vom Epic gelesen)
    [InlineData("140896")]                                                                    // ID
    public async Task AccountIsSentAsTempoId(string account)
    {
        using var jira = Jira();
        var key = await jira.Client(new JiraSettings { AccountFieldId = "customfield_10401" })
            .CreateIssueAsync("ST", "Titel", "", "10100", 0, null, default, account);
        Assert.Equal("ST-1001", key);
    }

    [Fact]
    public async Task UnknownAccountGivesClearMessage()
    {
        using var jira = Jira();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => jira.Client(new JiraSettings { AccountFieldId = "customfield_10401" })
            .CreateIssueAsync("ST", "Titel", "", "10100", 0, null, default, "GIBTSNICHT"));
        Assert.Contains("nicht gefunden", e.Message);
        Assert.DoesNotContain(jira.Calls, c => c.StartsWith("POST /rest/api/2/issue"));
    }
}

public class TempoWorklogAccountTests
{
    [Fact]
    public async Task WorklogUsesTempoAccountKeyEvenIfTicketShowsName()
    {
        const string accounts = """[ { "id": 140896, "key": "100_26980_3800_140896", "name": "26980_Kostenstellenaufträge 2026", "status": "OPEN" } ]""";
        using var jira = new FakeJira((method, path, body) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/myself") => (200, """{"key":"JIRAUSER1","name":"fabian"}"""),
            ("GET", var p) when p.StartsWith("/rest/tempo-accounts/1/account") => (200, accounts),
            // Tempo kennt nur den Key – mit dem Namen kommt „account … null“
            ("POST", "/rest/tempo-timesheets/4/worklogs/") when body.Contains("\"value\":\"100_26980_3800_140896\"") =>
                (200, """[{"tempoWorklogId":7,"attributes":{"_Account_":{"value":"100_26980_3800_140896"}}}]"""),
            ("POST", "/rest/tempo-timesheets/4/worklogs/") => (400, """{"errors":{"_Account_":"Account id is null"}}"""),
            _ => (404, "{}"),
        });
        var client = jira.Client(new JiraSettings { UseTempo = true });
        var booked = await client.AddWorklogAsync("ST-1", DateTime.Today.AddHours(9), 3600, "Test", "26980_Kostenstellenaufträge 2026");
        Assert.Equal("100_26980_3800_140896", booked);
    }

    [Fact]
    public async Task TempoErrorIsShownReadable()
    {
        using var jira = new FakeJira((method, path, _) => (method, path) switch
        {
            ("GET", var p) when p.StartsWith("/rest/api/2/myself") => (200, """{"key":"JIRAUSER1","name":"fabian"}"""),
            ("GET", var p) when p.StartsWith("/rest/tempo-accounts/1/account") => (200, "[]"),
            ("POST", "/rest/tempo-timesheets/4/worklogs/") => (400, """{"errors":{"_Account_":"Account id is null"}}"""),
            _ => (404, "{}"),
        });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jira.Client(new JiraSettings { UseTempo = true }).AddWorklogAsync("ST-1", DateTime.Today, 3600, "x", "GIBTSNICHT"));
        Assert.Contains("Tempo lehnt die Buchung auf ST-1 ab", e.Message);
        Assert.Contains("Account id is null", e.Message);
    }
}
