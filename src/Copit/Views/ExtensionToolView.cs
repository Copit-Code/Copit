using Copit.Controls;
using Copit.Core.Ai;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>
/// Tool einer Extension (Route "ext:&lt;extension&gt;/&lt;tool&gt;"). Art "mcp-prompt": MCP-Server bewusst auswählen,
/// Auswertung prompten, Ergebnis live als Markdown, Verlauf und Export.
/// </summary>
public class ExtensionToolView : ContentView, IActivatable
{
    private readonly ExtensionService _ext;
    private readonly McpAnalysisService _mcp;
    private readonly DataService _data;
    private readonly ToastService _toast;
    private readonly Grid _root = new();
    private string _route = "";
    private InstalledExtension? _extension;
    private ExtTool? _tool;
    private CopitProject? _project;
    private List<McpServerInfo> _servers = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private string _prompt = "";
    private string _serverFilter = "";
    private bool _readFiles;
    private CancellationTokenSource? _cts;
    private McpRun? _shown;

    // Live-Ausgabe
    private readonly MarkdownView _result = new();
    private readonly Label _status = Mono("", 11, "Muted");
    private readonly VerticalStackLayout _calls = new() { Spacing = 2 };
    private string _live = "";

    public ExtensionToolView(ExtensionService ext, McpAnalysisService mcp, DataService data, ToastService toast)
    {
        _ext = ext; _mcp = mcp; _data = data; _toast = toast;
        BackgroundColor = C("Bg");
        Content = _root;
    }

    private string Key => _route.StartsWith("ext:") ? _route[4..] : _route;
    private bool Running => _cts != null;

