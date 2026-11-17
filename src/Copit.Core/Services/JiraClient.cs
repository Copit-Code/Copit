using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Copit.Core.Models;

namespace Copit.Core.Services;

public record JiraFieldIds(string? EpicLink, string? Account, string? EpicName);
public record JiraFieldOption(string Id, string Label);
public record JiraCreateField(string Id, string Name, bool Required, bool HasDefault, string Type, string? ItemsType, string? Custom, List<JiraFieldOption> Options);
public record JiraCreateCheck(bool? CanCreate, JiraIssueType? Type, List<JiraIssueType> Types, JiraFieldIds Fields, List<JiraCreateField> Required, List<string> Problems);

public record JiraIssueType(string Id, string Name, bool Subtask)
{
    public string Label => $"{Name} · {Id}";
}

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
            IssueTypeId = f?["issuetype"].Str("id") ?? "",
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
        var key = await TempoAccountKeyAsync(account, ct);
        var body = new
        {
            worker = string.IsNullOrEmpty(me.Key) ? me.Name : me.Key,
            originTaskId = w.IssueKey,
            started = w.Started.ToString("yyyy-MM-dd'T'HH:mm:ss.000"),
            timeSpentSeconds = w.TimeSpentSeconds,
            billableSeconds = w.TimeSpentSeconds,
            comment = w.Comment,
            attributes = new Dictionary<string, object> { [_s.TempoAccountAttributeKey] = new { value = key } },
        };
        JsonNode? res;
        try { res = await SendJsonAsync(HttpMethod.Put, $"rest/tempo-timesheets/4/worklogs/{Uri.EscapeDataString(w.TempoId)}", body, ct); }
        catch (ApiException e) { throw new InvalidOperationException($"Tempo lehnt den Account {key} ab: {JiraErrorText(e.Message)}"); }
        var booked = (res as JsonArray)?.FirstOrDefault() ?? res;
        var attr = booked?["attributes"]?[_s.TempoAccountAttributeKey];
        var actual = attr == null ? (booked?["attributes"] == null ? key : null) : ReadAccount(attr["value"] ?? attr);
        if (!string.Equals(actual, key, StringComparison.OrdinalIgnoreCase) && !string.Equals(actual, account.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Tempo hat den Account \"{key}\" nicht übernommen (jetzt: \"{actual ?? "keiner"}\").");
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
            var worker = string.IsNullOrEmpty(me.Key) ? me.Name : me.Key;
            if (string.IsNullOrWhiteSpace(worker)) throw new InvalidOperationException("Jira liefert keinen Benutzer-Key für dich (myself) – Buchung über Tempo nicht möglich. Token/Berechtigung prüfen.");
            // Tempo erwartet im Account-Attribut den Account-KEY. Am Ticket steht oft nur Name oder ID → auflösen.
            var accKey = string.IsNullOrWhiteSpace(account) ? null : await TempoAccountKeyAsync(account, ct);
            var attributes = new Dictionary<string, object>();
            if (accKey != null) attributes[_s.TempoAccountAttributeKey] = new { value = accKey };
            var body = new
            {
                worker,
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
            catch (ApiException e)
            {
                throw new InvalidOperationException($"Tempo lehnt die Buchung auf {issueKey} ab{(accKey != null ? $" (Account {accKey})" : "")}: {JiraErrorText(e.Message)}");
            }
            if (accKey == null) return null;
            var booked = (res as JsonArray)?.FirstOrDefault() ?? res;
            var attr = booked?["attributes"]?[_s.TempoAccountAttributeKey];
            var actual = attr == null ? null : ReadAccount(attr["value"] ?? attr);
            if (actual == null && booked?["attributes"] == null) return accKey; // ältere Tempo-Versionen liefern keine Attribute zurück
            if (!string.Equals(actual, accKey, StringComparison.OrdinalIgnoreCase) && !string.Equals(actual, account!.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Worklog auf {issueKey} angelegt, aber Tempo hat den Account \"{accKey}\" nicht übernommen (gebucht: \"{actual ?? "keiner"}\"). Bitte in Tempo prüfen.");
            return actual ?? accKey;
        }

        var wl = new { comment, started = FormatJiraDate(start), timeSpentSeconds = seconds };
        await PostAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/worklog", wl, ct);
        return null;
    }

    /// <summary>Account (Key, Name oder ID) → Tempo-Account-Key fürs Worklog-Attribut. Ohne Treffer: Eingabe unverändert.</summary>
    public async Task<string> TempoAccountKeyAsync(string account, CancellationToken ct = default)
    {
        try { if (await ResolveTempoAccountAsync(account, ct) is { Key.Length: > 0 } acc) return acc.Key; }
        catch (Exception) { /* Tempo-Accounts nicht lesbar → so buchen, wie eingetragen */ }
        return account.Trim();
    }

    /// <summary>Fehler betrifft das Account-Feld (dann nächste Schreibweise probieren, sonst echten Fehler weiterreichen).</summary>
    private bool IsAccountError(string message) =>
        message.Contains("account", StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(_s.AccountFieldId) && message.Contains(_s.AccountFieldId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tempo-Account zu Key, ID oder Namen (auch „Key · Name“), notfalls direkt per Key bei Tempo.</summary>
    public async Task<TempoAccount?> ResolveTempoAccountAsync(string account, CancellationToken ct = default)
    {
        account = account.Trim();
        var list = await GetTempoAccountsAsync(ct);
        var hit = list.FirstOrDefault(a => a.Key.Equals(account, StringComparison.OrdinalIgnoreCase) || a.Id == account)
                  ?? list.FirstOrDefault(a => a.Name.Trim().Equals(account, StringComparison.OrdinalIgnoreCase))
                  ?? list.FirstOrDefault(a => account.Contains(a.Key, StringComparison.OrdinalIgnoreCase) && a.Key.Length > 4);
        if (hit != null) return hit;
        try
        {
            var n = await GetAsync($"rest/tempo-accounts/1/account/key/{Uri.EscapeDataString(account)}", ct);
            if (n.Str("id") is { Length: > 0 } id) return new TempoAccount(n.Str("key"), n.Str("name"), id);
        }
        catch (ApiException) { }
        return null;
    }

    public async Task<List<TempoAccount>> GetTempoAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await GetAsync("rest/tempo-accounts/1/account", ct);
            return (node?.AsArray() ?? [])
                .Where(a => a.Str("status") is "" or "OPEN" or "Open" or "open")
                .Select(a => new TempoAccount(a.Str("key"), a.Str("name"), a.Str("id")))
                .Where(a => a.Key.Length > 0)
                .OrderBy(a => a.Name).ToList();
        }
        catch { return []; }
    }

    // ---------------- Tickets erstellen ----------------

    public async Task<string> CreateIssueAsync(string projectKey, string summary, string description, string issueType,
        double estimateHours, string? epicKey, CancellationToken ct = default, string? account = null,
        IReadOnlyDictionary<string, JsonNode?>? extraFields = null)
    {
        LastCreateNotes.Clear();
        if (!string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(_s.AccountFieldId))
        {
            // Tempo-Account-Feld erwartet die interne Account-ID („Account id is null“ bei Key/Name) →
            // Account über Key, ID oder Namen auflösen und die ID in den üblichen Schreibweisen probieren
            var acc = await ResolveTempoAccountAsync(account, ct)
                      ?? throw new InvalidOperationException($"Account „{account}“ wurde in Tempo nicht gefunden – bitte im Ticket-Dialog einen Account aus der Liste wählen.");
            var variants = new List<JsonNode?>();
            if (long.TryParse(acc.Id, out var idNum)) variants.Add(JsonValue.Create(idNum));
            if (acc.Id.Length > 0) { variants.Add(new JsonObject { ["id"] = acc.Id }); variants.Add(JsonValue.Create(acc.Id)); }
            variants.Add(new JsonObject { ["key"] = acc.Key });
            ApiException? lastErr = null;
            foreach (var v in variants)
            {
                try
                {
                    var key = await CreateIssueCoreAsync(projectKey, summary, description, issueType, estimateHours, epicKey, (_s.AccountFieldId, v), extraFields, ct);
                    var check = await GetIssueAsync(key, ct);
                    if (check?.Account == null)
                        throw new InvalidOperationException($"{key} wurde angelegt, aber das Account-Feld ist leer. Bitte in Jira prüfen.");
                    return key;
                }
                catch (ApiException e) when (e.StatusCode == 400 && IsAccountError(e.Message)) { lastErr = e; }
            }
            throw new InvalidOperationException($"Ticket konnte nicht mit Account {acc.Key} (ID {acc.Id}) angelegt werden: {JiraErrorText(lastErr?.Message ?? "")}");
        }
        return await CreateIssueCoreAsync(projectKey, summary, description, issueType, estimateHours, epicKey, null, extraFields, ct);
    }

    private async Task<string> CreateIssueCoreAsync(string projectKey, string summary, string description, string issueType,
        double estimateHours, string? epicKey, (string Field, JsonNode? Value)? extra, IReadOnlyDictionary<string, JsonNode?>? extraFields, CancellationToken ct)
    {
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["summary"] = summary,
            ["description"] = description,
            ["issuetype"] = IssueTypeRef(string.IsNullOrWhiteSpace(issueType) ? _s.DefaultIssueType : issueType),
        };
        if (estimateHours > 0)
            fields["timetracking"] = new JsonObject { ["originalEstimate"] = FormatEstimate(estimateHours) };
        // Epic: über das Epic-Link-Feld (Jira Server/DC), sonst als parent (Jira Cloud / neue Hierarchie)
        var epicField = await EpicLinkFieldAsync(ct);
        if (!string.IsNullOrWhiteSpace(epicKey))
        {
            if (epicField != null) fields[epicField] = epicKey;
            else fields["parent"] = new JsonObject { ["key"] = epicKey };
        }
        if (extra is { } x) fields[x.Field] = x.Value?.DeepClone();
        foreach (var (k, v) in extraFields ?? new Dictionary<string, JsonNode?>()) fields[k] = v?.DeepClone();

        // Felder, die nicht auf der Anlege-Maske stehen, weglassen und erneut versuchen – danach nachtragen
        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "description", "timetracking", "parent" };
        if (epicField != null) optional.Add(epicField);
        var dropped = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        JsonNode? node = null;
        for (var attempt = 0; ; attempt++)
        {
            try { node = await PostAsync("rest/api/2/issue", new JsonObject { ["fields"] = fields.DeepClone() }, ct); break; }
            catch (ApiException e) when (e.StatusCode == 400 && attempt < 5)
            {
                // parent ist in Jira DC nur für Untervorgänge erlaubt → Epic über das Epic-Link-Feld verknüpfen
                if (fields.ContainsKey("parent") && !string.IsNullOrWhiteSpace(epicKey) && MentionsParent(e.Message))
                {
                    fields.Remove("parent");
                    var ef = epicField ?? (await DetectFieldsSafeAsync(ct))?.EpicLink;
                    if (ef != null) { fields[ef] = epicKey; epicField = ef; LastCreateNotes.Add($"Epic über das Epic-Link-Feld ({ef}) verknüpft"); }
                    else LastCreateNotes.Add($"Epic {epicKey} konnte nicht verknüpft werden – bitte in Jira zuordnen");
                    continue;
                }
                // Typ per Name abgelehnt (z. B. lokalisiert „Aufgabe“ statt „Task“) → passende ID aus Jira suchen
                if (e.Message.Contains("issuetype", StringComparison.OrdinalIgnoreCase) && fields["issuetype"]?["name"] is { } typeName)
                {
                    var types = await GetIssueTypesAsync(projectKey, ct);
                    var match = types.FirstOrDefault(t => t.Name.Equals(typeName.ToString(), StringComparison.OrdinalIgnoreCase))
                                ?? types.FirstOrDefault(t => !t.Subtask && TaskAliases.Contains(typeName.ToString()) && TaskAliases.Contains(t.Name));
                    if (match == null)
                        throw new InvalidOperationException($"Ticket-Typ \"{typeName}\" gibt es im Projekt {projectKey} nicht. Verfügbar: {string.Join(", ", types.Select(t => $"{t.Name} ({t.Id})"))}");
                    fields["issuetype"] = new JsonObject { ["id"] = match.Id };
                    LastCreateNotes.Add($"Ticket-Typ als {match.Name} ({match.Id}) angelegt");
                    continue;
                }
                if (e.Message.Contains("issuetype", StringComparison.OrdinalIgnoreCase) && fields["issuetype"]?["id"] is { } typeId)
                {
                    var types = await GetIssueTypesAsync(projectKey, ct);
                    throw new InvalidOperationException($"Jira lehnt das Ticket ab: {JiraErrorText(e.Message)}\nTyp-ID {typeId}; verfügbar in {projectKey}: {string.Join(", ", types.Select(t => $"{t.Name} ({t.Id})"))}");
                }
                var bad = UnsettableFields(e.Message).Where(f => optional.Contains(f) && fields.ContainsKey(f)).ToList();
                if (bad.Count == 0) throw new ApiException("Jira lehnt das Ticket ab: " + JiraErrorText(e.Message), 400);
                foreach (var f in bad) { dropped[f] = fields[f]?.DeepClone(); fields.Remove(f); }
            }
        }
        var key = node.Str("key");
        if (dropped.Count > 0) await RestoreDroppedFieldsAsync(key, dropped, description, estimateHours, epicKey, ct);
        return key;
    }

    private static bool MentionsParent(string message) =>
        message.Contains("parent", StringComparison.OrdinalIgnoreCase) || message.Contains("sub-task", StringComparison.OrdinalIgnoreCase)
        || message.Contains("subtask", StringComparison.OrdinalIgnoreCase) || message.Contains("Untervorgang", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Unteraufgabe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lesbarer Text aus einer Jira-Fehlerantwort: errorMessages und errors (Feld: Meldung).</summary>
    public static string JiraErrorText(string message)
    {
        var start = message.IndexOf('{');
        if (start < 0) return message;
        try
        {
            var n = JsonNode.Parse(message[start..]);
            var parts = new List<string>();
            foreach (var m in n?["errorMessages"]?.AsArray() ?? []) if (m != null) parts.Add(m.ToString());
            if (n?["errors"] is JsonObject o) foreach (var (k, v) in o) parts.Add($"{k}: {v}");
            return parts.Count > 0 ? string.Join(" · ", parts) : message;
        }
        catch { return message; }
    }

    // ---------------- Felder erkennen, Pflichtfelder, Prüfung ----------------

    private JiraFieldIds? _fieldIds;

    /// <summary>Erkennt Epic-Link-, Epic-Name- und Tempo-Account-Feld anhand ihres Feldtyps.</summary>
    public async Task<JiraFieldIds> DetectFieldsAsync(CancellationToken ct = default)
    {
        if (_fieldIds != null) return _fieldIds;
        var arr = await GetAsync("rest/api/2/field", ct) as JsonArray ?? [];
        string? F(string custom) => arr.FirstOrDefault(f => f?["schema"]?["custom"]?.ToString() == custom)?["id"]?.ToString();
        return _fieldIds = new JiraFieldIds(F("com.pyxis.greenhopper.jira:gh-epic-link"), F("com.tempoplugin.tempo-accounts:accounts.customfield"), F("com.pyxis.greenhopper.jira:gh-epic-label"));
    }

    private async Task<JiraFieldIds?> DetectFieldsSafeAsync(CancellationToken ct)
    {
        try { return await DetectFieldsAsync(ct); } catch { return null; }
    }

    private async Task<string?> EpicLinkFieldAsync(CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(_s.EpicLinkFieldId) ? _s.EpicLinkFieldId : (await DetectFieldsSafeAsync(ct))?.EpicLink;

    /// <summary>Felder der Anlege-Maske für Projekt + Typ (neue createmeta-API, sonst die alte).</summary>
    public async Task<List<JiraCreateField>> GetCreateFieldsAsync(string projectKey, string issueTypeId, CancellationToken ct = default)
    {
        try
        {
            var n = await GetAsync($"rest/api/2/issue/createmeta/{Uri.EscapeDataString(projectKey)}/issuetypes/{Uri.EscapeDataString(issueTypeId)}?maxResults=500", ct);
            var values = (n?["values"] ?? n) as JsonArray;
            if (values is { Count: > 0 }) return [.. values.Where(v => v != null).Select(v => ParseCreateField(v!["fieldId"]?.ToString() ?? "", v!))];
        }
        catch (ApiException) { }
        var old = await GetAsync($"rest/api/2/issue/createmeta?projectKeys={Uri.EscapeDataString(projectKey)}&issuetypeIds={Uri.EscapeDataString(issueTypeId)}&expand=projects.issuetypes.fields", ct);
        var fields = old?["projects"]?[0]?["issuetypes"]?[0]?["fields"] as JsonObject;
        return fields == null ? [] : [.. fields.Where(f => f.Value != null).Select(f => ParseCreateField(f.Key, f.Value!))];
    }

    public static JiraCreateField ParseCreateField(string id, JsonNode n)
    {
        var options = (n["allowedValues"] as JsonArray ?? [])
            .Where(o => o != null)
            .Select(o => new JiraFieldOption(o!["id"]?.ToString() ?? o["key"]?.ToString() ?? "", o["value"]?.ToString() ?? o["name"]?.ToString() ?? o["key"]?.ToString() ?? ""))
            .Where(o => o.Id.Length > 0 && o.Label.Length > 0).ToList();
        return new JiraCreateField(id, n["name"]?.ToString() ?? id, n["required"]?.GetValue<bool>() ?? false, n["hasDefaultValue"]?.GetValue<bool>() ?? false,
            n["schema"]?["type"]?.ToString() ?? "string", n["schema"]?["items"]?.ToString(), n["schema"]?["custom"]?.ToString(), options);
    }

    private static readonly HashSet<string> HandledFields = new(StringComparer.OrdinalIgnoreCase)
        { "project", "issuetype", "summary", "description", "reporter", "timetracking", "parent", "assignee", "priority" };

    /// <summary>Pflichtfelder ohne Vorgabewert, die Copit nicht selbst füllt (Epic-Link/Account werden gesondert behandelt).</summary>
    public static List<JiraCreateField> MissingRequired(IEnumerable<JiraCreateField> fields, JiraFieldIds? ids, string? accountField = null) =>
        [.. fields.Where(f => f.Required && !f.HasDefault && !HandledFields.Contains(f.Id)
                             && f.Id != ids?.EpicLink && f.Id != ids?.Account && f.Id != accountField)];

    /// <summary>Wert für ein Feld der Anlege-Maske: Auswahlfelder per ID, Mehrfachauswahl als Liste, Zahl, sonst Text.</summary>
    public static JsonNode? FieldValue(JiraCreateField f, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        if (f.Options.Count > 0)
        {
            var o = f.Options.FirstOrDefault(x => x.Label.Equals(value, StringComparison.OrdinalIgnoreCase) || x.Id == value);
            if (o == null) return null;
            var node = new JsonObject { ["id"] = o.Id };
            return f.Type == "array" ? new JsonArray(node) : node;
        }
        if (f.Type == "number" && double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        if (f.Type == "array") return new JsonArray(JsonValue.Create(value));
        return JsonValue.Create(value);
    }

    /// <summary>Prüft vor dem Anlegen: Berechtigung, Typ, erkannte Felder und offene Pflichtfelder.</summary>
    public async Task<JiraCreateCheck> CheckCreateAsync(string projectKey, string issueType, CancellationToken ct = default)
    {
        bool? canCreate = null;
        try
        {
            var p = await GetAsync($"rest/api/2/mypermissions?projectKey={Uri.EscapeDataString(projectKey)}&permissions=CREATE_ISSUES", ct);
            canCreate = p?["permissions"]?["CREATE_ISSUES"]?["havePermission"]?.GetValue<bool>();
        }
        catch (ApiException) { }
        var types = await GetIssueTypesAsync(projectKey, ct);
        var ids = await DetectFieldsSafeAsync(ct) ?? new JiraFieldIds(null, null, null);
        var r = IssueTypeRef(string.IsNullOrWhiteSpace(issueType) ? _s.DefaultIssueType : issueType);
        var type = types.FirstOrDefault(t => r["id"]?.ToString() == t.Id || string.Equals(r["name"]?.ToString(), t.Name, StringComparison.OrdinalIgnoreCase));
        var required = new List<JiraCreateField>();
        var problems = new List<string>();
        if (canCreate == false) problems.Add($"Keine Berechtigung „Vorgänge erstellen“ im Projekt {projectKey} – bitte beim Jira-Admin anfragen.");
        if (type == null) problems.Add($"Ticket-Typ „{issueType}“ gibt es in {projectKey} nicht. Verfügbar: {string.Join(", ", types.Select(t => t.Label))}");
        else
        {
            try { required = MissingRequired(await GetCreateFieldsAsync(projectKey, type.Id, ct), ids, _s.AccountFieldId); }
            catch (Exception e) { problems.Add("Pflichtfelder nicht ermittelbar: " + JiraErrorText(e.Message)); }
        }
        return new JiraCreateCheck(canCreate, type, types, ids, required, problems);
    }

    private static readonly HashSet<string> TaskAliases = new(StringComparer.OrdinalIgnoreCase) { "Task", "Aufgabe", "Tâche", "Tarea", "Attività" };

    /// <summary>Ticket-Typ als Name oder ID: "10002", "Aufgabe" oder "Aufgabe (10002)" / "Aufgabe · 10002".</summary>
    public static JsonObject IssueTypeRef(string value)
    {
        value = value.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(value, @"^(?:.*?[\s(·]+)?(\d+)\)?$");
        return m.Success ? new JsonObject { ["id"] = m.Groups[1].Value } : new JsonObject { ["name"] = value };
    }

    /// <summary>Ticket-Typen, die im Projekt angelegt werden können (neuere und ältere Jira-Versionen).</summary>
    public async Task<List<JiraIssueType>> GetIssueTypesAsync(string projectKey, CancellationToken ct = default)
    {
        static List<JiraIssueType> Read(JsonNode? arr) => [.. (arr as JsonArray ?? [])
            .Where(t => t != null).Select(t => new JiraIssueType(t.Str("id"), t.Str("name"), t?["subtask"]?.GetValue<bool>() ?? false))
            .Where(t => t.Id.Length > 0)];
        try
        {
            var n = await GetAsync($"rest/api/2/issue/createmeta/{Uri.EscapeDataString(projectKey)}/issuetypes?maxResults=100", ct);
            var list = Read(n?["values"] ?? n);
            if (list.Count > 0) return list;
        }
        catch (ApiException) { }
        try
        {
            var n = await GetAsync($"rest/api/2/issue/createmeta?projectKeys={Uri.EscapeDataString(projectKey)}", ct);
            var list = Read(n?["projects"]?[0]?["issuetypes"]);
            if (list.Count > 0) return list;
        }
        catch (ApiException) { }
        var p = await GetAsync($"rest/api/2/project/{Uri.EscapeDataString(projectKey)}", ct);
        return Read(p?["issueTypes"]);
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
