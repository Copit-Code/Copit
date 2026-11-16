using System.Globalization;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Activity: wann lief welcher Agent, AI-Zeit vs. Agent-Zeit, Tokens und Kosten in Dollar.</summary>
public class ActivityView : ContentView, IActivatable
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly SessionManager _mgr;
    private readonly DataService _data;
    private readonly NavigationService _nav;
    private readonly Grid _root = new();
    private string _range = "day";
    private DateTime _anchor = DateTime.Today;
    private string? _expanded;
    private string _groupBy = "session";
    private List<ActSession> _sessions = [];
    private bool _loading;

    public ActivityView(SessionManager mgr, DataService data, NavigationService nav)
    {
        _mgr = mgr; _data = data; _nav = nav;
        BackgroundColor = C("Bg");
        Content = _root;
    }

    public void OnActivated(object? parameter) => _ = LoadAsync();

    private (DateTime From, DateTime To) Range()
    {
        if (_range == "day") return (_anchor.Date, _anchor.Date.AddDays(1));
        var mon = _anchor.Date.AddDays(-(((int)_anchor.DayOfWeek + 6) % 7));
        return (mon, mon.AddDays(7));
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        if (_sessions.Count == 0) { _root.Children.Clear(); _root.Add(new ActivityIndicator { IsRunning = true, Color = C("Accent"), WidthRequest = 28, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }); }
        var (from, to) = Range();
        try
        {
            var agents = _mgr.Agents.ToList();
            _sessions = await Task.Run(() => _mgr.Activity.Build(from, to, agents, id => _data.Project(id)?.SourceFolder));
        }
        catch (Exception e) { App.LogCrash(e); _sessions = []; }
        finally { _loading = false; }
        Render();
    }

    private void Shift(int dir)
    {
        _anchor = _anchor.AddDays(_range == "day" ? dir : 7 * dir);
        _ = LoadAsync();
    }

    private void Render()
    {
        var (from, to) = Range();
        var segs = _sessions.SelectMany(s => ActivityService.Clip(s.Segments, from, to)).ToList();
        var aiTime = ActivityService.Union(segs);
        var agentTime = TimeSpan.FromTicks(segs.Sum(s => (s.End - s.Start).Ticks));
        var cost = _sessions.Sum(s => s.CostUsd);
        var tokens = _sessions.Sum(s => s.TotalTokens);
        var exact = _sessions.Where(s => s.CostExact).Sum(s => s.CostUsd);

        var page = new VerticalStackLayout { Spacing = 18, Padding = new Thickness(26, 22, 26, 30) };

        // Kopf + Navigation
        var title = _range == "day" ? from.ToString("dddd, d. MMMM yyyy", De) : $"KW {ISOWeek.GetWeekOfYear(from)} · {from:dd.MM.} – {to.AddDays(-1):dd.MM.yyyy}";
        page.Add(Row(
            (Stack(3, Caption("Activity"), H1(title)), Star),
            (new Grid { VerticalOptions = LayoutOptions.Center, Children = { Seg([("day", "Tag"), ("week", "Woche")], _range, k => { _range = k; _ = LoadAsync(); }) } }, Auto),
            (HStack(2, IconBtn("chevron-left", () => Shift(-1), "Zurück"), Chip("Heute", () => { _anchor = DateTime.Today; _ = LoadAsync(); }), IconBtn("chevron-right", () => Shift(1), "Weiter"), IconBtn("refresh", () => _ = LoadAsync(), "Neu laden")), Auto)));

        // Kennzahlen
        var stats = new Grid { ColumnSpacing = 12, ColumnDefinitions = [new(Star), new(Star), new(Star), new(Star), new(Star)] };
        stats.Add(Stat("AI time", Dur(aiTime), "Wanduhr, ≥1 Agent aktiv"), 0);
        stats.Add(Stat("Agent time", Dur(agentTime), aiTime.TotalMinutes > 0 ? $"Parallelität ×{agentTime / aiTime:0.0}" : "parallel doppelt gezählt"), 1);
        stats.Add(Stat("Kosten", Money(cost), exact > 0 ? $"{Money(exact)} exakt laut CLI, Rest geschätzt" : "Tokens × Listenpreis", "Accent"), 2);
        stats.Add(Stat("Tokens", Tok(tokens), $"{Tok(_sessions.Sum(s => s.OutputTokens))} Output · {Tok(_sessions.Sum(s => s.CacheRead))} Cache"), 3);
        stats.Add(Stat("Sessions", _sessions.Count.ToString(), $"{_sessions.Sum(s => s.Prompts)} Prompts · {_sessions.Sum(s => s.Subagents)} Sub-Agents"), 4);
        page.Add(stats);

        // Zeitleiste
        page.Add(Row((Caption(_range == "day" ? "Zeitleiste – welcher Agent lief wann" : "Woche – AI time pro Tag nach Projekt"), Star),
            (Seg([("session", "Sessions"), ("project", "Projekte")], _groupBy, k => { _groupBy = k; Render(); }), Auto)));
        page.Add(Ui.Card(_range == "day" ? DayTimeline(from) : WeekChart(from), new Thickness(14, 12)));

        // Aufschlüsselung
        page.Add(Caption(_groupBy == "project" ? "Nach Projekt" : "Sessions"));
        page.Add(_groupBy == "project" ? ProjectTable(from, to) : SessionTable(from, to));
        if (_sessions.Count == 0)
            page.Add(Wrap("Keine Agent-Aktivität in diesem Zeitraum. Copit liest Claude-Code-Sessions aus ~/.claude/projects und eigene Agents (auch Copilot).", 12.5, "Muted"));

        page.Add(Wrap("AI time = Zeit, in der mindestens ein Agent gearbeitet hat (Pausen > 10 min zählen nicht). Agent time = Summe aller Agents, parallele Arbeit zählt mehrfach. " +
                      "Kosten: exakt laut CLI für Copit-Agents, sonst Tokens × Listenpreis (Cache-Lesen 10 %, Cache-Schreiben 125 %).", 11, "Muted"));

        _root.Children.Clear();
        _root.Add(new ScrollView { Content = page });
    }

    // ------------------------------------------------------------ Zeitleiste (Tag)

    private View DayTimeline(DateTime day)
    {
        var lanes = _groupBy == "project"
            ? _sessions.GroupBy(s => s.Project).Select(g => (Label: g.Key, Color: ColorFor(g.Key), Segs: g.SelectMany(s => ActivityService.Clip(s.Segments, day, day.AddDays(1))).ToList())).ToList()
            : _sessions.Select(s => (Label: s.Title, Color: ColorFor(s.Project), Segs: ActivityService.Clip(s.Segments, day, day.AddDays(1)).ToList())).ToList();
        lanes = lanes.Where(l => l.Segs.Count > 0).OrderBy(l => l.Segs.Min(x => x.Item1)).ToList();
        if (lanes.Count == 0) return Text("Keine Aktivität", 12, "Muted");

        var minH = Math.Max(0, lanes.Min(l => l.Segs.Min(s => s.Item1)).Hour - 1);
        var maxH = Math.Min(24, lanes.Max(l => l.Segs.Max(s => s.Item2)).Hour + 2);
        var drawable = new TimelineDrawable
        {
            Start = day.AddHours(minH), End = day.AddHours(maxH), Lanes = lanes.Select(l => (l.Color, l.Segs)).ToList(),
            Line = C("LineSoft"), Muted = C("Muted"), Now = day.Date == DateTime.Today ? DateTime.Now : null, Accent = C("Accent"),
        };
        var labels = new VerticalStackLayout { Spacing = 0, WidthRequest = 230, Padding = new Thickness(0, 22, 0, 0) };
        foreach (var l in lanes) labels.Add(new Grid { HeightRequest = TimelineDrawable.LaneH, Children = { Row((Square(l.Color, 7), Auto), (Text(l.Label, 11.5, "Text2"), Star)) } });
        var gv = new GraphicsView { Drawable = drawable, HeightRequest = 22 + lanes.Count * TimelineDrawable.LaneH + 4 };
        return Row((labels, Auto), (gv, Star));
    }

    private sealed class TimelineDrawable : IDrawable
    {
        public const double LaneH = 24;
        public DateTime Start, End;
        public DateTime? Now;
        public List<(Color Color, List<(DateTime, DateTime)> Segs)> Lanes = [];
        public Color Line = Colors.Gray, Muted = Colors.Gray, Accent = Colors.Orange;

        public void Draw(ICanvas c, RectF r)
        {
            var span = (End - Start).TotalMinutes;
            float X(DateTime t) => (float)(r.Left + (t - Start).TotalMinutes / span * r.Width);
            c.FontSize = 9.5f;
            c.FontColor = Muted;
            for (var h = Start; h <= End; h = h.AddHours(1))
            {
                var x = X(h);
                c.StrokeColor = Line; c.StrokeSize = 1;
                c.DrawLine(x, 18, x, r.Bottom);
                c.DrawString(h.ToString("HH"), x + 3, 2, 30, 14, HorizontalAlignment.Left, VerticalAlignment.Top);
            }
            for (int i = 0; i < Lanes.Count; i++)
            {
                var y = (float)(22 + i * LaneH + 6);
                foreach (var (a, b) in Lanes[i].Segs)
                {
                    var x0 = X(a); var x1 = Math.Max(X(b), x0 + 2);
                    c.FillColor = Lanes[i].Color.WithAlpha(0.85f);
                    c.FillRectangle(x0, y, x1 - x0, (float)LaneH - 12);
                }
            }
            if (Now is { } n && n > Start && n < End)
            {
                c.StrokeColor = Accent; c.StrokeSize = 1.5f;
                c.DrawLine(X(n), 16, X(n), r.Bottom);
            }
        }
    }

    // ------------------------------------------------------------ Woche

    private View WeekChart(DateTime mon)
    {
        var days = Enumerable.Range(0, 7).Select(i => mon.AddDays(i)).ToList();
        var projects = _sessions.Select(s => s.Project).Distinct().ToList();
        var data = days.Select(d => projects.Select(p =>
            ActivityService.Union(_sessions.Where(s => s.Project == p).SelectMany(s => ActivityService.Clip(s.Segments, d, d.AddDays(1)))).TotalHours).ToList()).ToList();
        var recs = ActivityService.Records(_sessions);
        var dayCost = days.Select(d => recs.Where(r => r.Start >= d && r.Start < d.AddDays(1)).Sum(r => r.CostUsd)).ToList();
        var max = Math.Max(1, data.Max(d => d.Sum()));
        var chart = new Grid { ColumnSpacing = 14, HeightRequest = 210 };
        for (int i = 0; i < 7; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition(Star));
            var stack = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.End };
            for (int p = projects.Count - 1; p >= 0; p--)
                if (data[i][p] > 0) stack.Add(new BoxView { Color = ColorFor(projects[p]), HeightRequest = Math.Max(2, data[i][p] / max * 150) });
            var col = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], RowSpacing = 4 };
            col.Add(Mono(data[i].Sum() > 0 ? $"{data[i].Sum():0.#}h" : "", 10, "Text2"), 0, 0);
            col.Add(stack, 0, 1);
            col.Add(Mono(days[i].ToString("ddd dd.", De).ToUpperInvariant(), 9.5, days[i].Date == DateTime.Today ? "Accent" : "Muted"), 0, 2);
            col.Add(Mono(dayCost[i] > 0 ? Money(dayCost[i]) : "", 10, "Muted"), 0, 3);
            chart.Add(col, i);
        }
        var legend = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var p in projects) legend.Add(new HorizontalStackLayout { Spacing = 5, Margin = new Thickness(0, 0, 14, 4), Children = { Square(ColorFor(p), 7), Text(p, 11, "Text2") } });
        return Stack(12, chart, legend);
    }

    // ------------------------------------------------------------ Tabellen

    private static Grid TableRow(params (View, GridLength)[] cells)
    {
        var g = Row(cells);
        g.Padding = new Thickness(10, 7);
        return g;
    }

    private View Header(params (string, GridLength)[] cols) =>
        TableRow(cols.Select(c => ((View)Caption(c.Item1), c.Item2)).ToArray());

    private View SessionTable(DateTime from, DateTime to)
    {
        var list = new VerticalStackLayout { Spacing = 0 };
        list.Add(Header(("Session", Star), ("Projekt", Px(170)), ("Quelle", Px(110)), ("AI time", Px(80)), ("Tokens", Px(80)), ("Kosten", Px(80))));
        foreach (var s in _sessions.OrderByDescending(s => s.CostUsd))
        {
            var active = ActivityService.Union(ActivityService.Clip(s.Segments, from, to));
            var title = Stack(1, Text(s.Title, 12.5, "Text"), Text(string.Join(" · ", new[] { s.Branch, s.Ticket, string.Join(", ", s.Models) }.Where(x => !string.IsNullOrEmpty(x))), 10.5, "Muted"));
            var row = TableRow((Row((Square(ColorFor(s.Project), 7), Auto), (title, Star)), Star), (Text(s.Project, 12, "Text2"), Px(170)), (Text(s.Entry, 11.5, "Muted"), Px(110)),
                (Mono(Dur(active), 11.5, "Text"), Px(80)), (Mono(Tok(s.TotalTokens), 11.5, "Text2"), Px(80)), (Mono(Money(s.CostUsd) + (s.CostExact ? "" : "*"), 11.5, "Accent"), Px(80)));
            Hover(row);
            OnTap(row, () => { _expanded = _expanded == s.Id ? null : s.Id; Render(); });
            list.Add(row);
            if (_expanded == s.Id) list.Add(Detail(s));
            list.Add(Line());
        }
        return Ui.Card(list, new Thickness(4, 4));
    }

    private View Detail(ActSession s)
    {
        var p = Pricing.For(s.Models.FirstOrDefault());
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 6, Padding = new Thickness(28, 6, 10, 12), ColumnDefinitions = [new(Star), new(Star), new(Star)] };
        grid.Add(Stack(4, Caption("Aktive Phasen"), Wrap(string.Join("\n", s.Segments.Select(g => $"{g.Start:HH:mm}–{g.End:HH:mm}  ({Dur(g.End - g.Start)})")), 11.5, "Text2")), 0);
        grid.Add(Stack(4, Caption("Tokens"),
            Wrap($"Input  {Tok(s.InputTokens)}\nOutput  {Tok(s.OutputTokens)}\nCache lesen  {Tok(s.CacheRead)}\nCache schreiben  {Tok(s.CacheWrite)}", 11.5, "Text2")), 1);
        var actions = Stack(4, Caption("Details"), Wrap($"{s.Prompts} Prompts · {s.ToolCalls} Tool-Aufrufe · {s.Subagents} Sub-Agents\n{s.ProjectPath}", 11.5, "Text2"));
        if (s.AgentId != null && _mgr.Agents.FirstOrDefault(a => a.Id == s.AgentId) is { } ag)
            actions.Add(Chip("Agent öffnen", () => _nav.Go(Routes.Session, _mgr.Host(ag))));
        grid.Add(actions, 2);
        return grid;
    }

    private View ProjectTable(DateTime from, DateTime to)
    {
        var list = new VerticalStackLayout { Spacing = 0 };
        list.Add(Header(("Projekt", Star), ("Sessions", Px(80)), ("AI time", Px(90)), ("Agent time", Px(90)), ("Tokens", Px(80)), ("Kosten", Px(80))));
        var total = Math.Max(0.0001, _sessions.Sum(s => s.CostUsd));
        foreach (var g in _sessions.GroupBy(s => s.Project).OrderByDescending(g => g.Sum(s => s.CostUsd)))
        {
            var segs = g.SelectMany(s => ActivityService.Clip(s.Segments, from, to)).ToList();
            var c = g.Sum(s => s.CostUsd);
            var name = Stack(4, Row((Square(ColorFor(g.Key), 7), Auto), (Text(g.Key, 12.5), Star)), Bar(c / total, ColorFor(g.Key), 3));
            list.Add(TableRow((name, Star), (Mono(g.Count().ToString(), 11.5), Px(80)), (Mono(Dur(ActivityService.Union(segs)), 11.5, "Text"), Px(90)),
                (Mono(Dur(TimeSpan.FromTicks(segs.Sum(x => (x.Item2 - x.Item1).Ticks))), 11.5), Px(90)), (Mono(Tok(g.Sum(s => s.TotalTokens)), 11.5), Px(80)), (Mono(Money(c), 11.5, "Accent"), Px(80))));
            list.Add(Line());
        }
        return Ui.Card(list, new Thickness(4, 4));
    }
}
