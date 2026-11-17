using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Automation-Builder: WENN Trigger [UND Bedingungen] DANN Aktionen – für Agents, Git, GitLab, Jira und Zeitpläne.</summary>
public class AutomationsView : ContentView, IActivatable
{
    private readonly AutomationService _engine;
    private readonly DataService _data;
    private readonly SessionManager _mgr;
    private readonly ToastService _toast;
    private readonly Grid _root = new();
    private Automation? _sel;
    private Automation? _draft;
    private bool _visible;
    private static AutomationService? _engineStatic;

    public AutomationsView(AutomationService engine, DataService data, SessionManager mgr, ToastService toast, NavigationService nav)
    {
        _engine = engine; _data = data; _mgr = mgr; _toast = toast; _engineStatic = engine;
        BackgroundColor = C("Bg");
        Content = _root;
        engine.RunsChanged += () => MainThread.BeginInvokeOnMainThread(() => { if (_visible && _draft == null) Render(); });
        nav.Navigated += (r, _) => _visible = r == Routes.Automations;
    }

    public void OnActivated(object? parameter)
    {
        _visible = true;
        _engine.SeedDefaults();
        _sel ??= _engine.All.FirstOrDefault();
        Render();
    }

    private static string TriggerLabel(string t) => AutomationService.Triggers.FirstOrDefault(x => x.Type == t)?.Label ?? t;
    private static string ActionLabel(string t) => _engineStatic?.AllActions.FirstOrDefault(x => x.Type == t)?.Label ?? t;

    private void Render()
    {
        var page = new Grid { ColumnDefinitions = [new(new GridLength(360)), new(new GridLength(1)), new(GridLength.Star)] };
        page.Add(ListPane(), 0);
        page.Add(new BoxView { Color = C("LineSoft") }, 1);
        page.Add(new ScrollView { Content = _draft != null ? Editor(_draft) : _sel != null ? Detail(_sel) : Text("Keine Automation gewählt", 13, "Muted") }, 2);
        _root.Children.Clear();
        _root.Add(page);
    }