    public void OnActivated(object? parameter)
    {
        if (parameter is string route && route != _route) { _route = route; _tool = null; }
        var t = _ext.Tools.FirstOrDefault(x => x.Route == _route);
        if (t.Tool == null) { Show(Stack(8, H2("Tool nicht verfügbar"), Wrap("Die Extension ist deaktiviert oder wurde entfernt.", 12.5, "Muted"))); return; }
        var first = _tool == null;
        _extension = t.Ext; _tool = t.Tool;
        if (first)
        {
            _readFiles = _tool.ReadFiles;
            _project ??= _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened).FirstOrDefault();
            LoadServers();
            _selected.Clear();
            if (_mcp.Store.Value.Selections.TryGetValue(Key, out var sel)) foreach (var s in sel.Where(s => _servers.Any(x => x.Name.Equals(s, StringComparison.OrdinalIgnoreCase)))) _selected.Add(s);
        }
        Render();
    }

    private void LoadServers() => _servers = _mcp.Servers(_project != null && Directory.Exists(_project.SourceFolder) ? _project.SourceFolder : null);

    private void Show(View v) { _root.Children.Clear(); _root.Add(v); }

    private void Render()
    {
        if (_tool == null) return;
        var g = new Grid { ColumnDefinitions = [new(new GridLength(380)), new(new GridLength(1)), new(GridLength.Star)] };
        g.Add(new ScrollView { Content = Left(), BackgroundColor = C("Bg2") }, 0);
        g.Add(new BoxView { Color = C("Line") }, 1);
        g.Add(new ScrollView { Content = Right() }, 2);
        Show(g);
    }

    // ------------------------------------------------------------ Links: Projekt + MCP-Server

    private View Left()
    {
        var s = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(18, 20, 14, 24) };
        s.Add(Stack(3, Caption(_extension!.Manifest.Name), H2(_tool!.Title), Wrap(_tool.Description, 11.5, "Muted")));
        var projects = _data.Projects.Value.Projects.OrderBy(p => p.Name).ToList();
        if (projects.Count > 0)
            s.Add(Stack(4, Caption("Projekt (Arbeitsordner, .mcp.json)"), Pick(["Ohne Projekt", .. projects.Select(p => p.Name)], _project == null ? 0 : projects.IndexOf(_project) + 1, i =>
            {
                _project = i == 0 ? null : projects[i - 1];
                LoadServers();
                _selected.RemoveWhere(n => !_servers.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
                Render();
            })));

        s.Add(Row((Caption($"MCP-Server ({_selected.Count}/{_servers.Count} gewählt)"), Star),
            (Chip("Alle", () => { foreach (var x in _servers) _selected.Add(x.Name); Render(); }), Auto),
            (Chip("Keine", () => { _selected.Clear(); Render(); }), Auto)));
        if (_servers.Count > 6) s.Add(Input(_serverFilter, "Server suchen", v => { _serverFilter = v; Render(); }));
        if (_servers.Count == 0)
            s.Add(Ui.Card(Wrap("Keine MCP-Server gefunden. Lege einen in Claude Code an (`claude mcp add …`), in .mcp.json im Projekt oder hier über „Server hinzufügen“.", 12, "Muted")));
        foreach (var grp in _servers.Where(x => _serverFilter.Length == 0 || x.Name.Contains(_serverFilter, StringComparison.OrdinalIgnoreCase) || x.Summary.Contains(_serverFilter, StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.Source))
        {
            s.Add(Caption(grp.Key));
            foreach (var srv in grp)
            {
                var on = _selected.Contains(srv.Name);
                var row = Row(
                    (Glyph(on ? "check-circle" : "circle", 15, on ? "Accent" : "Muted"), Auto),
                    (Stack(1, Text(srv.Name, 13, on ? "Text" : "Text2", on), Mono($"{srv.Type} · {srv.Summary}", 10.5, "Muted")), Star));
                row.ColumnSpacing = 10; row.Padding = new Thickness(8, 6);
                if (on) row.BackgroundColor = C("AccentSoft");
                if (srv.Source == "Copit")
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    row.Add(IconBtn("trash", () => _ = RemoveServerAsync(srv), "Entfernen", 12), 2);
                }
                s.Add(OnTap(Hover(row, on ? C("AccentSoft") : null), () => { if (!_selected.Remove(srv.Name)) _selected.Add(srv.Name); Render(); }));
            }
        }
        s.Add(Btn("Server hinzufügen", () => _ = AddServerAsync()));
        s.Add(Row((Toggle(_readFiles, v => _readFiles = v), Auto), (Wrap("Dateien im Projektordner lesen erlauben (nur lesen)", 12, "Text2"), Star)));
        return s;
    }

    private async Task AddServerAsync()
    {
        var name = await Dialogs.Prompt("MCP-Server hinzufügen", "Name (so heißen später die Tools: mcp__name__…)", "", "z. B. postgres");
        if (string.IsNullOrWhiteSpace(name)) return;
        var def = await Dialogs.Prompt("MCP-Server hinzufügen", "Befehlszeile, URL oder JSON im Claude-Format", "", "npx -y @modelcontextprotocol/server-postgres postgresql://localhost/db");
        if (string.IsNullOrWhiteSpace(def)) return;
        try
        {
            _mcp.Store.Value.Servers[name.Trim()] = McpCatalog.ParseServer(def);
            await _mcp.Store.SaveAsync();
            LoadServers(); _selected.Add(name.Trim()); Render();
        }
        catch (Exception e) { _toast.Show("Server ungültig", e.Message, NoticeLevel.Warning); }
    }

    private async Task RemoveServerAsync(McpServerInfo s)
    {
        if (!await Dialogs.Confirm("Server entfernen", $"„{s.Name}“ aus Copit entfernen?", "Entfernen")) return;
        _mcp.Store.Value.Servers.Remove(s.Name);
        await _mcp.Store.SaveAsync();
        _selected.Remove(s.Name); LoadServers(); Render();
    }

    // ------------------------------------------------------------ Rechts: Prompt, Ergebnis, Verlauf

    private View Right()
    {
        var s = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(22, 20, 26, 30), MaximumWidthRequest = 1100, HorizontalOptions = LayoutOptions.Fill };
        if (_tool!.Presets.Count > 0)
        {
            var presets = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var p in _tool.Presets)
            {
                var c = Chip(p.Title, () => ApplyPreset(p));
                c.Margin = new Thickness(0, 0, 6, 6);
                presets.Add(c);
            }
            s.Add(Stack(6, Caption("Vorlagen"), presets));
        }
        s.Add(Stack(6, Caption("Welche Auswertung möchtest du?"), Area(_prompt, _tool.Placeholder.Length > 0 ? _tool.Placeholder : "Beschreibe die gewünschte Auswertung…", v => _prompt = v, 110)));
        var selectedNames = _servers.Where(x => _selected.Contains(x.Name)).Select(x => x.Name).ToList();
        s.Add(Row(
            (Running ? Btn("Stoppen", () => _cts?.Cancel(), "Danger") : Primary("Auswertung starten", () => _ = RunAsync()), Auto),
            (Wrap(selectedNames.Count == 0 ? "Kein Server gewählt" : "Verwendet nur: " + string.Join(", ", selectedNames), 12, selectedNames.Count == 0 ? "Amber" : "Text2"), Star)));

        if (Running || _shown != null)
        {
            var head = Row((Caption(Running ? "Ergebnis (läuft…)" : $"Ergebnis · {_shown!.Date:dd.MM.yyyy HH:mm}"), Star));
            if (!Running && _shown != null)
            {
                head.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                head.Add(HStack(6,
                    Mono($"{Money(_shown.CostUsd)} · {Tok(_shown.Tokens)} Tokens · {_shown.DurationMs / 1000.0:0} s", 11, "Muted"),
                    IconBtn("copy", () => { _ = Clipboard.SetTextAsync(_shown.Result); _toast.Success("Kopiert", ""); }, "Kopieren"),
                    IconBtn("file", () => _ = SaveAsync(_shown), "Als Markdown speichern")), 1);
            }
            s.Add(head);
            if (!Running && _shown != null) _result.Text = _shown.IsError ? "**Fehler:** " + _shown.Result : _shown.Result;
            s.Add(Ui.Card(_result, new Thickness(16, 14)));
            if (Running) s.Add(_status);
            if (_calls.Children.Count > 0 || (_shown?.ToolCalls.Count ?? 0) > 0)
            {
                if (!Running && _shown != null) { _calls.Children.Clear(); foreach (var g in _shown.ToolCalls.GroupBy(x => x)) _calls.Add(Mono($"{g.Key}{(g.Count() > 1 ? $"  ×{g.Count()}" : "")}", 10.5, "Muted")); }
                s.Add(Stack(4, Caption("Tool-Aufrufe"), _calls));
            }
        }

        var history = _mcp.Store.Value.History.Where(h => h.Tool == Key).Take(20).ToList();
        if (history.Count > 0)
        {
            s.Add(Caption("Verlauf"));
            foreach (var h in history)
            {
                var r = Row(
                    (Mono(h.Date.ToString("dd.MM. HH:mm"), 11, "Muted"), Px(90)),
                    (Text(h.Prompt.Replace('\n', ' '), 12.5, h == _shown ? "Text" : "Text2", h == _shown), Star),
                    (Mono(string.Join(", ", h.Servers), 10.5, "Muted"), Px(200)),
                    (Mono(Money(h.CostUsd), 10.5, h.IsError ? "Red" : "Muted"), Px(60)));
                r.ColumnSpacing = 10; r.Padding = new Thickness(8, 6);
                s.Add(OnTap(Hover(r), () =>
                {
                    if (Running) return;
                    _shown = h; _prompt = h.Prompt;
                    _selected.Clear(); foreach (var n in h.Servers) _selected.Add(n);
                    Render();
                }));
            }
        }
        return s;
    }

    private void ApplyPreset(ExtToolPreset p)
    {
        _prompt = p.Prompt;
        var matches = _servers.Where(x => p.Servers.Any(w => x.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count > 0) { _selected.Clear(); foreach (var m in matches) _selected.Add(m.Name); }
        else if (p.Servers.Count > 0) _toast.Show("Passender Server fehlt", $"Für diese Vorlage wird z. B. {string.Join(" oder ", p.Servers)} erwartet – bitte selbst wählen.", NoticeLevel.Info);
        Render();
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_prompt)) { _toast.Show("Prompt fehlt", "Beschreibe, welche Auswertung du möchtest.", NoticeLevel.Warning); return; }
        var servers = _servers.Where(x => _selected.Contains(x.Name)).ToList();
        if (servers.Count == 0) { _toast.Show("Kein MCP-Server gewählt", "Wähle links mindestens einen Server aus.", NoticeLevel.Warning); return; }
        _cts = new CancellationTokenSource();
        _shown = null; _live = ""; _result.Text = ""; _calls.Children.Clear(); _status.Text = "startet Claude…";
        Render();
        var dir = _project != null && Directory.Exists(_project.SourceFolder) ? _project.SourceFolder : null;
        try
        {
            var run = await Task.Run(() => _mcp.RunAsync(Key, _tool!.SystemPrompt, servers, _prompt, dir, _readFiles, _project?.Id, e => MainThread.BeginInvokeOnMainThread(() => OnEvent(e)), _cts.Token));
            _shown = run;
        }
        catch (OperationCanceledException) { _toast.Show("Abgebrochen", "", NoticeLevel.Info); }
        catch (Exception e) { _toast.Show("Auswertung fehlgeschlagen", e.Message, NoticeLevel.Error); }
        finally { _cts = null; Render(); }
    }

    private void OnEvent(AgentEvent e)
    {
        switch (e)
        {
            case AgentTextDelta d: _live += d.Text; _result.Text = _live; _status.Text = "schreibt…"; break;
            case AgentThinking: _status.Text = "denkt nach…"; break;
            case AgentToolUse t:
                _status.Text = "ruft " + t.Name + " auf…";
                _calls.Add(Mono(t.Name, 10.5, "Muted"));
                break;
            case AgentToolResult r when r.IsError: _calls.Add(Mono("  Fehler: " + (r.Content.Length > 160 ? r.Content[..160] : r.Content), 10.5, "Red")); break;
        }
    }

    private async Task SaveAsync(McpRun run)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Copit", "Auswertungen");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{run.Date:yyyy-MM-dd HHmm} {string.Concat((_tool?.Title ?? "Auswertung").Split(Path.GetInvalidFileNameChars()))}.md");
            var md = $"---\ntool: \"{_tool?.Title}\"\ndate: {run.Date:s}\nservers: [{string.Join(", ", run.Servers.Select(s => $"\"{s}\""))}]\ncost_usd: {run.CostUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}\n---\n\n# {_tool?.Title}\n\n> {run.Prompt.Replace("\n", "\n> ")}\n\n{run.Result}\n";
            await File.WriteAllTextAsync(file, md);
            _toast.Success("Gespeichert", file);
            Dialogs.OpenFolder(dir);
        }
        catch (Exception e) { _toast.Show("Speichern fehlgeschlagen", e.Message, NoticeLevel.Error); }
    }
}
