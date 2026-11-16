using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Vernetzte Projekt-Doku unter /docs: Code &amp; Architektur, Arbeit &amp; Verlauf, Projektwissen.</summary>
public class DocsView : ContentView, IActivatable
{
    private readonly DocsService _docs;
    private readonly DataService _data;
    private readonly SessionManager _mgr;
    private readonly ToastService _toast;
    private readonly ConnectionHub _hub;
    private readonly Grid _root = new();
    private CopitProject? _project;
    private bool _busy;
    private readonly List<string> _steps = [];
    private DocsResult? _last;

    public DocsView(DocsService docs, DataService data, SessionManager mgr, ToastService toast, ConnectionHub hub)
    {
        _docs = docs; _data = data; _mgr = mgr; _toast = toast; _hub = hub;
        BackgroundColor = C("Bg");
        Content = _root;
    }

    public void OnActivated(object? parameter)
    {
        var projects = _data.Projects.Value.Projects;
        _project = parameter is string id ? projects.FirstOrDefault(p => p.Id == id) ?? _project : _project ?? projects.OrderByDescending(p => p.LastOpened).FirstOrDefault();
        Render();
    }

    private string Folder => _project == null ? "docs" : string.IsNullOrWhiteSpace(_docs.For(_project).Folder) ? "docs" : _docs.For(_project).Folder.Trim('/', '\\');
    private string DocsDir => _project == null ? "" : Path.Combine(_project.SourceFolder, Folder);

    private void Render()
    {
        var page = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 1100, HorizontalOptions = LayoutOptions.Start };
        var projects = _data.Projects.Value.Projects.OrderBy(p => p.Name).ToList();
        page.Add(Row((Stack(3, Caption("Doku"), H1("Vernetzte Projektdoku"),
            Wrap("Markdown unter /docs im Repo – Code, Tickets, Commits, MRs, Agent-Sessions, Zeitbuchungen und Projektwissen gegenseitig verlinkt. GitHub/GitLab rendern alles direkt.", 12, "Muted")), Star)));
        if (projects.Count == 0) { page.Add(Ui.Card(Text("Erst ein Projekt mit Quellordner anlegen.", 13, "Muted"))); Show(page); return; }
        _project ??= projects[0];
        var s = _docs.For(_project);

        page.Add(Row(
            (Stack(4, Caption("Projekt"), Pick(projects.Select(p => $"{p.Name}{(string.IsNullOrEmpty(p.EpicKey) ? "" : " · " + p.EpicKey)}").ToList(), projects.IndexOf(_project), i => { _project = projects[i]; _last = null; Render(); }, 320)), Auto),
            (Stack(4, Caption("Ordner"), Input(s.Folder, "docs", v => { s.Folder = v.Trim(); _ = _data.Docs.SaveAsync(); }, 120)), Auto),
            (Stack(4, Caption("Verlauf (Tage)"), Input(s.HistoryDays.ToString(), "180", v => { if (int.TryParse(v, out var d) && d is > 0 and < 3650) { s.HistoryDays = d; _ = _data.Docs.SaveAsync(); } }, 100)), Auto),
            (new BoxView(), Star)));

        View Section(string title, string desc, bool on, Action<bool> set, string icon) => Ui.Card(Row(
            (Glyph(icon, 18, on ? "Accent" : "Muted"), Auto),
            (Stack(2, Text(title, 13.5, "Text", true), Wrap(desc, 11.5, "Muted")), Star),
            (Toggle(on, v => { set(v); _ = _data.Docs.SaveAsync(); }), Auto)), new Thickness(14, 12));
        var sections = new Grid { ColumnSpacing = 12, ColumnDefinitions = [new(Star), new(Star), new(Star)] };
        sections.Add(Section("Code & Architektur", "Module, Abhängigkeiten (Mermaid), Dateien mit Typen – verlinkt auf Quellcode und Tickets.", s.Architecture, v => s.Architecture = v, "code"), 0);
        sections.Add(Section("Arbeit & Verlauf", "Pro Ticket: Commits, Code, Branches, MRs, KI-Sessions, Zeitbuchungen, verwandte Tickets.", s.History, v => s.History = v, "ticket"), 1);
        sections.Add(Section("Projektwissen", "Konventionen, Zuordnungen, Rückfragen-Wissen, Agent-Regeln, Notizen, Entscheidungen (ADR).", s.Knowledge, v => s.Knowledge = v, "book"), 2);
        page.Add(sections);
        page.Add(Row((Toggle(s.IncludeWorklogs, v => { s.IncludeWorklogs = v; _ = _data.Docs.SaveAsync(); }), Auto),
            (Wrap($"Jira-Zeitbuchungen aller Personen in die Ticket-Seiten aufnehmen{(_hub.Jira == null ? " (Jira nicht verbunden)" : "")}", 12, "Text2"), Star)));

