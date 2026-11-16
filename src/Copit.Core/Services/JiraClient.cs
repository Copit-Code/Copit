using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Copit.Core.Models;

namespace Copit.Core.Services;

/// <summary>REST-Client für ein selbst gehostetes Jira (Server / Data Center, API v2) inkl. optional Tempo.</summary>
public class JiraClient
{
    private readonly HttpClient _http;
    private readonly JiraSettings _s;
    private JsonNode? _myself;

    public string BaseUrl { get; }

    public JiraClient(JiraSettings settings, string token)
    {
        _s = settings;
        BaseUrl = settings.BaseUrl.TrimEnd('/');
        _http = HttpFactory.Create(BaseUrl, settings.IgnoreCertificateErrors);
        if (settings.AuthMode == JiraAuthMode.Basic)
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{token}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        else
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public string IssueUrl(string key) => $"{BaseUrl}/browse/{key}";

    private async Task<JsonNode?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        return await HttpFactory.ReadJsonAsync(resp, ct);
    }

    private async Task<JsonNode?> PostAsync(string url, object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(url, body, ct);
        return await HttpFactory.ReadJsonAsync(resp, ct);
    }

    public async Task<(string Name, string Key, string DisplayName)> GetMyselfAsync(CancellationToken ct = default)
    {
        _myself ??= await GetAsync("rest/api/2/myself", ct);
        return (_myself.Str("name"), _myself.Str("key"), _myself.Str("displayName"));
    }

    private string[] Fields =>
    [
        "summary", "status", "issuetype", "assignee", "priority", "updated", "description",
        "timeoriginalestimate", "timespent", "timeestimate", "aggregatetimespent",
        .. string.IsNullOrWhiteSpace(_s.AccountFieldId) ? Array.Empty<string>() : [_s.AccountFieldId]
    ];

    public async Task<List<JiraIssue>> SearchAsync(string jql, int max = 300, CancellationToken ct = default)
    {
        var result = new List<JiraIssue>();
        int startAt = 0;
        while (result.Count < max)
        {
            var body = new { jql, startAt, maxResults = Math.Min(100, max - result.Count), fields = Fields };
            var node = await PostAsync("rest/api/2/search", body, ct);
            var issues = node?["issues"]?.AsArray();
            if (issues == null || issues.Count == 0) break;
            result.AddRange(issues.Select(Map));
            startAt += issues.Count;
            if (startAt >= node.Long("total")) break;
        }
        return result;
    }

