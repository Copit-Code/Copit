using System.Text.Json.Nodes;
using Copit.Core.Models;

namespace Copit.Core.Services;

/// <summary>Wem gehört ein Ticket, wenn sich mehrere Projekte ein Epic teilen?</summary>
public enum TicketOwner
{
    /// <summary>Gehört zu diesem Projekt.</summary>
    Own,
    /// <summary>Keinem Projekt sicher zugeordnet – wird angezeigt, aber markiert.</summary>
    Unassigned,
    /// <summary>Gehört zu einem anderen Projekt mit demselben Epic – wird ausgeblendet.</summary>
    Other,
}

/// <summary>
/// Abgrenzung von Projekten mit gemeinsamem Epic. Reihenfolge der Regeln:
/// 1. manuelle Zuordnung in Copit,
/// 2. Jira-Filter des Projekts (Component, Label, JQL),
/// 3. Code: Commits/Branches mit dem Ticket-Key im Repo des Projekts (oder ein Agent am Ticket),
/// 4. sonst „nicht zugeordnet“. Hat ein Epic nur ein Projekt und keinen Filter, gehört ihm alles.
/// </summary>
public static class TicketScope
{
    /// <summary>JQL aus Component, Label und Zusatzfilter des Projekts (null = kein Filter).</summary>
    public static string? FilterJql(CopitProject p)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.TicketComponent)) parts.Add($"component = \"{Esc(p.TicketComponent)}\"");
        if (!string.IsNullOrWhiteSpace(p.TicketLabel)) parts.Add($"labels = \"{Esc(p.TicketLabel)}\"");
        if (!string.IsNullOrWhiteSpace(p.TicketJql)) parts.Add($"({p.TicketJql.Trim()})");
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static string Esc(string s) => s.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Alle Projekte mit demselben Epic (inkl. <paramref name="p"/>).</summary>
    public static List<CopitProject> Siblings(IEnumerable<CopitProject> all, CopitProject p) =>
        string.IsNullOrWhiteSpace(p.EpicKey)
            ? [p]
            : [.. all.Where(x => x.Id == p.Id || string.Equals(x.EpicKey.Trim(), p.EpicKey.Trim(), StringComparison.OrdinalIgnoreCase))
                     .DistinctBy(x => x.Id)];

    /// <summary>Muss Copit für dieses Projekt überhaupt abgrenzen?</summary>
    public static bool NeedsScoping(CopitProject p, IReadOnlyList<CopitProject> siblings) =>
        FilterJql(p) != null || siblings.Count > 1;

    /// <param name="jiraMatches">Projekt-ID → Tickets, die zum Jira-Filter des Projekts passen (nur Projekte mit Filter).</param>
    /// <param name="repoKeys">Projekt-ID → Ticket-Keys aus Commits/Branches/Agents des Projekts.</param>
    public static TicketOwner Classify(string key, CopitProject p, IReadOnlyList<CopitProject> siblings,
        IReadOnlyDictionary<string, string> assignments,
        IReadOnlyDictionary<string, ISet<string>> jiraMatches,
        IReadOnlyDictionary<string, ISet<string>> repoKeys)
    {
        if (!NeedsScoping(p, siblings)) return TicketOwner.Own;
        var others = siblings.Where(s => s.Id != p.Id).ToList();

        // 1. Manuelle Zuordnung (nur auf Projekte, die es noch gibt und dasselbe Epic haben)
        if (assignments.TryGetValue(key, out var assigned) || assignments.TryGetValue(key.ToUpperInvariant(), out assigned))
        {
            if (assigned == p.Id) return TicketOwner.Own;
            if (others.Any(o => o.Id == assigned)) return TicketOwner.Other;
        }

        // 2. Jira-Filter
        if (In(jiraMatches, p.Id, key)) return TicketOwner.Own;
        if (others.Any(o => In(jiraMatches, o.Id, key))) return TicketOwner.Other;

        // 3. Code im Repo / Agent am Ticket
        var mine = In(repoKeys, p.Id, key);
        var theirs = others.Any(o => In(repoKeys, o.Id, key));
        if (mine && !theirs) return TicketOwner.Own;
        if (theirs && !mine) return TicketOwner.Other;

        // 4. Nichts Eindeutiges. Mit eigenem Jira-Filter und ohne Geschwister: Filter entscheidet.
        if (others.Count == 0) return mine ? TicketOwner.Own : TicketOwner.Other;
        return TicketOwner.Unassigned;
    }

    private static bool In(IReadOnlyDictionary<string, ISet<string>> map, string projectId, string key) =>
        map.TryGetValue(projectId, out var set) && set.Contains(key);

    /// <summary>Felder, die ein neues Ticket des Projekts bekommt, damit es im Filter landet.</summary>
    public static void AddCreateFields(CopitProject p, IDictionary<string, JsonNode?> fields)
    {
        if (!string.IsNullOrWhiteSpace(p.TicketComponent) && !fields.ContainsKey("components"))
            fields["components"] = new JsonArray(new JsonObject { ["name"] = p.TicketComponent.Trim() });
        if (!string.IsNullOrWhiteSpace(p.TicketLabel) && !fields.ContainsKey("labels"))
            fields["labels"] = new JsonArray(JsonValue.Create(p.TicketLabel.Trim().Replace(' ', '_')));
    }
}