        var actions = HStack(8,
            _busy ? new ActivityIndicator { IsRunning = true, Color = C("Accent"), WidthRequest = 20, HeightRequest = 20 } : Primary("Doku aktualisieren", () => _ = RunAsync(false, false)),
            Btn("+ Doku-Agent vertiefen", () => _ = RunAsync(true, false), tooltip: "Erzeugt die Fakten und startet einen Agent, der Modul-Beschreibungen, Architektur-Überblick und Entscheidungen schreibt – nur unter docs/"),
            Ghost("Aktualisieren & committen", () => _ = RunAsync(false, true)),
            Ghost("Ordner öffnen", () => Dialogs.OpenFolder(DocsDir)),
            Ghost("Im Editor öffnen", () => Dialogs.OpenInEditor(_project.SourceFolder)));
        page.Add(actions);

        if (_steps.Count > 0)
            page.Add(Ui.Card(Stack(4, _steps.Select((x, i) => (View)Row((Glyph(i < _steps.Count - 1 || !_busy ? "check" : "loader", 12, i < _steps.Count - 1 || !_busy ? "Green" : "Accent"), Auto), (Text(x, 12, "Text2"), Star))).ToArray()), new Thickness(14, 10)));
        if (_last != null)
        {
            var res = Stack(6, Text($"{_last.Pages} Seiten geschrieben · {_last.Modules} Module · {_last.Tickets} Tickets · {_last.Commits} Commits", 13, "Text", true));
            foreach (var w in _last.Warnings) res.Add(Wrap("Hinweis: " + w, 11.5, "Amber"));
            page.Add(Ui.Card(res, stroke: "Green"));
        }
        else if (s.LastRun != null) page.Add(Text($"Zuletzt {s.LastRun:dd.MM.yyyy HH:mm} · {s.LastResult}", 11.5, "Muted"));

        // Inhalt der Doku
        page.Add(Caption("Inhalt"));
        page.Add(Tree());
        page.Add(Wrap("Alles zwischen den Markern <!-- copit:generated --> wird bei jedem Lauf neu erzeugt. Texte außerhalb (eigene Ergänzungen oder vom Doku-Agent) bleiben erhalten. " +
                      "Per Automation „Doku aktualisieren“ lässt sich das z. B. nach jedem fertigen Agent oder wöchentlich auslösen.", 11, "Muted"));
        Show(page);
    }

    private View Tree()
    {
        var dir = DocsDir;
        if (!Directory.Exists(dir)) return Ui.Card(Text("Noch keine Doku – „Doku aktualisieren“ erzeugt sie.", 12.5, "Muted"));
        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var g in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f)
                     .GroupBy(f => f.Contains('/') ? f[..f.IndexOf('/')] : ""))
        {
            var files = g.ToList();
            var header = Row((Glyph(g.Key == "" ? "book" : g.Key == "architecture" ? "code" : g.Key == "history" ? "ticket" : "note", 13, "Accent"), Auto),
                (Mono((g.Key == "" ? Folder : $"{Folder}/{g.Key}").ToUpperInvariant(), 10.5, "Text", true), Star), (Mono($"{files.Count} Seiten", 10.5, "Muted"), Auto));
            header.Padding = new Thickness(12, 10, 12, 4);
            list.Add(header);
            foreach (var f in files.Take(g.Key == "history" ? 40 : 200))
            {
                var full = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
                var title = File.ReadLines(full).FirstOrDefault(l => l.StartsWith("# "))?[2..] ?? f;
                var row = Row((Text(title, 12.5, "Text2"), Star), (Mono(f, 10.5, "Muted"), Auto));
                row.Padding = new Thickness(34, 5, 12, 5);
                Hover(row);
                OnTap(row, () => Dialogs.OpenInEditor(full));
                list.Add(row);
            }
            if (g.Key == "history" && files.Count > 40) list.Add(new Label { Text = $"… {files.Count - 40} weitere Ticket-Seiten", FontSize = 11.5, TextColor = C("Muted"), Padding = new Thickness(34, 4) });
            list.Add(Line());
        }
        return Ui.Card(list, new Thickness(0));
    }

    private void Show(View page)
    {
        _root.Children.Clear();
        _root.Add(new ScrollView { Content = page });
    }

    private async Task RunAsync(bool agent, bool commit)
    {
        if (_project == null || _busy) return;
        if (!Directory.Exists(_project.SourceFolder)) { _toast.Show("Quellordner fehlt", _project.SourceFolder, NoticeLevel.Warning); return; }
        _busy = true; _steps.Clear(); _last = null; Render();
        try
        {
            void Progress(string x) => MainThread.BeginInvokeOnMainThread(() => { if (_steps.LastOrDefault() != x) { _steps.Add(x); if (_steps.Count > 8) _steps.RemoveAt(0); Render(); } });
            var project = _project;
            _last = await Task.Run(() => _docs.GenerateAsync(project, Progress));
            if (agent || commit) await _mgr.UpdateDocsAsync(project, _docs, agent, commit, Progress, skipGenerate: true);
            _toast.Success("Doku aktualisiert", $"{_last.Pages} Seiten · {DocsDir}{(agent ? " · Doku-Agent läuft (Board)" : "")}");
        }
        catch (Exception e) { _toast.Error(e, "Doku"); }
        finally { _busy = false; Render(); }
    }
}
