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

    private async Task<JsonNode?> SendJsonAsync(HttpMethod method, string url, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var resp = await _http.SendAsync(req, ct);
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
                    var accountField = !string.IsNullOrWhiteSpace(_s.AccountFieldId);
                    lock (result)
                        result.Add(new JiraWorklog
                        {
                            Id = w.Str("id"),
                            IssueKey = issue.Key,
                            IssueSummary = issue.Summary,
                            Started = started.LocalDateTime,
                            TimeSpentSeconds = w.Long("timeSpentSeconds"),
                            Comment = w.Str("comment"),
                            // ohne Tempo hängt der Account am Ticket (Account-Feld)
                            Account = accountField ? issue.Account : null,
                            AccountChecked = accountField && !_s.UseTempo,
                        });
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (_s.UseTempo && result.Count > 0)
        {
            try { ApplyTempoAccounts(result, await GetTempoWorklogsAsync(from, to, me, ct), _s.TempoAccountAttributeKey); }
            catch { /* Tempo nicht erreichbar → Accounts bleiben „unbekannt“ */ }
        }
        return result.OrderBy(w => w.Started).ToList();
    }

    /// <summary>Eigene Worklogs aus Tempo (enthalten die Attribute, also den Account).</summary>
    private async Task<JsonArray> GetTempoWorklogsAsync(DateOnly from, DateOnly to, (string Name, string Key, string DisplayName) me, CancellationToken ct)
    {
        var worker = string.IsNullOrEmpty(me.Key) ? me.Name : me.Key;
        var res = await PostAsync("rest/tempo-timesheets/4/worklogs/search", new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd"), worker = new[] { worker } }, ct);
        return res as JsonArray ?? [];
    }

    /// <summary>Ordnet Tempo-Worklogs (per Jira-Worklog-ID, sonst Ticket + Start + Dauer) den Jira-Worklogs zu und übernimmt den Account.</summary>
    public static void ApplyTempoAccounts(List<JiraWorklog> logs, JsonArray tempo, string attributeKey)
    {
        foreach (var t in tempo)
        {
            if (t == null) continue;
            var jiraId = t["jiraWorklogId"]?.ToString() ?? t["originId"]?.ToString();
            var key = t["issue"]?["key"]?.ToString() ?? t["originTaskId"]?.ToString();
            var secs = t["timeSpentSeconds"]?.GetValue<long>() ?? 0;
            DateTime.TryParse(t["started"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started);
            var w = logs.FirstOrDefault(l => jiraId != null && l.Id == jiraId)
                    ?? logs.FirstOrDefault(l => !l.AccountChecked && string.Equals(l.IssueKey, key, StringComparison.OrdinalIgnoreCase)
                                                && l.TimeSpentSeconds == secs && Math.Abs((l.Started - started).TotalMinutes) < 1);
            if (w == null) continue;
            var attr = t["attributes"]?[attributeKey];
            w.Account = attr == null ? null : ReadAccount(attr["value"] ?? attr);
            w.AccountChecked = true;
            w.TempoId = t["tempoWorklogId"]?.ToString() ?? t["id"]?.ToString();
        }
    }

    /// <summary>Trägt den Account an einem bestehenden Tempo-Worklog nach und prüft, ob Tempo ihn übernommen hat.</summary>
    public async Task<string> SetWorklogAccountAsync(JiraWorklog w, string account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(w.TempoId)) throw new InvalidOperationException("Diese Buchung ist in Tempo nicht bekannt – Account bitte direkt in Tempo nachtragen.");
        var me = await GetMyselfAsync(ct);
        var body = new
        {
            worker = string.IsNullOrEmpty(me.Key) ? me.Name : me.Key,
            originTaskId = w.IssueKey,
            started = w.Started.ToString("yyyy-MM-dd'T'HH:mm:ss.000"),
            timeSpentSeconds = w.TimeSpentSeconds,
            billableSeconds = w.TimeSpentSeconds,
            comment = w.Comment,
            attributes = new Dictionary<string, object> { [_s.TempoAccountAttributeKey] = new { value = account.Trim() } },
        };
        var res = await SendJsonAsync(HttpMethod.Put, $"rest/tempo-timesheets/4/worklogs/{Uri.EscapeDataString(w.TempoId)}", body, ct);
        var booked = (res as JsonArray)?.FirstOrDefault() ?? res;
        var attr = booked?["attributes"]?[_s.TempoAccountAttributeKey];
        var actual = attr == null ? (booked?["attributes"] == null ? account.Trim() : null) : ReadAccount(attr["value"] ?? attr);
        if (!string.Equals(actual, account.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Tempo hat den Account \"{account}\" nicht übernommen (jetzt: \"{actual ?? "keiner"}\").");
        w.Account = actual; w.AccountChecked = true;
        return actual!;
    }

    /// <summary>Alle Worklogs eines Tickets (alle Personen) – für die Doku „Arbeit &amp; Verlauf“.</summary>
    public async Task<List<(DateTime Started, string Author, long Seconds, string Comment)>> GetIssueWorklogsAsync(string key, CancellationToken ct = default)
    {
        var node = await GetAsync($"rest/api/2/issue/{Uri.EscapeDataString(key)}/worklog", ct);
        var outp = new List<(DateTime, string, long, string)>();
        foreach (var w in node?["worklogs"]?.AsArray() ?? [])
        {
            if (!DateTimeOffset.TryParse(w.Str("started"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)) continue;
            outp.Add((started.LocalDateTime, w?["author"].Str("displayName") ?? "", w.Long("timeSpentSeconds"), w.Str("comment")));
        }
        return outp.OrderBy(x => x.Item1).ToList();
    }

    public static string FormatJiraDate(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{local:yyyy-MM-dd'T'HH:mm:ss}.000{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";
    }

    /// <summary>
    /// Bucht einen Worklog. Mit Account geht die Buchung immer über Tempo (nur dort gibt es Accounts) –
    /// danach wird geprüft, ob der Account im angelegten Worklog wirklich gesetzt ist.
    /// </summary>
    /// <returns>Gebuchter Account laut Jira/Tempo (null = ohne Account gebucht).</returns>
    public async Task<string?> AddWorklogAsync(string issueKey, DateTime start, long seconds, string comment, string? account, CancellationToken ct = default)
    {
        if (_s.UseTempo || !string.IsNullOrWhiteSpace(account))
        {
            var me = await GetMyselfAsync(ct);
            var attributes = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(account))
                attributes[_s.TempoAccountAttributeKey] = new { value = account.Trim() };
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
            JsonNode? res;
            try { res = await PostAsync("rest/tempo-timesheets/4/worklogs/", body, ct); }
            catch (ApiException e) when (!_s.UseTempo && e.StatusCode == 404)
            {
                throw new InvalidOperationException($"Für {issueKey} ist ein Account hinterlegt, aber Tempo ist nicht erreichbar – ohne Tempo kann Jira keinen Account buchen. Nichts gebucht.");
            }
            if (string.IsNullOrWhiteSpace(account)) return null;
            var booked = (res as JsonArray)?.FirstOrDefault() ?? res;
            var attr = booked?["attributes"]?[_s.TempoAccountAttributeKey];
            var actual = attr == null ? null : ReadAccount(attr["value"] ?? attr);
            if (actual == null && booked?["attributes"] == null) return account; // ältere Tempo-Versionen liefern keine Attribute zurück
            if (!string.Equals(actual, account.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Worklog auf {issueKey} angelegt, aber Tempo hat den Account \"{account}\" nicht übernommen (gebucht: \"{actual ?? "keiner"}\"). Bitte in Tempo prüfen.");
            return actual;
        }

        var wl = new { comment, started = FormatJiraDate(start), timeSpentSeconds = seconds };
        await PostAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/worklog", wl, ct);
        return null;
    }

    public async Task<List<TempoAccount>> GetTempoAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await GetAsync("rest/tempo-accounts/1/account", ct);
            return (node?.AsArray() ?? [])
                .Where(a => a.Str("status") is "" or "OPEN")
                .Select(a => new TempoAccount(a.Str("key"), a.Str("name"), a.Str("id")))
                .Where(a => a.Key.Length > 0)
                .OrderBy(a => a.Name).ToList();
        }
        catch { return []; }
    }

    // ---------------- Tickets erstellen ----------------

    public async Task<string> CreateIssueAsync(string projectKey, string summary, string description, string issueType,
        double estimateHours, string? epicKey, CancellationToken ct = default, string? account = null)
    {
        LastCreateNotes.Clear();
        if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(_s.AccountFieldId))
        {
            // Tempo-Account-Feld: je nach Version als ID-Zahl, {id} oder Key – nacheinander probieren
            var acc = (await GetTempoAccountsAsync(ct)).FirstOrDefault(a => string.Equals(a.Key, account, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Id, account, StringComparison.OrdinalIgnoreCase));
            var variants = new List<JsonNode?>();
            if (acc != null && long.TryParse(acc.Id, out var idNum)) { variants.Add(JsonValue.Create(idNum)); variants.Add(new JsonObject { ["id"] = acc.Id }); }
            variants.Add(JsonValue.Create(account));
            variants.Add(new JsonObject { ["key"] = account });
            ApiException? lastErr = null;
            foreach (var v in variants)
            {
                try
                {
                    var key = await CreateIssueCoreAsync(projectKey, summary, description, issueType, estimateHours, epicKey, (_s.AccountFieldId, v), ct);
                    var check = await GetIssueAsync(key, ct);
                    if (check?.Account == null)
                        throw new InvalidOperationException($"{key} wurde angelegt, aber das Account-Feld ist leer. Bitte in Jira prüfen.");
                    return key;
                }
                catch (ApiException e) when (e.StatusCode == 400) { lastErr = e; }
            }
            throw new InvalidOperationException($"Ticket konnte nicht mit Account \"{account}\" angelegt werden: {lastErr?.Message}");
        }
        return await CreateIssueCoreAsync(projectKey, summary, description, issueType, estimateHours, epicKey, null, ct);
    }

    private async Task<string> CreateIssueCoreAsync(string projectKey, string summary, string description, string issueType,
        double estimateHours, string? epicKey, (string Field, JsonNode? Value)? extra, CancellationToken ct)
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
        if (extra is { } x) fields[x.Field] = x.Value?.DeepClone();

        // Felder, die nicht auf der Anlege-Maske stehen, weglassen und erneut versuchen – danach nachtragen
        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "description", "timetracking", "parent" };
        if (!string.IsNullOrWhiteSpace(_s.EpicLinkFieldId)) optional.Add(_s.EpicLinkFieldId);
        var dropped = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        JsonNode? node = null;
        for (var attempt = 0; ; attempt++)
        {
            try { node = await PostAsync("rest/api/2/issue", new JsonObject { ["fields"] = fields.DeepClone() }, ct); break; }
            catch (ApiException e) when (e.StatusCode == 400 && attempt < 4)
            {
                var bad = UnsettableFields(e.Message).Where(f => optional.Contains(f) && fields.ContainsKey(f)).ToList();
                if (bad.Count == 0) throw;
                foreach (var f in bad) { dropped[f] = fields[f]?.DeepClone(); fields.Remove(f); }
            }
        }
        var key = node.Str("key");
        if (dropped.Count > 0) await RestoreDroppedFieldsAsync(key, dropped, description, estimateHours, epicKey, ct);
        return key;
    }

    /// <summary>Hinweise zum letzten angelegten Ticket (z. B. Beschreibung als Kommentar nachgetragen).</summary>
    public List<string> LastCreateNotes { get; } = [];

    /// <summary>Feldnamen aus einer Jira-400-Antwort, die auf der Maske fehlen („cannot be set … not on the appropriate screen, or unknown“).</summary>
    public static List<string> UnsettableFields(string message)
    {
        var start = message.IndexOf('{');
        if (start < 0) return [];
        try
        {
            var errors = JsonNode.Parse(message[start..])?["errors"] as JsonObject;
            return errors == null ? [] : [.. errors
                .Where(e => (e.Value?.ToString() ?? "") is var m && (m.Contains("cannot be set", StringComparison.OrdinalIgnoreCase) || m.Contains("appropriate screen", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Key)];
        }
        catch { return []; }
    }

    /// <summary>Weggelassene Felder per Bearbeiten nachtragen; Beschreibung notfalls als Kommentar, damit nichts verloren geht.</summary>
    private async Task RestoreDroppedFieldsAsync(string key, Dictionary<string, JsonNode?> dropped, string description, double estimateHours, string? epicKey, CancellationToken ct)
    {
        foreach (var (field, value) in dropped)
        {
            try
            {
                await SendJsonAsync(HttpMethod.Put, $"rest/api/2/issue/{Uri.EscapeDataString(key)}", new JsonObject { ["fields"] = new JsonObject { [field] = value?.DeepClone() } }, ct);
                LastCreateNotes.Add(field switch { "description" => "Beschreibung nachträglich gesetzt", "timetracking" => "Schätzung nachträglich gesetzt", _ => $"{field} nachträglich gesetzt" });
            }
            catch (ApiException)
            {
                if (field == "description" && !string.IsNullOrWhiteSpace(description))
                {
                    try { await AddCommentAsync(key, description, ct); LastCreateNotes.Add("Beschreibung ist in Jira nicht erlaubt – als Kommentar eingetragen"); }
                    catch { LastCreateNotes.Add("Beschreibung konnte nicht gesetzt werden"); }
                }
                else if (field == "timetracking") LastCreateNotes.Add($"Schätzung ({FormatEstimate(estimateHours)}) ist in Jira nicht erlaubt – nicht gesetzt");
                else LastCreateNotes.Add($"Epic {epicKey} konnte nicht verknüpft werden – bitte in Jira zuordnen");
            }
        }
    }

    public static string FormatEstimate(double hours)
    {
        var totalMin = (int)Math.Round(hours * 60);
        var h = totalMin / 60; var m = totalMin % 60;
        return m == 0 ? $"{h}h" : h == 0 ? $"{m}m" : $"{h}h {m}m";
    }

    public async Task AddCommentAsync(string issueKey, string body, CancellationToken ct = default)
        => await PostAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment", new { body }, ct);

    /// <summary>Übergang per Name oder Zielstatus ausführen.</summary>
    public async Task TransitionAsync(string issueKey, string target, CancellationToken ct = default)
    {
        var key = Uri.EscapeDataString(issueKey);
        var list = (await GetAsync($"rest/api/2/issue/{key}/transitions", ct))?["transitions"]?.AsArray() ?? [];
        var t = list.FirstOrDefault(x => string.Equals(x?["name"]?.ToString(), target, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(x?["to"]?["name"]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Übergang \"{target}\" für {issueKey} nicht verfügbar");
        await PostAsync($"rest/api/2/issue/{key}/transitions", new { transition = new { id = t!["id"]!.ToString() } }, ct);
    }
}