/// <summary>Ergebnis der Abgrenzung für ein Projekt.</summary>
public class ScopedTickets
{
    public static readonly ScopedTickets All = new() { Active = false };
    /// <summary>false = keine Abgrenzung nötig (Epic gehört nur diesem Projekt).</summary>
    public bool Active { get; init; } = true;
    public Dictionary<string, TicketOwner> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TicketOwner Of(string key) => !Active ? TicketOwner.Own : Owners.GetValueOrDefault(key, TicketOwner.Unassigned);
    public bool Visible(string key, bool includeUnassigned = true) => Of(key) switch
    {
        TicketOwner.Own => true,
        TicketOwner.Unassigned => includeUnassigned,
        _ => false,
    };
    /// <summary>Hinweise (z. B. Jira-Filter fehlerhaft).</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>Lädt die Daten für <see cref="TicketScope"/>: Jira-Filter, Repo-Keys, Agents und manuelle Zuordnung.</summary>
public class TicketScopeService(ConnectionHub hub, DataService data, SessionStore sessions, TicketChangesService changes)
{
    public async Task<ScopedTickets> ScopeAsync(CopitProject p, IEnumerable<JiraIssue> epicIssues, CancellationToken ct = default)
    {
        var siblings = TicketScope.Siblings(data.Projects.Value.Projects, p);
        if (!TicketScope.NeedsScoping(p, siblings)) return ScopedTickets.All;
        var result = new ScopedTickets();

        var jiraMatches = new Dictionary<string, ISet<string>>();
        if (hub.Jira is { } jira)
            foreach (var s in siblings)
            {
                if (TicketScope.FilterJql(s) is not { } jql) continue;
                try { jiraMatches[s.Id] = (await jira.GetEpicIssuesAsync(s.EpicKey, ct, jql)).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception e) { result.Warnings.Add($"Jira-Filter von „{s.Name}“ ungültig: {e.Message}"); }
            }

        var repoKeys = new Dictionary<string, ISet<string>>();
        foreach (var s in siblings)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (GitService.IsRepo(s.SourceFolder))
                try { keys.UnionWith(await changes.RepoTicketKeysAsync(s.SourceFolder, ct: ct)); } catch { /* Repo nicht lesbar */ }
            foreach (var a in sessions.ForProject(s.Id)) if (!string.IsNullOrWhiteSpace(a.Ticket)) keys.Add(a.Ticket.Trim());
            repoKeys[s.Id] = keys;
        }
        // Gleiches Repo bei mehreren Projekten sagt nichts über die Zuordnung aus
        var sameRepo = siblings.Where(s => !string.IsNullOrWhiteSpace(s.SourceFolder))
            .GroupBy(s => Path.GetFullPath(s.SourceFolder).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        if (sameRepo) repoKeys.Clear();

        var assignments = data.Projects.Value.TicketAssignments;
        foreach (var i in epicIssues)
            result.Owners[i.Key] = TicketScope.Classify(i.Key, p, siblings, assignments, jiraMatches, repoKeys);
        return result;
    }

    /// <summary>Ticket manuell einem Projekt zuordnen (null = Zuordnung entfernen).</summary>
    public async Task AssignAsync(string key, string? projectId)
    {
        var map = data.Projects.Value.TicketAssignments;
        key = key.Trim().ToUpperInvariant();
        if (projectId == null) map.Remove(key); else map[key] = projectId;
        await data.Projects.SaveAsync();
    }
}
