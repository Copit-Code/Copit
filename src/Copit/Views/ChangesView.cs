using Copit.Controls;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Öffnet die Änderungsansicht für ein Projekt und optional direkt ein Ticket.</summary>
public record ChangesRequest(string ProjectId, string? Ticket = null);

/// <summary>
/// Code-Änderungen pro Ticket: Übersicht aller Tickets mit Commits, Detailansicht mit Commits, Dateibaum und Diff
/// (Gesamtänderung, einzelner Commit oder noch nicht committete Agent-Arbeit).
/// </summary>
public class ChangesView : ContentView, IActivatable
{
    private readonly DataService _data;
    private readonly TicketChangesService _svc;
    private readonly SessionManager _mgr;
    private readonly ConnectionHub _hub;
    private readonly Grid _root = new();
    private CopitProject? _project;
    private int _days = 90;
    private string _filter = "";
    private List<TicketSummary>? _summaries;
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);

    // Detail
    private string? _ticket;
    private TicketChanges? _changes;
    private string _source = "net";            // net | commit:<sha> | ws:<dir>
    private List<FileDiff> _files = [];
    private FileDiff? _file;
    private bool _allFiles;
    private int _load;

    public ChangesView(DataService data, TicketChangesService svc, SessionManager mgr, ConnectionHub hub)
    {
        _data = data; _svc = svc; _mgr = mgr; _hub = hub;
        BackgroundColor = C("Bg");
        Content = _root;
    }

    public void OnActivated(object? parameter)
    {
        var projects = _data.Projects.Value.Projects;
        if (parameter is ChangesRequest r)
        {
            var p = projects.FirstOrDefault(x => x.Id == r.ProjectId);
            if (p != null && p != _project) { _project = p; _summaries = null; }
            if (r.Ticket != null) { _ = OpenTicketAsync(r.Ticket); return; }
            _ticket = null;
        }
        _project ??= projects.Where(p => GitService.IsRepo(p.SourceFolder)).OrderByDescending(p => p.LastOpened).FirstOrDefault();
        if (_ticket == null) _ = LoadSummariesAsync();
        else Render();
    }

    private void Show(View v) { _root.Children.Clear(); _root.Add(v); }

    // ============================================================ Übersicht

    private async Task LoadSummariesAsync()
    {
        var load = ++_load;
        _ticket = null;
        if (_project == null || !GitService.IsRepo(_project.SourceFolder)) { Render(); return; }
        if (_summaries == null) { Render(loading: true); _summaries = await Task.Run(() => _svc.SummariesAsync(_project.SourceFolder, _days)); }
        if (load != _load) return;
        Render();
        _ = LoadTitlesAsync(_summaries.Take(40).Select(s => s.Ticket));
    }

    private async Task LoadTitlesAsync(IEnumerable<string> keys)
    {
        if (_hub.Jira == null) return;
        var changed = false;
        foreach (var k in keys.Where(k => !_titles.ContainsKey(k)).ToList())
        {
            try { _titles[k] = (await _hub.Jira.GetIssueAsync(k))?.Summary ?? ""; changed = true; } catch { _titles[k] = ""; }
        }
        if (changed) MainThread.BeginInvokeOnMainThread(() => Render());
    }

    private void Render(bool loading = false)
    {
        if (_ticket != null) { RenderDetail(); return; }
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30) };
        page.Add(Stack(3, Caption("Änderungen"), H1("Code-Änderungen pro Ticket"),
            Wrap("Alle Commits mit dem Ticket-Key in der Message oder auf einem Ticket-Branch – über alle Branches, dazu nicht committete Arbeit der Agents.", 12, "Muted")));
        var projects = _data.Projects.Value.Projects.Where(p => GitService.IsRepo(p.SourceFolder)).OrderBy(p => p.Name).ToList();
        if (projects.Count == 0) { page.Add(Ui.Card(Text("Kein Projekt mit Git-Repository vorhanden.", 13, "Muted"))); Show(new ScrollView { Content = page }); return; }
        _project ??= projects[0];
        page.Add(Row(
            (Stack(4, Caption("Projekt"), Pick(projects.Select(p => p.Name).ToList(), Math.Max(0, projects.IndexOf(_project)), i => { _project = projects[i]; _summaries = null; _ = LoadSummariesAsync(); }, 300)), Auto),
            (Stack(4, Caption("Zeitraum"), Seg([("30", "30 Tage"), ("90", "90 Tage"), ("365", "1 Jahr")], _days.ToString(), k => { _days = int.Parse(k); _summaries = null; _ = LoadSummariesAsync(); })), Auto),
            (Stack(4, Caption("Suche"), Input(_filter, "Ticket oder Titel", v => { _filter = v; Render(); }, 240)), Auto),
            (new BoxView(), Star),
            (IconBtn("refresh", () => { _summaries = null; _ = LoadSummariesAsync(); }, "Neu laden"), Auto)));
        if (loading || _summaries == null) { page.Add(Text("Lade Commits…", 13, "Muted")); Show(new ScrollView { Content = page }); return; }

        var list = _summaries.Where(s => _filter.Length == 0 || s.Ticket.Contains(_filter, StringComparison.OrdinalIgnoreCase) || _titles.GetValueOrDefault(s.Ticket, "").Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) { page.Add(Ui.Card(Text("Keine Commits mit Ticket-Key in diesem Zeitraum.", 13, "Muted"))); Show(new ScrollView { Content = page }); return; }
        var max = Math.Max(1, list.Max(s => s.Added + s.Removed));
        var table = new VerticalStackLayout { Spacing = 0 };
        table.Add(Header());
        foreach (var s in list)
        {
            var row = Row(
                (Mono(s.Ticket, 12, "Accent", true), Px(110)),
                (Text(_titles.GetValueOrDefault(s.Ticket, ""), 13, "Text"), Star),
                (Mono($"{s.Commits}", 11.5), Px(70)),
                (Mono($"{s.Files}", 11.5), Px(70)),
                (ChangeBar(s.Added, s.Removed, max), Px(190)),
                (Mono(s.Last.ToString("dd.MM.yy HH:mm"), 11, "Muted"), Px(110)),
                (Text(string.Join(", ", s.Authors.Take(3)), 11.5, "Muted"), Px(170)));
            row.ColumnSpacing = 12; row.Padding = new Thickness(10, 11);
            table.Add(OnTap(Hover(row), () => _ = OpenTicketAsync(s.Ticket)));
            table.Add(Line());
        }
        page.Add(table);
        Show(new ScrollView { Content = page });

        static View Header()
        {
            var h = Row((Caption("Ticket"), Px(110)), (Caption("Titel"), Star), (Caption("Commits"), Px(70)), (Caption("Dateien"), Px(70)), (Caption("Zeilen"), Px(190)), (Caption("Zuletzt"), Px(110)), (Caption("Von"), Px(170)));
            h.ColumnSpacing = 12; h.Padding = new Thickness(10, 0, 10, 8);
            return h;
        }
    }

    /// <summary>+/− Zahlen mit Balken (grün/rot), Länge relativ zum größten Eintrag.</summary>
    private static View ChangeBar(int add, int del, int max)
    {
        var rest = Math.Max(0, max - add - del);
        var bar = new Grid { HeightRequest = 6, WidthRequest = 80, ColumnSpacing = 0, VerticalOptions = LayoutOptions.Center, BackgroundColor = C("Line"),
            ColumnDefinitions = [new(new GridLength(Math.Max(add, 0.0001), GridUnitType.Star)), new(new GridLength(Math.Max(del, 0.0001), GridUnitType.Star)), new(new GridLength(Math.Max(rest, 0.0001), GridUnitType.Star))] };
        bar.Add(new BoxView { Color = C("Green") }, 0);
        bar.Add(new BoxView { Color = C("Red") }, 1);
        var g = HStack(8, Mono($"+{add}", 11, "Green"), Mono($"−{del}", 11, "Red"), bar);
        return g;
    }

    // ============================================================ Detail eines Tickets

    private async Task OpenTicketAsync(string ticket)
    {
        if (_project == null) return;
        var load = ++_load;
        _ticket = ticket.ToUpperInvariant(); _changes = null; _files = []; _file = null; _source = "net"; _allFiles = false;
        RenderDetail();
        _ = LoadTitlesAsync([_ticket]);
        var repo = _project.SourceFolder;
        var agents = _mgr.Agents.ToList();
        var ch = await Task.Run(() => _svc.LoadAsync(repo, _ticket, agents));
        if (load != _load) return;
        _changes = ch;
        if (ch.Commits.Count == 0 && ch.Workspaces.Count > 0) _source = "ws:" + ch.Workspaces[0].Dir;
        await LoadFilesAsync(load);
    }

    private async Task LoadFilesAsync(int load)
    {
        if (_project == null || _changes == null) return;
        var repo = _project.SourceFolder; var ch = _changes; var src = _source;
        _files = []; _file = null;
        RenderDetail(loadingFiles: true);
        var files = await Task.Run(async () =>
            src.StartsWith("commit:") ? await _svc.CommitDiffAsync(repo, src[7..])
            : src.StartsWith("ws:") ? await _svc.WorkspaceDiffAsync(src[3..])
            : await _svc.NetDiffAsync(repo, ch));
        if (load != _load) return;
        _files = files;
        _file = files.FirstOrDefault();
        RenderDetail();
    }

    private void SetSource(string s) { if (_source == s) return; _source = s; _ = LoadFilesAsync(++_load); }

    private void RenderDetail(bool loadingFiles = false)
    {
        var t = _ticket!;
        var left = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(18, 18, 14, 24) };
        left.Add(Row((IconBtn("chevron-left", () => _ = LoadSummariesAsync(), "Zur Übersicht"), Auto), (Caption("Änderungen"), Star)));
        left.Add(Stack(3, Mono(t, 15, "Accent", true), Wrap(_titles.GetValueOrDefault(t, ""), 13, "Text")));
        if (_changes == null) { left.Add(Text("Suche Commits…", 12.5, "Muted")); ShowDetail(left, Text("", 12)); return; }

        var ch = _changes;
        var stats = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new(Star), new(Star), new(Star)] };
        stats.Add(Stat("Commits", ch.Commits.Count.ToString()), 0);
        stats.Add(Stat("Dateien", ch.FileCount.ToString()), 1);
        stats.Add(Stat("Zeilen", $"+{ch.Added} −{ch.Removed}"), 2);
        left.Add(stats);
        if (ch.Branches.Count > 0) left.Add(Wrap("Branches: " + string.Join(", ", ch.Branches), 11.5, "Muted"));

        // Quelle wählen
        left.Add(Caption("Ansicht"));
        View SourceRow(string key, string icon, string title, string sub)
        {
            var on = _source == key;
            var r = Row((Glyph(icon, 13, on ? "Accent" : "Muted"), Auto), (Stack(1, Text(title, 12.5, on ? "Text" : "Text2", on), Mono(sub, 10.5, "Muted")), Star));
            r.ColumnSpacing = 8; r.Padding = new Thickness(8, 6);
            if (on) r.BackgroundColor = C("AccentSoft");
            return OnTap(Hover(r, on ? C("AccentSoft") : null), () => SetSource(key));
        }
        if (ch.Commits.Count > 0) left.Add(SourceRow("net", "diff", "Gesamtänderung des Tickets", $"{ch.Commits.Count} Commits zusammengefasst"));
        foreach (var w in ch.Workspaces) left.Add(SourceRow("ws:" + w.Dir, "pencil", "Nicht committet", w.Title));
        if (ch.Commits.Count > 0)
        {
            left.Add(Caption("Commits"));
            foreach (var c in ch.Commits.AsEnumerable().Reverse())
                left.Add(SourceRow("commit:" + c.Sha, "commit", c.Subject, $"{c.Short} · {c.Author} · {c.Date:dd.MM. HH:mm} · +{c.Added} −{c.Removed}"));
        }
        if (ch.Commits.Count == 0 && ch.Workspaces.Count == 0) left.Add(Ui.Card(Wrap("Keine Commits und keine offene Agent-Arbeit zu diesem Ticket gefunden. Tipp: Ticket-Key in die Commit-Message oder den Branchnamen schreiben.", 12, "Muted")));

        // Dateien
        if (loadingFiles) left.Add(Text("Lade Dateien…", 12.5, "Muted"));
        else if (_files.Count > 0)
        {
            left.Add(Row((Caption($"Dateien ({_files.Count})"), Star), (Chip(_allFiles ? "Einzeln" : "Alle zeigen", () => { _allFiles = !_allFiles; RenderDetail(); }), Auto)));
            var max = Math.Max(1, _files.Max(f => f.Added + f.Removed));
            foreach (var g in _files.GroupBy(f => f.Folder))
            {
                if (g.Key.Length > 0) left.Add(Row((Glyph("folder", 11, "Muted"), Auto), (Mono(g.Key, 10.5, "Muted"), Star)));
                foreach (var f in g)
                {
                    var on = f == _file && !_allFiles;
                    var r = Row(
                        (StatusBadge(f.Status), Px(18)),
                        (Text(f.Name, 12.5, on ? "Text" : "Text2", on), Star),
                        (MiniBar(f.Added, f.Removed, max), Px(54)));
                    r.ColumnSpacing = 8; r.Padding = new Thickness(g.Key.Length > 0 ? 18 : 6, 5, 6, 5);
                    if (on) r.BackgroundColor = C("AccentSoft");
                    ToolTipProperties.SetText(r, $"{f.Path}  +{f.Added} −{f.Removed}");
                    left.Add(OnTap(Hover(r, on ? C("AccentSoft") : null), () => { _file = f; _allFiles = false; RenderDetail(); }));
                }
            }
        }

        // Rechts: Diff
        var right = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(18, 18, 22, 30) };
        var repoDir = _source.StartsWith("ws:") ? _source[3..] : _project!.SourceFolder;
        foreach (var f in _allFiles ? _files : _file != null ? [_file] : [])
        {
            var head = Row(
                (StatusBadge(f.Status), Auto),
                (Stack(1, Mono(f.Path, 12.5, "Text", true), f.OldPath != null && f.Status == "R" ? Mono("vorher: " + f.OldPath, 10.5, "Muted") : new BoxView { HeightRequest = 0 }), Star),
                (HStack(6, Mono($"+{f.Added}", 11.5, "Green"), Mono($"−{f.Removed}", 11.5, "Red")), Auto),
                (IconBtn("code", () => Dialogs.OpenFileAt(Path.Combine(repoDir, f.Path), FirstLine(f)), "Im Editor öffnen"), Auto));
            head.ColumnSpacing = 10;
            right.Add(head);
            var dv = new RichDiffView();
            dv.LineTapped += line => Dialogs.OpenFileAt(Path.Combine(repoDir, f.Path), line);
            dv.Show(f);
            right.Add(dv);
        }
        if (!loadingFiles && _files.Count == 0 && (_changes.Commits.Count > 0 || _changes.Workspaces.Count > 0)) right.Add(Text("Keine Dateiänderungen in dieser Ansicht.", 13, "Muted"));
        ShowDetail(left, right);
    }

    private static int FirstLine(FileDiff f) => f.Hunks.SelectMany(h => h.Lines).FirstOrDefault(l => l.Kind == DiffLineKind.Added)?.NewNo ?? f.Hunks.FirstOrDefault()?.Lines.FirstOrDefault(l => l.NewNo != null)?.NewNo ?? 1;

    private void ShowDetail(View left, View right)
    {
        var g = new Grid { ColumnDefinitions = [new(new GridLength(360)), new(new GridLength(1)), new(GridLength.Star)] };
        g.Add(new ScrollView { Content = left, BackgroundColor = C("Bg2") }, 0);
        g.Add(new BoxView { Color = C("Line") }, 1);
        g.Add(new ScrollView { Content = right }, 2);
        Show(g);
    }

    private static View StatusBadge(string status)
    {
        var (txt, color) = status switch { "A" => ("N", "Green"), "D" => ("G", "Red"), "R" => ("U", "Amber"), _ => ("Ä", "Accent") };
        var l = Mono(txt, 10, color, true);
        l.HorizontalTextAlignment = TextAlignment.Center;
        ToolTipProperties.SetText(l, status switch { "A" => "neu", "D" => "gelöscht", "R" => "umbenannt", _ => "geändert" });
        return l;
    }

    private static View MiniBar(int add, int del, int max)
    {
        var w = 50.0 * Math.Max(add + del, 1) / max;
        var g = new Grid { HeightRequest = 5, WidthRequest = Math.Max(4, w), HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center, ColumnSpacing = 0,
            ColumnDefinitions = [new(new GridLength(Math.Max(add, 0.0001), GridUnitType.Star)), new(new GridLength(Math.Max(del, 0.0001), GridUnitType.Star))] };
        g.Add(new BoxView { Color = C("Green") }, 0);
        g.Add(new BoxView { Color = C("Red") }, 1);
        return g;
    }
}
