using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Copit.Core.Services;

public record GitLabUser(long Id, string Username, string Name);
public record GitLabProjectInfo(long Id, string PathWithNamespace, string WebUrl, string DefaultBranch);
public record GitLabEvent(DateTime CreatedAt, string Action, string TargetType, string TargetTitle, string Ref, string CommitTitle, int CommitCount, long ProjectId);
public record GitLabPipeline(long Id, string Status, string Ref, string Sha, DateTime? CreatedAt, DateTime? UpdatedAt, string WebUrl, double? Duration);
public record GitLabJob(long Id, string Name, string Stage, string Status, double? Duration, string WebUrl, bool AllowFailure);
public record GitLabMergeRequest(long Iid, long ProjectId, string Title, string Description, string State, string SourceBranch, string TargetBranch,
    string Author, string WebUrl, string? DetailedStatus, bool Draft, DateTime? UpdatedAt, string? PipelineStatus);

/// <summary>GitLab REST API v4 mit Private Access Token.</summary>
public class GitLabClient
{
    private readonly HttpClient _http;
    private GitLabUser? _me;
    public string BaseUrl { get; }

    public GitLabClient(string baseUrl, string token, bool ignoreCerts)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = HttpFactory.Create(BaseUrl + "/api/v4", ignoreCerts);
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
    }

    private static string Id(string projectPathOrId) =>
        long.TryParse(projectPathOrId, out _) ? projectPathOrId : Uri.EscapeDataString(projectPathOrId);

    private async Task<JsonNode?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        return await HttpFactory.ReadJsonAsync(resp, ct);
    }

    private async Task<JsonNode?> PostAsync(string url, object? body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(url, body ?? new { }, ct);
        return await HttpFactory.ReadJsonAsync(resp, ct);
    }

    private static DateTime? Date(JsonNode? n, string p) =>
        DateTime.TryParse(n.Str(p), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;

    public async Task<GitLabUser> GetMeAsync(CancellationToken ct = default)
    {
        if (_me != null) return _me;
        var n = await GetAsync("user", ct);
        return _me = new GitLabUser(n.Long("id"), n.Str("username"), n.Str("name"));
    }

    public async Task<GitLabProjectInfo> GetProjectAsync(string project, CancellationToken ct = default)
    {
        var n = await GetAsync($"projects/{Id(project)}", ct);
        return new GitLabProjectInfo(n.Long("id"), n.Str("path_with_namespace"), n.Str("web_url"), n.Str("default_branch"));
    }

    private readonly Dictionary<long, string> _projectNames = [];

    public async Task<string> GetProjectNameAsync(long id, CancellationToken ct = default)
    {
        if (_projectNames.TryGetValue(id, out var n)) return n;
        try { n = (await GetProjectAsync(id.ToString(), ct)).PathWithNamespace; } catch { n = id.ToString(); }
        return _projectNames[id] = n;
    }

    /// <summary>Alle Events des aktuellen Users an einem Tag (Pushes, MRs, Kommentare, ...).</summary>
    public async Task<List<GitLabEvent>> GetMyEventsAsync(DateOnly day, CancellationToken ct = default)
    {
        var me = await GetMeAsync(ct);
        var after = day.AddDays(-1).ToString("yyyy-MM-dd");
        var before = day.AddDays(1).ToString("yyyy-MM-dd");
        var list = new List<GitLabEvent>();
        for (int page = 1; page <= 5; page++)
        {
            var arr = (await GetAsync($"users/{me.Id}/events?after={after}&before={before}&per_page=100&page={page}", ct))?.AsArray();
            if (arr == null || arr.Count == 0) break;
            foreach (var e in arr)
            {
                var created = Date(e, "created_at") ?? DateTime.MinValue;
                if (DateOnly.FromDateTime(created) != day) continue;
                var push = e?["push_data"];
                list.Add(new GitLabEvent(created, e.Str("action_name"), e.Str("target_type"), e.Str("target_title"),
                    push.Str("ref"), push.Str("commit_title"), (int)push.Long("commit_count"), e.Long("project_id")));
            }
            if (arr.Count < 100) break;
        }
        return list.OrderBy(e => e.CreatedAt).ToList();
    }

    public async Task<List<GitLabPipeline>> GetPipelinesAsync(string project, string? @ref = null, int count = 5, CancellationToken ct = default)
    {
        var q = $"projects/{Id(project)}/pipelines?per_page={count}&order_by=id&sort=desc";
        if (!string.IsNullOrEmpty(@ref)) q += "&ref=" + Uri.EscapeDataString(@ref);
        var arr = (await GetAsync(q, ct))?.AsArray() ?? [];
        return arr.Select(p => new GitLabPipeline(p.Long("id"), p.Str("status"), p.Str("ref"), p.Str("sha"),
            Date(p, "created_at"), Date(p, "updated_at"), p.Str("web_url"), null)).ToList();
    }

    public async Task<List<GitLabJob>> GetJobsAsync(string project, long pipelineId, CancellationToken ct = default)
    {
        var arr = (await GetAsync($"projects/{Id(project)}/pipelines/{pipelineId}/jobs?per_page=100&include_retried=false", ct))?.AsArray() ?? [];
        return arr.Select(j => new GitLabJob(j.Long("id"), j.Str("name"), j.Str("stage"), j.Str("status"),
            double.TryParse(j?["duration"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
            j.Str("web_url"), j?["allow_failure"]?.GetValue<bool>() ?? false))
            .OrderBy(j => j.Id).ToList();
    }

    public async Task<string> GetJobTraceAsync(string project, long jobId, int tailLines = 120, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"projects/{Id(project)}/jobs/{jobId}/trace", ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        text = StripAnsi(text);
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - tailLines)));
    }

    internal static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;]*[A-Za-z]|section_(start|end):\d+:[^\r\n]*\r?", "");

    public Task RetryPipelineAsync(string project, long pipelineId, CancellationToken ct = default)
        => PostAsync($"projects/{Id(project)}/pipelines/{pipelineId}/retry", null, ct);

    public Task RunPipelineAsync(string project, string @ref, CancellationToken ct = default)
        => PostAsync($"projects/{Id(project)}/pipeline?ref={Uri.EscapeDataString(@ref)}", null, ct);

    public Task RetryJobAsync(string project, long jobId, CancellationToken ct = default)
        => PostAsync($"projects/{Id(project)}/jobs/{jobId}/retry", null, ct);

    private static GitLabMergeRequest MapMr(JsonNode? m) => new(
        m.Long("iid"), m.Long("project_id"), m.Str("title"), m.Str("description"), m.Str("state"),
        m.Str("source_branch"), m.Str("target_branch"), m?["author"].Str("name") ?? "", m.Str("web_url"),
        m.Str("detailed_merge_status"), m?["draft"]?.GetValue<bool>() ?? false, Date(m, "updated_at"),
        m?["head_pipeline"]?["status"]?.ToString() ?? m?["pipeline"]?["status"]?.ToString());

    public async Task<List<GitLabMergeRequest>> GetMergeRequestsAsync(string project, string state = "opened", string? sourceBranch = null, CancellationToken ct = default)
    {
        var q = $"projects/{Id(project)}/merge_requests?state={state}&per_page=50&order_by=updated_at";
        if (!string.IsNullOrEmpty(sourceBranch)) q += "&source_branch=" + Uri.EscapeDataString(sourceBranch);
        var arr = (await GetAsync(q, ct))?.AsArray() ?? [];
        return arr.Select(MapMr).ToList();
    }

    /// <summary>Offene MRs in denen ich Reviewer bin.</summary>
    public async Task<List<GitLabMergeRequest>> GetReviewRequestsAsync(CancellationToken ct = default)
    {
        var me = await GetMeAsync(ct);
        var arr = (await GetAsync($"merge_requests?state=opened&scope=all&reviewer_username={Uri.EscapeDataString(me.Username)}&per_page=50", ct))?.AsArray() ?? [];
        return arr.Select(MapMr).ToList();
    }

    public async Task<List<GitLabMergeRequest>> GetMyMergeRequestsAsync(CancellationToken ct = default)
    {
        var arr = (await GetAsync("merge_requests?state=opened&scope=created_by_me&per_page=50", ct))?.AsArray() ?? [];
        return arr.Select(MapMr).ToList();
    }

    public async Task<string> GetMergeRequestDiffAsync(string project, long iid, int maxChars = 120_000, CancellationToken ct = default)
    {
        var n = await GetAsync($"projects/{Id(project)}/merge_requests/{iid}/changes", ct);
        var sb = new StringBuilder();
        foreach (var c in n?["changes"]?.AsArray() ?? [])
        {
            sb.AppendLine($"diff --git a/{c.Str("old_path")} b/{c.Str("new_path")}");
            sb.AppendLine($"--- a/{c.Str("old_path")}");
            sb.AppendLine($"+++ b/{c.Str("new_path")}");
            sb.AppendLine(c.Str("diff"));
            if (sb.Length > maxChars) { sb.AppendLine("... (gekürzt)"); break; }
        }
        return sb.ToString();
    }

    public Task PostMergeRequestNoteAsync(string project, long iid, string body, CancellationToken ct = default)
        => PostAsync($"projects/{Id(project)}/merge_requests/{iid}/notes", new { body }, ct);

    public async Task<GitLabMergeRequest> CreateMergeRequestAsync(string project, string source, string target, string title, string description, CancellationToken ct = default)
    {
        var n = await PostAsync($"projects/{Id(project)}/merge_requests",
            new { source_branch = source, target_branch = target, title, description, remove_source_branch = true }, ct);
        return MapMr(n);
    }

    /// <summary>Extrahiert "group/sub/repo" aus einer Remote-URL (https oder ssh).</summary>
    public static string? ProjectPathFromRemote(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var url = remoteUrl.Trim();
        string path;
        if (url.StartsWith("git@") || (url.Contains(':') && !url.Contains("://")))
            path = url[(url.IndexOf(':') + 1)..];
        else if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            path = u.AbsolutePath;
        else return null;
        path = path.Trim('/');
        if (path.EndsWith(".git")) path = path[..^4];
        return path.Length > 0 ? path : null;
    }
}
