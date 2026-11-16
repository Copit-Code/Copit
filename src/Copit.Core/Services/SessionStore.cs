using System.Text.Json;
using Copit.Core.Models;
using Copit.Core.Storage;

namespace Copit.Core.Services;

/// <summary>Persistiert Chat-Sessions einzeln als JSON. Sessions überleben Neustarts.</summary>
public class SessionStore
{
    private readonly string _dir = AppPaths.Dir("sessions");
    private readonly Dictionary<string, ChatSession> _cache = [];
    private bool _loaded;

    public IReadOnlyList<ChatSession> All
    {
        get { EnsureLoaded(); return _cache.Values.OrderByDescending(s => s.UpdatedAt).ToList(); }
    }

    public IEnumerable<ChatSession> ForProject(string projectId) => All.Where(s => s.ProjectId == projectId && !s.Archived);

    public ChatSession? Get(string id) { EnsureLoaded(); return _cache.GetValueOrDefault(id); }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(f), Json.Options);
                if (s != null) _cache[s.Id] = s;
            }
            catch { }
        }
    }

    public ChatSession Create(CopitProject project, AiProvider provider, AgentMode mode)
    {
        EnsureLoaded();
        var s = new ChatSession { ProjectId = project.Id, Provider = provider, Mode = mode };
        _cache[s.Id] = s;
        Save(s);
        return s;
    }

    public void Save(ChatSession s)
    {
        s.UpdatedAt = DateTime.Now;
        var path = Path.Combine(_dir, s.Id + ".json");
        var tmp = path + ".tmp";
        lock (s)
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options));
        }
        File.Move(tmp, path, true);
    }

    public void Delete(ChatSession s)
    {
        _cache.Remove(s.Id);
        var path = Path.Combine(_dir, s.Id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Kurzbeschreibungen der Sessions eines Tages (Signal für Zeitbuchung).</summary>
    public IEnumerable<string> DescribeDay(DateOnly day, DataService data) =>
        All.Where(s => s.Messages.Any(m => DateOnly.FromDateTime(m.Timestamp) == day))
           .Select(s =>
           {
               var p = data.Project(s.ProjectId);
               var prompts = s.Messages.Where(m => m.Role == ChatRole.User && DateOnly.FromDateTime(m.Timestamp) == day)
                   .Select(m => m.Text.Length > 120 ? m.Text[..120] + "…" : m.Text).Take(4);
               return $"Projekt {p?.Name} (Epic {p?.EpicKey}) – Session \"{s.Title}\": {string.Join(" | ", prompts)}";
           });
}