    private View ListPane()
    {
        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var g in _engine.All.GroupBy(a => AutomationService.Triggers.FirstOrDefault(t => t.Type == a.Trigger)?.Group ?? "Sonstige"))
        {
            var cap = Caption(g.Key, "Accent");
            cap.Margin = new Thickness(14, 12, 0, 4);
            list.Add(cap);
            foreach (var a in g)
            {
                var active = _sel == a && _draft == null;
                var row = Row(
                    (Toggle(a.Enabled, v => { a.Enabled = v; _ = _data.Automations.SaveAsync(); }), Auto),
                    (Stack(1, Text(a.Name, 12.5, a.Enabled ? "Text" : "Muted"),
                        Text($"{TriggerLabel(a.Trigger)} → {string.Join(", ", a.Actions.Select(x => ActionLabel(x.Type)))}", 10.5, "Muted")), Star),
                    (Mono(a.RunCount > 0 ? $"{a.RunCount}×" : "", 10, "Muted"), Auto));
                row.Padding = new Thickness(10, 7);
                row.BackgroundColor = active ? C("CardHover") : Colors.Transparent;
                OnTap(row, () => { _sel = a; _draft = null; Render(); });
                list.Add(row);
            }
        }
        var head = Row((Stack(2, Caption("Automationen"), H2("Builder")), Star), (Primary("Neu", () =>
        {
            _draft = new Automation { Name = "Neue Automation", Trigger = "agent.finished", Actions = [new RuleAction { Type = "notify", Params = new() { ["message"] = "{{title}} ist fertig" } }] };
            _sel = null;
            Render();
        }), Auto));
        head.Padding = new Thickness(14, 22, 14, 6);
        var g2 = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)], BackgroundColor = C("Bg2") };
        g2.Add(head, 0, 0);
        g2.Add(new ScrollView { Content = list }, 0, 1);
        return g2;
    }

    // ------------------------------------------------------------ Detail

    private View Detail(Automation a)
    {
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 900, HorizontalOptions = LayoutOptions.Start };
        page.Add(Row((Stack(3, Caption(a.Enabled ? "aktiv" : "pausiert", a.Enabled ? "Green" : "Muted"), H1(a.Name)), Star),
            (Ghost("Testlauf", async () =>
            {
                var agent = _mgr.Agents.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
                try { await _engine.TestAsync(a, a.Trigger.StartsWith("agent.") ? agent : null); _toast.Success("Testlauf ausgeführt", a.LastResult); }
                catch (Exception e) { _toast.Error(e, "Testlauf"); }
                Render();
            }), Auto),
            (Btn("Bearbeiten", () => { _draft = Clone(a); Render(); }), Auto),
            (IconBtn("trash", async () =>
            {
                if (!await Dialogs.Confirm("Automation löschen?", a.Name, "Löschen")) return;
                _engine.All.Remove(a);
                await _data.Automations.SaveAsync();
                _sel = _engine.All.FirstOrDefault();
                Render();
            }, "Löschen"), Auto)));

        page.Add(Block("WENN", Text(TriggerLabel(a.Trigger), 14, "Text", true),
            a.TriggerParams.Where(p => p.Value.Length > 0).Select(p => Mono($"{p.Key}: {p.Value}", 11.5, "Text2")).ToArray()));
        if (a.Conditions.Count > 0)
            page.Add(Block("UND", null, a.Conditions.Select(c => Mono($"{c.Field} {OpLabel(c.Op)} \"{c.Value}\"", 11.5, "Text2")).ToArray()));
        page.Add(Block("DANN", null, a.Actions.Select(x => (View)Stack(3, Text(ActionLabel(x.Type), 13, "Text", true),
            Stack(1, x.Params.Where(p => p.Value.Length > 0).Select(p => (View)Wrap($"{p.Key}: {p.Value}", 11.5, "Text2")).ToArray()))).ToArray()));

        page.Add(Caption("Letzte Läufe"));
        var runs = _data.Automations.Value.Runs.Where(r => r.AutomationId == a.Id).Take(20).ToList();
        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var r in runs)
        {
            var row = Row((Glyph(r.Success ? "check-circle" : "x-circle", 13, r.Success ? "Green" : "Red"), Auto), (Mono(r.At.ToString("dd.MM. HH:mm:ss"), 11, "Muted"), Auto), (Wrap(r.Output, 11.5, "Text2"), Star));
            row.Padding = new Thickness(12, 6);
            list.Add(row);
            list.Add(Line());
        }
        if (runs.Count == 0) list.Add(new Label { Text = "Noch nie ausgeführt", FontSize = 12, TextColor = C("Muted"), Padding = new Thickness(12, 10) });
        page.Add(Ui.Card(list, new Thickness(0)));
        return page;
    }

    private static View Block(string word, View? head, View[] lines)
    {
        var body = Stack(6);
        if (head != null) body.Add(head);
        foreach (var l in lines) body.Add(l);
        var g = Row((new Label { Text = word, FontFamily = MonoFont, FontSize = 11, CharacterSpacing = 1.4, TextColor = C("Accent"), WidthRequest = 54, VerticalOptions = LayoutOptions.Start }, Auto), (body, Star));
        return Ui.Card(g, new Thickness(16, 12));
    }

    private static string OpLabel(string op) => op switch { "equals" => "ist", "not_equals" => "ist nicht", "contains" => "enthält", "matches" => "passt auf Regex", _ => op };

    private static Automation Clone(Automation a) => new()
    {
        Id = a.Id, Name = a.Name, Enabled = a.Enabled, Trigger = a.Trigger, TriggerParams = new(a.TriggerParams),
        Conditions = a.Conditions.Select(c => new RuleCondition { Field = c.Field, Op = c.Op, Value = c.Value }).ToList(),
        Actions = a.Actions.Select(x => new RuleAction { Type = x.Type, Params = new(x.Params) }).ToList(),
        LastRun = a.LastRun, LastResult = a.LastResult, RunCount = a.RunCount,
    };

    // ------------------------------------------------------------ Editor

    private View Editor(Automation d)
    {
        var page = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(26, 22, 26, 30), MaximumWidthRequest = 900, HorizontalOptions = LayoutOptions.Start };
        page.Add(Row((Stack(3, Caption("Automation bearbeiten"), Input(d.Name, "Name", v => d.Name = v)), Star)));

        // WENN
        var tdef = AutomationService.Triggers.FirstOrDefault(t => t.Type == d.Trigger) ?? AutomationService.Triggers[0];
        var trig = Stack(8, Pick(AutomationService.Triggers.Select(t => $"{t.Group} · {t.Label}").ToList(), Array.IndexOf(AutomationService.Triggers, tdef), i =>
        {
            d.Trigger = AutomationService.Triggers[i].Type;
            d.TriggerParams = [];
            Render();
        }));
        foreach (var p in tdef.Params)
        {
            if (d.Trigger == "agent.moved")
            {
                // Spalten als Auswahl statt Freitext
                var cols = AgentStatusInfo.Columns;
                var items = new List<string> { "(beliebig)" };
                items.AddRange(cols.Select(c => c.Title));
                var cur = d.TriggerParams.GetValueOrDefault(p.Key, "");
                var idx = Array.FindIndex(cols, c => AutomationService.SameColumn(cur, c.Status.ToString()) && cur.Length > 0);
                var key = p.Key;
                trig.Add(Row((Text(p.Label, 12, "Text2"), Px(160)), (Pick(items, idx + 1, i => d.TriggerParams[key] = i == 0 ? "" : cols[i - 1].Status.ToString()), Star)));
                continue;
            }
            trig.Add(Row((Text(p.Label, 12, "Text2"), Px(160)), (Input(d.TriggerParams.GetValueOrDefault(p.Key, ""), p.Placeholder, v => d.TriggerParams[p.Key] = v), Star)));
        }
        if (d.Trigger == "agent.moved") trig.Add(Text("Löst aus, wenn du eine Karte auf dem Board verschiebst. Platzhalter: {{branch}}, {{ticket}}, {{from}}, {{to}}.", 10.5, "Muted"));
        page.Add(Block("WENN", null, [trig]));

        // UND
        var conds = Stack(8);
        foreach (var c in d.Conditions.ToList())
        {
            conds.Add(Row(
                (Pick(AutomationService.Fields, Math.Max(0, Array.IndexOf(AutomationService.Fields, c.Field)), i => c.Field = AutomationService.Fields[i], 130), Auto),
                (Pick(AutomationService.Ops.Select(OpLabel).ToList(), Math.Max(0, Array.IndexOf(AutomationService.Ops, c.Op)), i => c.Op = AutomationService.Ops[i], 150), Auto),
                (Input(c.Value, "Wert", v => c.Value = v), Star),
                (IconBtn("x", () => { d.Conditions.Remove(c); Render(); }, "Entfernen", 13), Auto)));
        }
        conds.Add(HStack(8, Chip("Bedingung", () => { d.Conditions.Add(new RuleCondition()); Render(); })));
        page.Add(Block("UND", null, [conds]));

        // DANN
        var acts = Stack(12);
        foreach (var a in d.Actions.ToList())
        {
            var all = _engine.AllActions.ToList();
            var adef = all.FirstOrDefault(x => x.Type == a.Type) ?? all[0];
            var box = Stack(8, Row((Pick(all.Select(x => $"{x.Group} · {x.Label}").ToList(), all.IndexOf(adef), i =>
            {
                a.Type = all[i].Type;
                a.Params = [];
                Render();
            }), Star), (IconBtn("x", () => { d.Actions.Remove(a); Render(); }, "Entfernen", 13), Auto)));
            foreach (var p in adef.Params)
                box.Add(Row((Text(p.Label, 12, "Text2"), Px(160)),
                    (p.Multiline ? Area(a.Params.GetValueOrDefault(p.Key, ""), p.Placeholder, v => a.Params[p.Key] = v, 60) : Input(a.Params.GetValueOrDefault(p.Key, ""), p.Placeholder, v => a.Params[p.Key] = v), Star)));
            acts.Add(Box(box, C("Panel"), new Thickness(12, 10), C("LineSoft")));
        }
        acts.Add(HStack(8, Chip("Aktion", () => { d.Actions.Add(new RuleAction { Type = "notify" }); Render(); })));
        page.Add(Block("DANN", null, [acts]));

        page.Add(Wrap("Variablen: {{title}} {{ticket}} {{branch}} {{status}} {{summary}} {{project}} {{repo}} {{lines}} {{files}} {{cost}} {{agentMinutes}} {{aiMinutes}} · Git: {{sha}} {{message}} {{author}} · GitLab: {{pipelineUrl}} · Jira: {{issueSummary}} {{from}}", 11, "Muted"));
        page.Add(Row((new BoxView(), Star), (Ghost("Abbrechen", () => { _draft = null; Render(); }), Auto), (Primary("Speichern", async () =>
        {
            if (string.IsNullOrWhiteSpace(d.Name)) { _toast.Show("Name fehlt", "", NoticeLevel.Warning); return; }
            if (d.Actions.Count == 0) { _toast.Show("Mindestens eine Aktion", "", NoticeLevel.Warning); return; }
            var idx = _engine.All.FindIndex(x => x.Id == d.Id);
            if (idx >= 0) _engine.All[idx] = d; else _engine.All.Add(d);
            await _data.Automations.SaveAsync();
            _sel = d; _draft = null;
            _toast.Success("Automation gespeichert", d.Name);
            Render();
        }), Auto)));
        return page;
    }
}
