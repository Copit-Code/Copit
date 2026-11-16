using Copit.Core.Models;

namespace Copit.Core.Services;

/// <summary>Ordner der Projektliste. Pfad mit '/', z. B. "Kunde A/Backend".</summary>
public class ProjectFolder
{
    public string Path { get; set; } = "";
    public bool Collapsed { get; set; }
}

/// <summary>Zeile der gruppierten Projektliste: Ordner oder Projekt, mit Einrückungstiefe.</summary>
public record ProjectTreeRow(bool IsFolder, string Path, string Name, int Depth, CopitProject? Project, int Count, bool Collapsed);

/// <summary>Gruppierung von Projekten in (verschachtelte) Ordner: Baum aufbauen, auf-/zuklappen, verschieben, umbenennen, löschen.</summary>
public static class ProjectFolders
{
    public static string Normalize(string? path) =>
        string.Join('/', (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static string Leaf(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
    private static bool IsInside(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Alle Ordnerpfade inkl. übergeordneter Ordner (aus gespeicherten Ordnern und Projekten).</summary>
    public static List<string> AllPaths(ProjectsData d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in d.Folders.Select(f => f.Path).Concat(d.Projects.Select(p => p.Group)))
        {
            var p = Normalize(raw);
            while (p.Length > 0) { set.Add(p); p = Parent(p); }
        }
        return [.. set.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static ProjectFolder? Find(ProjectsData d, string path) => d.Folders.FirstOrDefault(f => Normalize(f.Path).Equals(Normalize(path), StringComparison.OrdinalIgnoreCase));

    public static bool IsCollapsed(ProjectsData d, string path) => Find(d, path)?.Collapsed ?? false;

    /// <summary>Flache Liste für die Anzeige: Ordner zuerst (alphabetisch), dann Projekte des Ordners (zuletzt geöffnet zuerst). Zugeklappte Ordner ohne Inhalt.</summary>
    public static List<ProjectTreeRow> Flatten(ProjectsData d)
    {
        var paths = AllPaths(d);
        var rows = new List<ProjectTreeRow>();
        void Walk(string folder, int depth)
        {
            foreach (var child in paths.Where(p => Parent(p).Equals(folder, StringComparison.OrdinalIgnoreCase)))
            {
                var collapsed = IsCollapsed(d, child);
                var count = d.Projects.Count(p => IsInside(Normalize(p.Group), child));
                rows.Add(new ProjectTreeRow(true, child, Leaf(child), depth, null, count, collapsed));
                if (!collapsed) Walk(child, depth + 1);
            }
            foreach (var p in d.Projects.Where(p => Normalize(p.Group).Equals(folder, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.LastOpened))
                rows.Add(new ProjectTreeRow(false, folder, p.Name, depth, p, 0, false));
        }
        Walk("", 0);
        return rows;
    }

    public static void Add(ProjectsData d, string path)
    {
        path = Normalize(path);
        if (path.Length > 0 && Find(d, path) == null) d.Folders.Add(new ProjectFolder { Path = path });
    }

    /// <summary>Klappt einen Ordner auf/zu und gibt den neuen Zustand zurück (true = zugeklappt).</summary>
    public static bool Toggle(ProjectsData d, string path)
    {
        var f = Find(d, path);
        if (f == null) d.Folders.Add(f = new ProjectFolder { Path = Normalize(path) });
        f.Collapsed = !f.Collapsed;
        return f.Collapsed;
    }

    public static void Move(ProjectsData d, CopitProject p, string folder)
    {
        p.Group = Normalize(folder);
        Add(d, p.Group);
    }

    /// <summary>Benennt einen Ordner um bzw. verschiebt ihn (inkl. Unterordner und Projekte).</summary>
    public static void Rename(ProjectsData d, string oldPath, string newPath)
    {
        oldPath = Normalize(oldPath); newPath = Normalize(newPath);
        if (oldPath.Length == 0 || newPath.Length == 0 || oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) return;
        if (IsInside(newPath, oldPath)) throw new InvalidOperationException("Ein Ordner kann nicht in sich selbst verschoben werden.");
        string Map(string p) => newPath + Normalize(p)[oldPath.Length..];
        foreach (var p in d.Projects.Where(p => IsInside(Normalize(p.Group), oldPath))) p.Group = Map(p.Group);
        foreach (var f in d.Folders.Where(f => IsInside(Normalize(f.Path), oldPath))) f.Path = Map(f.Path);
        Dedupe(d);
    }

    /// <summary>Löscht einen Ordner; Projekte und Unterordner rücken eine Ebene nach oben.</summary>
    public static void Delete(ProjectsData d, string path)
    {
        path = Normalize(path);
        if (path.Length == 0) return;
        var parent = Parent(path);
        string Map(string p) { var rest = Normalize(p)[path.Length..].TrimStart('/'); return Normalize(parent.Length == 0 ? rest : rest.Length == 0 ? parent : parent + "/" + rest); }
        foreach (var p in d.Projects.Where(p => IsInside(Normalize(p.Group), path))) p.Group = Map(p.Group);
        d.Folders.RemoveAll(f => Normalize(f.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
        foreach (var f in d.Folders.Where(f => IsInside(Normalize(f.Path), path))) f.Path = Map(f.Path);
        Dedupe(d);
    }

    private static void Dedupe(ProjectsData d)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        d.Folders.RemoveAll(f => (f.Path = Normalize(f.Path)).Length == 0 || !seen.Add(f.Path));
    }
}
