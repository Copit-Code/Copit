using System.Text.Json;
using System.Text.RegularExpressions;
using Copit.Core.Models;

namespace Copit.Core.Services;

/// <summary>Listenpreise je Modellfamilie (USD / 1 Mio. Tokens). Cache-Lesen 10 %, Cache-Schreiben 125 %.</summary>
public static class Pricing
{
    public static Dictionary<string, (double Input, double Output)> Prices { get; set; } = new()
    {
        ["opus"] = (5, 25), ["fable"] = (5, 25), ["sonnet"] = (3, 15), ["haiku"] = (1, 5), ["default"] = (3, 15),
    };

    public static (double Input, double Output) For(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        foreach (var (k, v) in Prices) if (k != "default" && m.Contains(k)) return v;
        return Prices.GetValueOrDefault("default", (3, 15));
    }

    public static double Cost(long input, long output, long cacheRead, long cacheWrite, string? model)
    {
        var p = For(model);
        return (input * p.Input + output * p.Output + cacheRead * p.Input * 0.1 + cacheWrite * p.Input * 1.25) / 1_000_000d;
    }

    public static string PrettyModel(string m)
    {
        var x = Regex.Match(m, @"claude-(\w+)-(\d+)-(\d+)");
        return x.Success ? $"{char.ToUpper(x.Groups[1].Value[0])}{x.Groups[1].Value[1..]} {x.Groups[2].Value}.{x.Groups[3].Value}" : m;
    }
}

/// <summary>Eine Agent-Session für die Activity-Ansicht (Claude-Code-Transkript oder Copit-Agent).</summary>
public class ActSession
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "claude";
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string? Branch { get; set; }
    public string Entry { get; set; } = "";
    public List<string> Models { get; set; } = [];
    public int Prompts { get; set; }
    public int ToolCalls { get; set; }
    public int Subagents { get; set; }
    /// <summary>Aktive Zeitspannen (Pausen > 10 min getrennt).</summary>
    public List<(DateTime Start, DateTime End)> Segments { get; set; } = [];
    /// <summary>Kosten je Segment (aus den Token-Zeitpunkten) – exakte Zuordnung zu Tagen/Wochen.</summary>
    public List<double> SegmentCosts { get; set; } = [];
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double CostUsd { get; set; }
    public bool CostExact { get; set; }
    public string? AgentId { get; set; }
    public string? Ticket { get; set; }
    public bool Running { get; set; }
    public long TotalTokens => InputTokens + OutputTokens + CacheRead + CacheWrite;
}

/// <summary>Nutzungs-Datensatz für Budgets: aktive Spanne mit Kosten und Projektpfad.</summary>
public record UsageRecord(DateTime Start, DateTime End, double CostUsd, string Path, string Project, string Provider);

