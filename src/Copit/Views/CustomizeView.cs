using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Customize: Permission-Regeln (Deny / Ask / Allow) mit Scope und Schalter + Git-Permissionsmatrix pro Repo.</summary>
public class CustomizeView : ContentView, IActivatable
{
    private readonly PermissionService _perms;
    private readonly DataService _data;
    private readonly SettingsService _settings;
    private readonly ToastService _toast;
    private readonly UpdateService _updates;
    private readonly Grid _root = new();
    private string _updateStatus = "";
    private string _tokenInput = "";
    private string _tab = "rules";
    private string _filter = "all";
    private string _search = "";
    private string _newPattern = "";
    private PermissionLevel _newLevel = PermissionLevel.Allow;
    private int _newScope;

    public CustomizeView(PermissionService perms, DataService data, SettingsService settings, ToastService toast, UpdateService updates)
    {
        _perms = perms; _data = data; _settings = settings; _toast = toast; _updates = updates;
        BackgroundColor = C("Bg");
        Content = _root;
    }

    public void OnActivated(object? parameter)
    {
        if (parameter is string t) _tab = t;
        Render();
    }

    private List<string> Repos() => _data.Projects.Value.Projects.Select(p => p.SourceFolder).Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Color LevelColor(PermissionLevel l) => l switch { PermissionLevel.Deny => C("Red"), PermissionLevel.Ask => C("Amber"), _ => C("Green") };

    private void Render()
    {
        var page = new Grid { ColumnDefinitions = [new(new GridLength(210)), new(new GridLength(1)), new(GridLength.Star)] };
        // Sub-Navigation
        var nav = Stack(2, Caption("Customize"));
        nav.Padding = new Thickness(14, 22, 10, 0);
        foreach (var (key, label, icon) in new[] { ("rules", "Permission rules", "lock"), ("matrix", "Git-Matrix", "branch"), ("agent", "Agent-Verhalten", "bot"), ("time", "Zeitbuchung", "clock"), ("updates", "Updates", "refresh") })
        {
            var active = _tab == key;
            var row = Row((Glyph(icon, 14, active ? "Text" : "Text2"), Auto), (Text(label, 13, active ? "Text" : "Text2"), Star));
            row.Padding = new Thickness(8, 7);
            row.BackgroundColor = active ? C("CardHover") : Colors.Transparent;
            var k = key;
            OnTap(row, () => { _tab = k; Render(); });
            nav.Add(row);
        }
        page.Add(nav, 0);
        page.Add(new BoxView { Color = C("LineSoft") }, 1);
        View content = _tab switch { "matrix" => Matrix(), "agent" => AgentSettings(), "time" => TimeSettingsPage(), "updates" => Updates(), _ => Rules() };
        page.Add(new ScrollView { Content = content }, 2);
        _root.Children.Clear();
        _root.Add(page);
    }

    // ------------------------------------------------------------ Regeln

    private View Rules()
    {
        var repos = Repos();
        var all = _perms.All(repos);
        var page = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 1100 };
        page.Add(Stack(3, H1("Permission rules"),
            Wrap("Regeln für Agents: Deny schlägt Ask schlägt Allow. Claude-Regeln liegen in ~/.claude/settings.json bzw. .claude/settings.json im Repo, Copit-Regeln gibt Copit beim Start als --allowedTools / --disallowedTools mit (auch für Copilot).", 12, "Muted")));

        // Neue Regel
        var scopes = new List<(string Label, RuleSource Src, string? Repo)> { ("Copit · global", RuleSource.Copit, null), ("Claude · global (~/.claude)", RuleSource.Global, null) };
        foreach (var r in repos) { scopes.Add(($"Copit · {ActivityService.ProjectName(r)}", RuleSource.Copit, r)); scopes.Add(($"Claude · {ActivityService.ProjectName(r)} (.claude)", RuleSource.Project, r)); }
        if (_newScope >= scopes.Count) _newScope = 0;
        var add = Row(
            (Input(_newPattern, "Muster, z. B. Bash(git push:*) · Edit · WebFetch", v => _newPattern = v), Star),
            (Seg([("Deny", "Deny"), ("Ask", "Ask"), ("Allow", "Allow")], _newLevel.ToString(), k => { _newLevel = Enum.Parse<PermissionLevel>(k); Render(); }, k => LevelColor(Enum.Parse<PermissionLevel>(k))), Auto),
            (Pick(scopes.Select(s => s.Label).ToList(), _newScope, i => _newScope = i, 230), Auto),
            (Primary("Hinzufügen", () =>
            {
                var pat = _newPattern.Trim();
                if (pat.Length == 0) return;
                var sc = scopes[_newScope];
                try { _perms.Add(pat, _newLevel, sc.Src, sc.Repo); _newPattern = ""; Render(); }
                catch (Exception e) { _toast.Error(e, "Regel speichern"); }
            }), Auto));
        page.Add(Ui.Card(Stack(8, Caption("Neue Regel"), add,
            Text("Beispiele: Bash(npm test:*) · Bash(git commit:*) · Read · Edit · WebFetch(domain:docs.example.com)", 11, "Muted"))));

