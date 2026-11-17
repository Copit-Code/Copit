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