/// <summary>
/// Liest Claude-Code-Transkripte (~/.claude/projects/**.jsonl) und baut daraus Sessions mit
/// aktiver Zeit, Tokens und Kosten. AI time = Wanduhrzeit, in der mindestens ein Agent arbeitete (Pausen > 10 min zählen nicht).
/// </summary>
public class ActivityService
{
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);
    private readonly string _root;
    private readonly Dictionary<string, (DateTime Mtime, long Size, Dictionary<string, RawSession> Sessions)> _cache = [];
    private readonly object _lock = new();

    private readonly IReadOnlyList<string> _copilotRoots;

    /// <param name="root">Claude-Transkripte (Standard ~/.claude/projects).</param>
    /// <param name="copilotRoots">Copilot-CLI-Ordner (Standard: $COPILOT_HOME, ~/.copilot, $XDG_STATE_HOME/.copilot).</param>
    public ActivityService(string? root = null, IReadOnlyList<string>? copilotRoots = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        _copilotRoots = copilotRoots ?? DefaultCopilotRoots();
    }

    public static List<string> DefaultCopilotRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string?> { Environment.GetEnvironmentVariable("COPILOT_HOME"), Path.Combine(home, ".copilot") };
        if (Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg) list.Add(Path.Combine(xdg, ".copilot"));
        return [.. list.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x!)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private class Point
    {
        public DateTime Ts; public long In, Out, CR, CW; public string? Model; public bool HasUsage;
        /// <summary>Copilot: Premium Requests und AI Credits (Kosten werden mit den aktuellen Preisen berechnet).</summary>
        public double Premium, Credits; public bool Copilot;
    }

    private class RawSession
    {
        public string Source = "claude";
        public string Id = "", Cwd = "", Entry = "Claude Code", Title = "";
        public string? Branch;
        public List<Point> Points = [];
        public HashSet<string> Models = [];
        public int Prompts, ToolCalls, Subagents;
    }

    private static readonly Dictionary<string, string> EntryNames = new() { ["claude-desktop"] = "Desktop app", ["cli"] = "CLI", ["sdk-ts"] = "SDK", ["sdk-py"] = "SDK", ["claude-vscode"] = "VS Code" };

    public static List<(DateTime, DateTime)> SegmentsOf(IEnumerable<DateTime> times)
    {
        var t = times.OrderBy(x => x).ToList();
        var outp = new List<(DateTime S, DateTime E)>();
        foreach (var x in t)
        {
            if (outp.Count > 0 && x - outp[^1].E <= Pause) outp[^1] = (outp[^1].S, x);
            else outp.Add((x, x));
        }
        return outp.Select(s => (s.S, s.E - s.S < TimeSpan.FromSeconds(30) ? s.S.AddSeconds(30) : s.E)).ToList();
    }

    /// <summary>Vereinigung von Intervallen (parallele Arbeit zählt einmal).</summary>
    public static TimeSpan Union(IEnumerable<(DateTime Start, DateTime End)> iv)
    {
        var total = TimeSpan.Zero;
        DateTime cs = DateTime.MinValue, ce = DateTime.MinValue;
        foreach (var (a, b) in iv.OrderBy(x => x.Start))
        {
            if (a > ce) { if (ce > cs) total += ce - cs; cs = a; ce = b; }
            else if (b > ce) ce = b;
        }
        if (ce > cs) total += ce - cs;
        return total;
    }

    public static IEnumerable<(DateTime Start, DateTime End)> Clip(IEnumerable<(DateTime Start, DateTime End)> segs, DateTime from, DateTime to) =>
        segs.Select(s => (s.Start < from ? from : s.Start, s.End > to ? to : s.End)).Where(s => s.Item2 > s.Item1);

    private Dictionary<string, RawSession> ParseFile(string file)
    {
        var map = new Dictionary<string, RawSession>();
        var titles = new Dictionary<string, string>();
        var seen = new HashSet<string>();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (!line.Contains("\"sessionId\"")) continue;
            JsonElement j;
            try { j = JsonDocument.Parse(line).RootElement; } catch { continue; }
            var type = j.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            var sid = j.TryGetProperty("sessionId", out var si) ? si.GetString() ?? "" : "";
            if (type is "custom-title" or "ai-title")
            {
                var t = j.TryGetProperty("customTitle", out var ct) ? ct.GetString() : j.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                if (!string.IsNullOrWhiteSpace(t)) titles[sid] = t!;
                continue;
            }
            if (type is not ("user" or "assistant")) continue;
            if (!j.TryGetProperty("timestamp", out var tsEl) || !DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)) continue;
            ts = ts.ToLocalTime();
            if (!map.TryGetValue(sid, out var s))
            {
                var ep = j.TryGetProperty("entrypoint", out var e) ? e.GetString() ?? "" : "";
                s = new RawSession { Id = sid, Cwd = j.TryGetProperty("cwd", out var cw) ? cw.GetString() ?? "" : "", Entry = EntryNames.GetValueOrDefault(ep, string.IsNullOrEmpty(ep) ? "Claude Code" : ep) };
                map[sid] = s;
            }
            if (j.TryGetProperty("gitBranch", out var gb) && gb.GetString() is { Length: > 0 } b && b != "HEAD") s.Branch = b;
            var side = j.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
            var msg = j.TryGetProperty("message", out var m) ? m : default;
            if (type == "user")
            {
                var isMeta = j.TryGetProperty("isMeta", out var im) && im.ValueKind == JsonValueKind.True;
                string? text = null;
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c))
                {
                    if (c.ValueKind == JsonValueKind.String) text = c.GetString();
                    else if (c.ValueKind == JsonValueKind.Array)
                        foreach (var x in c.EnumerateArray())
                            if (x.TryGetProperty("type", out var xt) && xt.GetString() == "text") { text = x.TryGetProperty("text", out var xx) ? xx.GetString() : ""; break; }
                }
                if (text != null && !side && !isMeta)
                {
                    s.Prompts++;
                    if (s.Title == "") s.Title = Regex.Replace(Regex.Replace(text, "<[^>]+>", " "), @"\s+", " ").Trim() is var tt2 && tt2.Length > 80 ? tt2[..80] : Regex.Replace(Regex.Replace(text, "<[^>]+>", " "), @"\s+", " ").Trim();
                }
                s.Points.Add(new Point { Ts = ts });
            }
            else
            {
                string? model = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("model", out var mo) ? mo.GetString() : null;
                if (model != null && model != "<synthetic>") s.Models.Add(model);
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var x in c.EnumerateArray())
                        if (x.TryGetProperty("type", out var xt) && xt.GetString() == "tool_use")
                        {
                            s.ToolCalls++;
                            if (x.TryGetProperty("name", out var nm) && nm.GetString() is "Task" or "Agent") s.Subagents++;
                        }
                var key = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("id", out var id) ? id.GetString() : j.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                // Streaming schreibt eine Nachricht in mehreren Zeilen → Usage nur einmal pro message.id
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("usage", out var us) && key != null && seen.Add(key))
                {
                    long L(string p) => us.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                    s.Points.Add(new Point { Ts = ts, HasUsage = true, In = L("input_tokens"), Out = L("output_tokens"), CR = L("cache_read_input_tokens"), CW = L("cache_creation_input_tokens"), Model = model });
                }
                else s.Points.Add(new Point { Ts = ts });
            }
        }
        foreach (var (k, t) in titles) if (map.TryGetValue(k, out var s)) s.Title = t;
        return map;
    }

    private List<RawSession> Transcripts(DateTime from, DateTime to)
    {
        var outp = new List<RawSession>();
        if (!Directory.Exists(_root)) return outp;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            FileInfo fi;
            try { fi = new FileInfo(f); } catch { continue; }
            if (fi.LastWriteTime < from) continue;
            Dictionary<string, RawSession> sessions;
            lock (_lock)
            {
                if (!_cache.TryGetValue(f, out var c) || c.Mtime != fi.LastWriteTimeUtc || c.Size != fi.Length)
                {
                    try { c = (fi.LastWriteTimeUtc, fi.Length, ParseFile(f)); _cache[f] = c; } catch { continue; }
                }
                sessions = c.Sessions;
            }
            outp.AddRange(sessions.Values.Where(s => s.Points.Any(p => p.Ts >= from && p.Ts < to)));
        }
        return outp;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string N(string p) { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p.TrimEnd('\\', '/'); } }
        var x = N(a); var y = N(b);
        return x.Equals(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || y.StartsWith(x + "/", StringComparison.OrdinalIgnoreCase) || y.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copilot-CLI-Sessions: &lt;root&gt;/session-state/&lt;id&gt;/events.jsonl (seit Copilot CLI 0.0.380).</summary>
    private List<RawSession> CopilotTranscripts(DateTime from, DateTime to)
    {
        var outp = new List<RawSession>();
        foreach (var root in _copilotRoots)
        {
            var dir = Path.Combine(root, "session-state");
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateDirectories(dir).Select(d => Path.Combine(d, "events.jsonl")).Where(File.Exists).ToList(); } catch { continue; }
            foreach (var f in files)
            {
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                if (fi.LastWriteTime < from) continue;
                Dictionary<string, RawSession> sessions;
                lock (_lock)
                {
                    if (!_cache.TryGetValue(f, out var c) || c.Mtime != fi.LastWriteTimeUtc || c.Size != fi.Length)
                    {
                        try { c = (fi.LastWriteTimeUtc, fi.Length, ParseCopilotFile(f)); _cache[f] = c; } catch { continue; }
                    }
                    sessions = c.Sessions;
                }
                outp.AddRange(sessions.Values.Where(s => s.Points.Any(p => p.Ts >= from && p.Ts < to)));
            }
        }
        return outp.GroupBy(s => s.Id).Select(g => g.First()).ToList();   // gleicher Ordner über mehrere Wurzeln (Symlink)
    }

    /// <summary>Liest eine events.jsonl der Copilot CLI. Tokens/Kosten stehen nur in session.shutdown (je Prozesslauf).</summary>
    private static Dictionary<string, RawSession> ParseCopilotFile(string file)
    {
        var s = new RawSession { Source = "copilot", Id = "copilot:" + Path.GetFileName(Path.GetDirectoryName(file)), Entry = "Copilot CLI" };
        var shutdowns = new Dictionary<long, Point>();   // sessionStartTime → letzter Stand dieses Prozesslaufs
        var agents = new HashSet<string>();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            JsonElement j;
            try { j = JsonDocument.Parse(line).RootElement; } catch { continue; }
            if (j.ValueKind != JsonValueKind.Object) continue;
            var type = j.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (!j.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String ||
                !DateTime.TryParse(tsEl.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)) continue;
            ts = ts.ToLocalTime();
            var data = j.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double Num(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            void Context(JsonElement ctx)
            {
                if (ctx.ValueKind != JsonValueKind.Object) return;
                if (Str(ctx, "cwd") is { Length: > 0 } cwd) s.Cwd = Str(ctx, "gitRoot") is { Length: > 0 } gr ? gr : cwd;
                if (Str(ctx, "branch") is { Length: > 0 } br && br != "HEAD") s.Branch = br;
            }
            if (j.TryGetProperty("agentId", out var ag) && ag.GetString() is { Length: > 0 } agId) agents.Add(agId);

            switch (type)
            {
                case "session.start":
                case "session.resume":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("context", out var ctx)) Context(ctx);
                    if (Str(data, "selectedModel") is { Length: > 0 } sm) s.Models.Add(sm);
                    if (Str(data, "sessionId") is { Length: > 0 } sid) s.Id = "copilot:" + sid;
                    continue;   // kein Arbeitszeitpunkt
                case "session.context_changed":
                    Context(data);
                    continue;
                case "session.model_change":
                    if (Str(data, "newModel") is { Length: > 0 } nm) s.Models.Add(nm);
                    continue;
                case "user.message":
                    if (j.TryGetProperty("agentId", out _)) break;
                    s.Prompts++;
                    if (s.Title == "" && Str(data, "content") is { Length: > 0 } text)
                    {
                        var t = Regex.Replace(text, @"\s+", " ").Trim();
                        s.Title = t.Length > 80 ? t[..80] : t;
                    }
                    break;
                case "assistant.message":
                    if (Str(data, "model") is { Length: > 0 } m) s.Models.Add(m);
                    break;
                case "tool.execution_start":
                    s.ToolCalls++;
                    break;
                case "session.shutdown":
                {
                    long inp = 0, outp = 0, cr = 0, cw = 0;
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("modelMetrics", out var mm) && mm.ValueKind == JsonValueKind.Object)
                        foreach (var mp in mm.EnumerateObject())
                            if (mp.Value.TryGetProperty("usage", out var u))
                            { inp += (long)Num(u, "inputTokens"); outp += (long)Num(u, "outputTokens"); cr += (long)Num(u, "cacheReadTokens"); cw += (long)Num(u, "cacheWriteTokens"); }
                    // inputTokens enthält den Cache – frische Eingabe = Rest
                    var fresh = inp >= cr + cw ? inp - cr - cw : inp;
                    var startKey = (long)Num(data, "sessionStartTime");
                    shutdowns[startKey] = new Point
                    {
                        Ts = ts, HasUsage = true, Copilot = true, In = fresh, Out = outp, CR = cr, CW = cw, Model = Str(data, "currentModel"),
                        Premium = Num(data, "totalPremiumRequests"), Credits = Num(data, "totalNanoAiu") / 1e9,
                    };
                    continue;
                }
                default:
                    if (type.StartsWith("session.", StringComparison.Ordinal)) continue;
                    break;
            }
            s.Points.Add(new Point { Ts = ts });
        }
        s.Points.AddRange(shutdowns.Values);
        s.Points.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        s.Subagents = agents.Count;
        if (s.Title == "") s.Title = "Copilot-Session";
        return s.Points.Count == 0 ? [] : new() { [s.Id] = s };
    }

    public static string ProjectName(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "?";
        var parts = Path.GetFullPath(p).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault() ?? p;
        var prev = parts.Length > 1 ? parts[^2] : "";
        var generic = new[] { "git", "src", "repos", "source", "projects", "code", "dev", "users", "documents" };
        return prev == "" || generic.Contains(prev.ToLowerInvariant()) || prev.EndsWith(':') ? last : $"{prev}/{last}";
    }

    /// <summary>Sessions im Zeitraum: Claude-Transkripte + Copit-Agents ohne Transkript (z. B. Copilot).</summary>
    public List<ActSession> Build(DateTime from, DateTime to, IEnumerable<ChatSession> agents, Func<string, string?> agentRepo)
    {
        var list = agents.ToList();
        var bySession = list.Where(a => !string.IsNullOrEmpty(a.CliSessionId)).GroupBy(a => a.CliSessionId!).ToDictionary(g => g.Key, g => g.First());
        var outp = new List<ActSession>();
        var copilotAgents = list.Where(a => a.Provider == AiProvider.Copilot).ToList();
        foreach (var s in Transcripts(from, to).Concat(CopilotTranscripts(from, to)))
        {
            var pts = s.Points.Where(p => p.Ts >= from && p.Ts < to).ToList();
            if (pts.Count == 0) continue;
            ChatSession? agent = null;
            if (s.Source == "copilot")
            {
                // Von Copit gestartete Copilot-Läufe stehen auch hier – die zählt der Copit-Agent bereits
                if (copilotAgents.Any(a => SamePath(agentRepo(a.ProjectId), s.Cwd) && a.Runs.Any(r => r.Start <= s.Points[^1].Ts.AddMinutes(1) && (r.End ?? DateTime.Now) >= s.Points[0].Ts.AddMinutes(-1))))
                    continue;
            }
            else bySession.TryGetValue(s.Id, out agent);
            var a = new ActSession
            {
                Id = s.Id, Source = agent != null ? "copit" : s.Source, Title = agent?.Title ?? (s.Title == "" ? "Session" : s.Title),
                ProjectPath = agent != null ? agentRepo(agent.ProjectId) ?? s.Cwd : s.Cwd, Branch = agent?.Branch ?? s.Branch,
                Entry = agent != null ? "Copit" : s.Entry, Models = s.Models.Select(Pricing.PrettyModel).ToList(), Prompts = s.Prompts,
                ToolCalls = s.ToolCalls, Subagents = s.Subagents, Segments = SegmentsOf(pts.Select(p => p.Ts)), AgentId = agent?.Id, Ticket = agent?.Ticket,
            };
            a.Project = ProjectName(a.ProjectPath);
            a.SegmentCosts = a.Segments.Select(_ => 0d).ToList();
            foreach (var p in pts.Where(p => p.HasUsage))
            {
                a.InputTokens += p.In; a.OutputTokens += p.Out; a.CacheRead += p.CR; a.CacheWrite += p.CW;
                var c = p.Copilot
                    ? p.Premium * Math.Max(0, AgentRun.CopilotPrices.Premium) + p.Credits * Math.Max(0, AgentRun.CopilotPrices.Credit)
                    : Pricing.Cost(p.In, p.Out, p.CR, p.CW, p.Model);
                a.CostUsd += c;
                var idx = a.Segments.FindIndex(g => p.Ts >= g.Start && p.Ts <= g.End);
                if (idx < 0) idx = a.Segments.Count - 1;
                if (idx >= 0) a.SegmentCosts[idx] += c;
            }
            if (agent != null)
            {
                var runCost = agent.Runs.Where(r => r.Start >= from && r.Start < to).Sum(r => r.Cost);
                if (runCost > 0) { var f = a.CostUsd > 0 ? runCost / a.CostUsd : 0; a.SegmentCosts = a.SegmentCosts.Select(x => x * f).ToList(); a.CostUsd = runCost; a.CostExact = true; }
            }
            outp.Add(a);
        }
        var covered = outp.Where(x => x.AgentId != null).Select(x => x.AgentId).ToHashSet();
        foreach (var ag in list.Where(x => !covered.Contains(x.Id)))
        {
            var runs = ag.Runs.Where(r => r.Start < to && (r.End ?? DateTime.Now) >= from).ToList();
            if (runs.Count == 0) continue;
            var path = agentRepo(ag.ProjectId) ?? "";
            outp.Add(new ActSession
            {
                Id = "copit:" + ag.Id, Source = "copit", Title = ag.Title, ProjectPath = path, Project = ProjectName(path), Branch = ag.Branch,
                Entry = $"Copit · {ag.Provider}", Models = string.IsNullOrEmpty(ag.Model) ? [] : [Pricing.PrettyModel(ag.Model)], Prompts = runs.Count,
                Segments = runs.Select(r => (r.Start, r.End ?? DateTime.Now)).ToList(), InputTokens = runs.Sum(r => r.InputTokens), OutputTokens = runs.Sum(r => r.OutputTokens),
                CacheRead = runs.Sum(r => r.CacheRead), CacheWrite = runs.Sum(r => r.CacheWrite), CostUsd = runs.Sum(r => r.Cost), CostExact = ag.Provider == AiProvider.Claude,
                AgentId = ag.Id, Ticket = ag.Ticket,
            });
        }
        return outp.OrderByDescending(x => x.Segments.FirstOrDefault().Start).ToList();
    }

    public static List<UsageRecord> Records(IEnumerable<ActSession> sessions) =>
        sessions.SelectMany(s =>
        {
            var total = Math.Max(1, s.Segments.Sum(g => (g.End - g.Start).TotalMilliseconds));
            var exact = s.SegmentCosts.Count == s.Segments.Count && s.SegmentCosts.Sum() > 0;
            return s.Segments.Select((g, i) => new UsageRecord(g.Start, g.End,
                exact ? s.SegmentCosts[i] : s.CostUsd * ((g.End - g.Start).TotalMilliseconds / total), s.ProjectPath, s.Project, s.Entry));
        }).ToList();
}