    public async Task<JiraIssue?> GetIssueAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var node = await GetAsync($"rest/api/2/issue/{Uri.EscapeDataString(key)}?fields={string.Join(',', Fields)}", ct);
            return node == null ? null : Map(node);
        }
        catch (ApiException e) when (e.StatusCode == 404) { return null; }
    }

    public async Task<List<JiraIssue>> GetEpicIssuesAsync(string epicKey, CancellationToken ct = default)
    {
        var epic = $"\"{epicKey}\"";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_s.EpicLinkFieldId))
            candidates.Add($"(cf[{_s.EpicLinkFieldId.Replace("customfield_", "")}] = {epic} OR parent = {epic}) ORDER BY updated DESC");
        candidates.Add($"(\"Epic Link\" = {epic} OR parent = {epic}) ORDER BY updated DESC");
        candidates.Add($"parent = {epic} ORDER BY updated DESC");

        ApiException? last = null;
        foreach (var jql in candidates)
        {
            try
            {
                var issues = await SearchAsync(jql, 500, ct);
                // Sub-Tasks der Epic-Tickets dazuholen
                if (issues.Count > 0)
                {
                    var keys = string.Join(",", issues.Take(80).Select(i => i.Key));
                    try { issues.AddRange(await SearchAsync($"parent in ({keys})", 300, ct)); } catch { }
                }
                return issues.DistinctBy(i => i.Key).ToList();
            }
            catch (ApiException e) when (e.StatusCode == 400) { last = e; }
        }
        throw last ?? new ApiException("Epic konnte nicht geladen werden.");
    }

    private JiraIssue Map(JsonNode? n)
    {
        var f = n?["fields"];
        var issue = new JiraIssue
        {
            Key = n.Str("key"),
            Summary = f.Str("summary"),
            Status = f?["status"].Str("name") ?? "",
            StatusCategory = f?["status"]?["statusCategory"].Str("key") ?? "",
            IssueType = f?["issuetype"].Str("name") ?? "",
            Assignee = f?["assignee"].Str("displayName") ?? "",
            Priority = f?["priority"].Str("name") ?? "",
            OriginalEstimateSeconds = f.Long("timeoriginalestimate"),
            TimeSpentSeconds = Math.Max(f.Long("timespent"), f.Long("aggregatetimespent")),
            RemainingEstimateSeconds = f.Long("timeestimate"),
            Description = f.Str("description"),
        };
        issue.Url = IssueUrl(issue.Key);
        if (DateTime.TryParse(f.Str("updated"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var upd)) issue.Updated = upd;
        if (!string.IsNullOrWhiteSpace(_s.AccountFieldId)) issue.Account = ReadAccount(f?[_s.AccountFieldId]);
        return issue;
    }

    /// <summary>Account-Feld kann Objekt (Tempo), Option, String oder Zahl sein.</summary>
    internal static string? ReadAccount(JsonNode? v)
    {
        if (v == null) return null;
        if (v is JsonValue) { var s = v.ToString(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        if (v is JsonArray arr) return arr.Count > 0 ? ReadAccount(arr[0]) : null;
        foreach (var p in new[] { "key", "value", "name", "id" })
        {
            var s = v.Str(p);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    // ---------------- Worklogs ----------------

    public async Task<List<JiraWorklog>> GetMyWorklogsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var me = await GetMyselfAsync(ct);
        var jql = $"worklogAuthor = currentUser() AND worklogDate >= \"{from:yyyy-MM-dd}\" AND worklogDate <= \"{to:yyyy-MM-dd}\"";
        var issues = await SearchAsync(jql, 300, ct);
        var result = new List<JiraWorklog>();
        using var gate = new SemaphoreSlim(6);
        var tasks = issues.Select(async issue =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var node = await GetAsync($"rest/api/2/issue/{issue.Key}/worklog", ct);
                foreach (var w in node?["worklogs"]?.AsArray() ?? [])
                {
                    var author = w?["author"];
                    var isMe = author.Str("name") == me.Name || author.Str("key") == me.Key;
                    if (!isMe) continue;
                    if (!DateTimeOffset.TryParse(w.Str("started"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) continue;
                    var d = DateOnly.FromDateTime(started.LocalDateTime);
                    if (d < from || d > to) continue;
                    lock (result)
                        result.Add(new JiraWorklog
                        {
                            Id = w.Str("id"),
                            IssueKey = issue.Key,
                            IssueSummary = issue.Summary,
                            Started = started.LocalDateTime,
                            TimeSpentSeconds = w.Long("timeSpentSeconds"),
                            Comment = w.Str("comment")
                        });
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return result.OrderBy(w => w.Started).ToList();
    }

    public static string FormatJiraDate(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{local:yyyy-MM-dd'T'HH:mm:ss}.000{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";
    }

    public async Task AddWorklogAsync(string issueKey, DateTime start, long seconds, string comment, string? account, CancellationToken ct = default)
    {
        if (_s.UseTempo)
        {
            var me = await GetMyselfAsync(ct);
            var attributes = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(account))
                attributes[_s.TempoAccountAttributeKey] = new { value = account };
            var body = new
            {
                worker = string.IsNullOrEmpty(me.Key) ? me.Name : me.Key,
                originTaskId = issueKey,
                started = start.ToString("yyyy-MM-dd'T'HH:mm:ss.000"),
                timeSpentSeconds = seconds,
                billableSeconds = seconds,
                comment,
                attributes
            };
            await PostAsync("rest/tempo-timesheets/4/worklogs/", body, ct);
            return;
        }

        var wl = new { comment, started = FormatJiraDate(start), timeSpentSeconds = seconds };
        await PostAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/worklog", wl, ct);
    }

    public async Task<List<TempoAccount>> GetTempoAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await GetAsync("rest/tempo-accounts/1/account", ct);
            return (node?.AsArray() ?? [])
                .Where(a => a.Str("status") is "" or "OPEN")
                .Select(a => new TempoAccount(a.Str("key"), a.Str("name")))
                .Where(a => a.Key.Length > 0)
                .OrderBy(a => a.Name).ToList();
        }
        catch { return []; }
    }

    // ---------------- Tickets erstellen ----------------

    public async Task<string> CreateIssueAsync(string projectKey, string summary, string description, string issueType,
        double estimateHours, string? epicKey, CancellationToken ct = default)
    {
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["summary"] = summary,
            ["description"] = description,
            ["issuetype"] = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(issueType) ? _s.DefaultIssueType : issueType },
        };
        if (estimateHours > 0)
            fields["timetracking"] = new JsonObject { ["originalEstimate"] = FormatEstimate(estimateHours) };
        if (!string.IsNullOrWhiteSpace(epicKey))
        {
            if (!string.IsNullOrWhiteSpace(_s.EpicLinkFieldId)) fields[_s.EpicLinkFieldId] = epicKey;
            else fields["parent"] = new JsonObject { ["key"] = epicKey };
        }
        var node = await PostAsync("rest/api/2/issue", new JsonObject { ["fields"] = fields }, ct);
        return node.Str("key");
    }

    public static string FormatEstimate(double hours)
    {
        var totalMin = (int)Math.Round(hours * 60);
        var h = totalMin / 60; var m = totalMin % 60;
        return m == 0 ? $"{h}h" : h == 0 ? $"{m}m" : $"{h}h {m}m";
    }

    public async Task AddCommentAsync(string issueKey, string body, CancellationToken ct = default)
        => await PostAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment", new { body }, ct);
}