        // Filter
        int Count(PermissionLevel? l) => all.Count(r => l == null || r.Level == l);
        page.Add(Row(
            (Seg([("all", $"Alle {Count(null)}"), ("Deny", $"Deny {Count(PermissionLevel.Deny)}"), ("Ask", $"Ask {Count(PermissionLevel.Ask)}"), ("Allow", $"Allow {Count(PermissionLevel.Allow)}")], _filter, k => { _filter = k; Render(); }), Auto),
            (new BoxView(), Star),
            (Input(_search, "Suchen…", v => { _search = v; }, 240), Auto),
            (IconBtn("search", Render, "Filtern"), Auto)));

        var rows = all.Where(r => _filter == "all" || r.Level.ToString() == _filter)
            .Where(r => _search.Length == 0 || r.Pattern.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Level).ThenBy(r => r.Pattern).ToList();
        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var r in rows) { list.Add(RuleRow(r)); list.Add(Line()); }
        if (rows.Count == 0) list.Add(new Label { Text = "Keine Regeln", FontSize = 12, TextColor = C("Muted"), Padding = new Thickness(14, 12) });
        page.Add(Ui.Card(list, new Thickness(0)));
        return page;
    }

    private View RuleRow(PermissionRule r)
    {
        var (title, tool) = r.Describe();
        var icon = tool switch { "Bash" => "terminal", "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "pencil", "Read" or "Grep" or "Glob" => "file", "WebFetch" or "WebSearch" => "link", _ => "zap" };
        var scope = r.Source switch
        {
            RuleSource.Global => Badge("Claude global", C("Cyan")),
            RuleSource.Project => Badge($"Projekt · {ActivityService.ProjectName(r.Repo ?? "")}", C("Pink")),
            _ => Badge(r.Repo == null ? "Copit global" : $"Copit · {ActivityService.ProjectName(r.Repo)}", C("Accent")),
        };
        var level = Seg([("Deny", "Deny"), ("Ask", "Ask"), ("Allow", "Allow")], r.Level.ToString(), k =>
        {
            try { _perms.SetLevel(r, Enum.Parse<PermissionLevel>(k)); Render(); } catch (Exception e) { _toast.Error(e); }
        }, k => LevelColor(Enum.Parse<PermissionLevel>(k)));
        var row = Row(
            (Glyph(icon, 15, r.Enabled ? "Text2" : "Muted"), Auto),
            (Stack(1, Text(title, 13, r.Enabled ? "Text" : "Muted"), Mono(r.Pattern, 10.5, "Muted")), Star),
            (scope, Auto),
            (level, Auto),
            (Toggle(r.Enabled, _ => { try { _perms.Toggle(r); Render(); } catch (Exception e) { _toast.Error(e); } }), Auto),
            (IconBtn("trash", () => { try { _perms.Delete(r); Render(); } catch (Exception e) { _toast.Error(e); } }, "Löschen", 13), Auto));
        row.Padding = new Thickness(14, 8);
        row.ColumnSpacing = 12;
        if (!r.Enabled) row.Opacity = 0.6;
        return Hover(row);
    }

    // ------------------------------------------------------------ Git-Matrix

    private View Matrix()
    {
        var repos = Repos();
        var page = new VerticalStackLayout { Spacing = 16, Padding = new Thickness(26, 22, 26, 30) };
        page.Add(Stack(3, H1("Git-Permissionsmatrix"),
            Wrap("Was Agents in Git und Shell dürfen – global und pro Repository. Klick auf eine Zelle wechselt Ask → Allow → Deny. Repo-Einträge überschreiben global (Deny gewinnt immer).", 12, "Muted")));
        var cols = new List<(string Label, string? Repo)> { ("GLOBAL", null) };
        cols.AddRange(repos.Select(r => (ActivityService.ProjectName(r).ToUpperInvariant(), (string?)r)));

        var grid = new Grid { ColumnSpacing = 0, RowSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(260)));
        foreach (var _ in cols) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(130)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(new Label { Text = "", Padding = new Thickness(12, 8) }, 0, 0);
        for (int c = 0; c < cols.Count; c++)
        {
            var h = Caption(cols[c].Label, "Text2");
            h.Padding = new Thickness(10, 8);
            h.LineBreakMode = LineBreakMode.TailTruncation;
            if (cols[c].Repo != null) ToolTipProperties.SetText(h, cols[c].Repo);
            grid.Add(h, c + 1, 0);
        }
        var row = 1;
        foreach (var group in PermissionService.Ops.GroupBy(o => o.Group))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var gh = Caption(group.Key, "Accent");
            gh.Padding = new Thickness(12, 12, 0, 4);
            grid.Add(gh, 0, row++);
            foreach (var op in group)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = Row((Text(op.Label, 12.5), Auto), (op.Risky ? Badge("riskant", C("Red")) : new BoxView { WidthRequest = 0 }, Auto));
                name.Padding = new Thickness(12, 8);
                ToolTipProperties.SetText(name, string.Join("  ", op.Patterns));
                grid.Add(name, 0, row);
                for (int c = 0; c < cols.Count; c++)
                {
                    var repo = cols[c].Repo;
                    var lvl = _perms.Matrix(op.Id, repo);
                    var inherited = repo != null && lvl == PermissionLevel.Ask ? _perms.Matrix(op.Id, null) : (PermissionLevel?)null;
                    var color = LevelColor(lvl);
                    var label = inherited is { } inh && inh != PermissionLevel.Ask ? $"({inh.ToString().ToUpperInvariant()})" : lvl.ToString().ToUpperInvariant();
                    var cell = Box(new Label { Text = label, FontSize = 10.5, FontFamily = MonoFont, CharacterSpacing = 1, HorizontalOptions = LayoutOptions.Center,
                        TextColor = inherited is { } i2 && i2 != PermissionLevel.Ask ? C("Muted") : color },
                        inherited is { } i3 && i3 != PermissionLevel.Ask ? Colors.Transparent : color.WithAlpha(lvl == PermissionLevel.Ask ? 0.06f : 0.14f), new Thickness(6, 6), C("LineSoft"));
                    cell.Margin = new Thickness(2);
                    var opId = op.Id;
                    OnTap(cell, () =>
                    {
                        var next = lvl switch { PermissionLevel.Ask => PermissionLevel.Allow, PermissionLevel.Allow => PermissionLevel.Deny, _ => PermissionLevel.Ask };
                        _perms.SetMatrix(opId, next, repo);
                        Render();
                    });
                    grid.Add(cell, c + 1, row);
                }
                row++;
            }
        }
        page.Add(Ui.Card(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = grid }, new Thickness(4, 4)));
        page.Add(Wrap("(ALLOW) in Klammern = vom globalen Wert geerbt. Die Matrix schreibt Copit-Regeln; bestehende Claude-Regeln aus settings.json siehst du unter Permission rules.", 11, "Muted"));
        return page;
    }

    // ------------------------------------------------------------ Agent-Verhalten

    private View AgentSettings()
    {
        var ai = _settings.Settings.Ai;
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 900 };
        page.Add(H1("Agent-Verhalten"));
        void Save() => _ = _settings.SaveAsync();
        page.Add(Ui.Card(Stack(14,
            Row((Stack(2, Text("Permission-Regeln anwenden", 13, "Text", true), Wrap("Copit-Regeln (inkl. Matrix) bei jedem Agent-Lauf als CLI-Flags mitgeben.", 11.5, "Muted")), Star), (Toggle(ai.ApplyPermissionRules, v => { ai.ApplyPermissionRules = v; Save(); }), Auto)),
            Line(),
            Row((Stack(2, Text("Eigene Worktrees für neue Agents", 13, "Text", true), Wrap("Agents mit Branch arbeiten in einem eigenen git worktree unter %LOCALAPPDATA%\\Copit\\worktrees – parallel, ohne deinen Branch zu wechseln.", 11.5, "Muted")), Star), (Toggle(ai.UseWorktrees, v => { ai.UseWorktrees = v; Save(); }), Auto)),
            Line(),
            Stack(6, Text("Globale Anweisungen", 13, "Text", true), Area(ai.GlobalInstructions, "Wird jedem Agent als System-Anweisung mitgegeben", v => { ai.GlobalInstructions = v; }, 90),
                HStack(8, Chip("Speichern", () => { Save(); _toast.Success("Gespeichert"); }))))));
        page.Add(Caption("Tageslimits für Agents"));
        foreach (var prov in new[] { AiProvider.Claude, AiProvider.Copilot })
        {
            var lim = prov == AiProvider.Claude ? ai.ClaudeLimit : ai.CopilotLimit;
            var mgr = Handler?.MauiContext?.Services.GetService<SessionManager>();
            var used = mgr?.UsageToday(prov, lim.Unit) ?? 0;
            var units = prov == AiProvider.Claude ? new[] { ("usd", "Dollar"), ("hours", "Stunden"), ("runs", "Läufe") } : new[] { ("hours", "Stunden"), ("runs", "Läufe") };
            if (!units.Any(u => u.Item1 == lim.Unit)) lim.Unit = units[0].Item1;
            var frac = lim.Amount > 0 ? used / lim.Amount : 0;
            page.Add(Ui.Card(Stack(10,
                Row((Stack(2, Text(prov.ToString(), 14, "Text", true), Text(lim.Amount > 0 ? $"Heute {lim.Format(used)} von {lim.Format(lim.Amount)}" : $"Heute {lim.Format(used)} · kein Limit", 11.5, "Muted")), Star),
                    (Seg(units.Select(u => (u.Item1, u.Item2)), lim.Unit, k => { lim.Unit = k; Save(); Render(); }), Auto),
                    (Input(lim.Amount > 0 ? lim.Amount.ToString("0.##") : "", "0 = aus", v =>
                    {
                        if (double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) && a >= 0) { lim.Amount = a; Save(); }
                        else if (v.Trim().Length == 0) { lim.Amount = 0; Save(); }
                    }, 100), Auto)),
                lim.Amount > 0 ? Bar(frac, frac >= 1 ? C("Red") : frac >= 0.8 ? C("Amber") : C("Green"), 4) : new BoxView { HeightRequest = 0 },
                Wrap("Gilt für alle Copit-Agents dieses Anbieters pro Kalendertag. Bei 80 % warnt Copit, bei 100 % starten keine neuen Läufe mehr." + (prov == AiProvider.Copilot ? " Copilot meldet keine Kosten – daher Stunden oder Läufe." : ""), 11, "Muted"))));
        }
        var prices = string.Join("   ", Pricing.Prices.Where(p => p.Key != "default").Select(p => $"{p.Key} ${p.Value.Input}/${p.Value.Output}"));
        page.Add(Ui.Card(Stack(6, Caption("Preise für Kostenschätzung (USD / 1 Mio. Tokens, Input/Output)"), Mono(prices, 11.5, "Text2"),
            Wrap("Cache-Lesen 10 %, Cache-Schreiben 125 % des Input-Preises. Für Copit-Agents mit Claude nimmt Copit die exakten Kosten aus der CLI.", 11, "Muted"))));
        return page;
    }

    // ------------------------------------------------------------ Zeitbuchung

    private View TimeSettingsPage()
    {
        var t = _settings.Settings.Time;
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 900 };
        page.Add(H1("Zeitbuchung"));
        var factor = (t.EffortFactor * 100).ToString("0");
        page.Add(Ui.Card(Stack(14,
            Row((Stack(2, Text("Nach Personen-Aufwand buchen", 13, "Text", true),
                Wrap("Die KI schätzt, wie lange eine Person ohne KI für die erledigte Arbeit gebraucht hätte, und bucht etwas weniger. Der Tag wird nicht künstlich auf das Soll aufgefüllt.", 11.5, "Muted")), Star),
                (Toggle(t.HumanEffort, v => { t.HumanEffort = v; _ = _settings.SaveAsync(); }), Auto)),
            Line(),
            Row((Stack(2, Text("Faktor", 13, "Text", true), Wrap("Anteil des geschätzten Personen-Aufwands, der gebucht wird (85 % = etwas weniger).", 11.5, "Muted")), Star),
                (Input(factor, "85", v => { if (double.TryParse(v.Trim().TrimEnd('%'), out var f) && f is >= 30 and <= 120) { t.EffortFactor = f / 100; _ = _settings.SaveAsync(); } }, 80), Auto), (Text("%", 13, "Muted"), Auto)))));
        page.Add(Wrap("Gelernte Zuordnungen (Thema/Repo → Ticket) und Antworten auf Rückfragen findest du in der Zeitbuchung unter „Regeln & Soll“.", 11.5, "Muted"));
        return page;
    }

    // ------------------------------------------------------------ Updates

    private View Updates()
    {
        var u = _settings.Settings.Update;
        var hasToken = _settings.GetSecret(SecretKeys.GitHubToken).Length > 0;
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 900 };
        page.Add(Stack(3, H1("Updates"), Text($"Installierte Version {UpdateService.Current.ToString(3)}", 12.5, "Muted")));
        var tokenEntry = new Entry { Placeholder = hasToken ? "•••••••• (neu eingeben zum Ersetzen)" : "Fine-grained Token mit Lesezugriff auf Contents", IsPassword = true, FontSize = 12.5 };
        tokenEntry.TextChanged += (_, a) => _tokenInput = a.NewTextValue ?? "";
        page.Add(Ui.Card(Stack(14,
            Row((Stack(2, Text("Automatisch nach Updates suchen", 13, "Text", true), Wrap("Beim Start und alle 6 Stunden. Neue Versionen werden mit einem Klick heruntergeladen und installiert – Copit startet danach neu.", 11.5, "Muted")), Star),
                (Toggle(u.Auto, v => { u.Auto = v; _ = _settings.SaveAsync(); }), Auto)),
            Line(),
            Stack(4, Caption("GitHub-Repository"), Input(u.Repo, "owner/name", v => { u.Repo = v.Trim(); _ = _settings.SaveAsync(); })),
            Stack(4, Caption(hasToken ? "GitHub-Token (hinterlegt)" : "GitHub-Token (optional, nur für private Repos)"),
                Row((tokenEntry, Star),
                    (Chip("Speichern", () => { _settings.SetSecret(SecretKeys.GitHubToken, _tokenInput.Trim()); _ = _settings.SaveAsync(); _tokenInput = ""; _toast.Success("Token gespeichert", "verschlüsselt (Windows DPAPI)"); Render(); }), Auto)),
                Wrap("Copit-Code/Copit ist öffentlich – dafür ist kein Token nötig. Nur für private Repos: GitHub → Settings → Developer settings → Fine-grained tokens, Resource owner = Besitzer des Repos, nur dieses Repository, „Contents: Read-only“. Wird lokal und verschlüsselt gespeichert.", 11, "Muted")),
            Line(),
            Row((Text(_updateStatus, 12.5, "Text2"), Star), (Primary("Jetzt prüfen", async () =>
            {
                _updateStatus = "Prüfe…"; Render();
                try
                {
                    var r = await _updates.CheckAsync(ignoreSkipped: true);
                    _updateStatus = r == null ? $"Copit {UpdateService.Current.ToString(3)} ist aktuell." : $"Neue Version {r.Tag} verfügbar.";
                    Render();
                    if (r != null) await InstallAsync(r);
                }
                catch (Exception e) { _updateStatus = e.Message; Render(); }
            }), Auto)))));
        return page;
    }

    /// <summary>Fragt nach, lädt den Installer und startet ihn still (Copit wird beendet und neu gestartet).</summary>
    public async Task InstallAsync(ReleaseInfo r)
    {
        var choice = await Dialogs.Choose($"Copit {r.Tag} ist verfügbar", "Jetzt installieren", "Release-Notes öffnen", "Diese Version überspringen");
        if (choice == "Release-Notes öffnen") { await Dialogs.OpenUrl(r.HtmlUrl); return; }
        if (choice == "Diese Version überspringen") { _settings.Settings.Update.SkippedVersion = r.Tag; await _settings.SaveAsync(); return; }
        if (choice != "Jetzt installieren") return;
        try
        {
            _toast.Show($"Lade Copit {r.Tag}…", r.InstallerName ?? "");
            var file = await _updates.DownloadAsync(r);
            Handler?.MauiContext?.Services.GetService<SessionManager>()?.SaveAll();
            UpdateService.RunInstaller(file);
            Application.Current?.Quit();
        }
        catch (Exception e) { _toast.Error(e, "Update"); }
    }
}
