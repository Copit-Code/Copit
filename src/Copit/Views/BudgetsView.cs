using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Budgets pro Projekt (Woche/Monat/Gesamt, Kosten oder Stunden) und Zusammenfassung über alles.</summary>
public class BudgetsView : ContentView, IActivatable
{
    private readonly SessionManager _mgr;
    private readonly DataService _data;
    private readonly ToastService _toast;
    private readonly Grid _root = new();
    private readonly Grid _overlay = new() { IsVisible = false, BackgroundColor = Color.FromArgb("#D0100C0A"), ZIndex = 10 };
    private List<UsageRecord> _records = [];

    public BudgetsView(SessionManager mgr, DataService data, ToastService toast)
    {
        _mgr = mgr; _data = data; _toast = toast;
        BackgroundColor = C("Bg");
        Content = new Grid { Children = { _root, _overlay } };
    }

    public void OnActivated(object? parameter) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var now = DateTime.Now;
        var from = BudgetService.EarliestNeeded(_data.Budgets.Value.Budgets, now);
        var agents = _mgr.Agents.ToList();
        try
        {
            _records = await Task.Run(() => ActivityService.Records(_mgr.Activity.Build(from, now.AddMinutes(1), agents, id => _data.Project(id)?.SourceFolder)));
        }
        catch (Exception e) { App.LogCrash(e); _records = []; }
        Render();
    }

    private static Color LevelColor(double f) => f >= 1 ? C("Red") : f >= 0.8 ? C("Amber") : C("Green");

    private string Fmt(Budget b, double v) => b.Type == BudgetType.Cost ? Money(v) : $"{v:0.#} h";

    private void Render()
    {
        var o = BudgetService.Overview(_records);
        var page = new VerticalStackLayout { Spacing = 18, Padding = new Thickness(26, 22, 26, 30) };
        page.Add(Row((Stack(3, Caption("Budgets"), H1("Budget & Verbrauch"), Text("Wöchentliche Budgets pro Projekt – Verbrauch aus allen Agent-Sessions (Claude Code + Copit)", 12, "Muted")), Star),
            (IconBtn("refresh", () => _ = LoadAsync(), "Neu laden"), Auto), (Primary("Budget", () => Edit(null)), Auto)));

        // Zusammenfassung über alles
        var stats = new Grid { ColumnSpacing = 12, ColumnDefinitions = [new(Star), new(Star), new(Star), new(Star)] };
        stats.Add(Stat("Diese Woche", Money(o.WeekCost), $"{o.WeekHours:0.#} h AI time", "Accent"), 0);
        stats.Add(Stat("Dieser Monat", Money(o.MonthCost), $"{o.MonthHours:0.#} h AI time"), 1);
        var budgets = _data.Budgets.Value.Budgets.Select(b => BudgetService.Evaluate(b, _records)).ToList();
        var weekBudget = budgets.Where(s => s.Budget.Period == BudgetPeriod.Week && s.Budget.Type == BudgetType.Cost).Sum(s => s.Budget.Amount);
        stats.Add(Stat("Wochenbudgets gesamt", weekBudget > 0 ? Money(weekBudget) : "–", weekBudget > 0 ? $"{o.WeekCost / weekBudget * 100:0}% verbraucht" : "noch keine Kosten-Budgets"), 2);
        stats.Add(Stat("Über Limit", budgets.Count(s => s.Fraction >= 1).ToString(), $"{budgets.Count(s => s.Fraction is >= 0.8 and < 1)} über 80 %", budgets.Any(s => s.Fraction >= 1) ? "Red" : "Text"), 3);
        page.Add(stats);

        // 14 Tage
        page.Add(Caption("Letzte 14 Tage"));
        page.Add(Ui.Card(DaysChart(o), new Thickness(14, 12)));

        // Budgets
        page.Add(Caption("Budgets"));
        if (budgets.Count == 0)
            page.Add(Ui.Card(Stack(8, Text("Noch keine Budgets", 13, "Text", true),
                Wrap("Lege z. B. ein Wochenbudget von $50 für ein Projekt an. Bei 80 % und 100 % meldet sich Copit, mit Hard-Stop starten keine neuen Agent-Läufe mehr.", 12, "Muted"),
                HStack(8, Chip("Wochenbudget anlegen", () => Edit(null))))));
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, ColumnDefinitions = [new(Star), new(Star)] };
        for (int i = 0; i < budgets.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Add(BudgetCard(budgets[i]), i % 2, i / 2);
        }
        page.Add(grid);

        // Nach Projekt / Anbieter (Monat)
        var split = new Grid { ColumnSpacing = 12, ColumnDefinitions = [new(new GridLength(2, GridUnitType.Star)), new(Star)] };
        split.Add(Breakdown("Monat nach Projekt", o.ByProject.Select(x => (x.Project, x.Cost, x.Hours)).ToList(), o.MonthCost), 0);
        split.Add(Breakdown("Monat nach Quelle", o.ByProvider.Select(x => (x.Provider, x.Cost, x.Hours)).ToList(), o.MonthCost), 1);
        page.Add(split);

        _root.Children.Clear();
        _root.Add(new ScrollView { Content = page });
    }

    private View DaysChart(BudgetOverview o)
    {
        var max = Math.Max(0.01, o.Days.Max(d => d.Cost));
        var chart = new Grid { ColumnSpacing = 8, HeightRequest = 150 };
        for (int i = 0; i < o.Days.Count; i++)
        {
            var d = o.Days[i];
            chart.ColumnDefinitions.Add(new ColumnDefinition(Star));
            var col = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 3 };
            col.Add(Mono(d.Cost > 0 ? Money(d.Cost) : "", 9.5, "Text2"), 0, 0);
            col.Add(new BoxView { Color = d.Day == DateTime.Today ? C("Accent") : C("Accent").WithAlpha(0.45f), HeightRequest = Math.Max(d.Cost > 0 ? 2 : 0, d.Cost / max * 100), VerticalOptions = LayoutOptions.End }, 0, 1);
            col.Add(Mono(d.Day.ToString("dd."), 9.5, d.Day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Muted" : "Text2"), 0, 2);
            ToolTipProperties.SetText(col, $"{d.Day:ddd dd.MM.} · {Money(d.Cost)} · {d.Hours:0.#} h");
            chart.Add(col, i);
        }
        return chart;
    }

    private View BudgetCard(BudgetStatus s)
    {
        var b = s.Budget;
        var color = LevelColor(s.Fraction);
        var folders = b.Folders.Count == 0 ? "Alle Projekte" : string.Join(", ", b.Folders.Select(ActivityService.ProjectName));
        var head = Row((Stack(2, Text(b.Name, 14, "Text", true), Text(folders, 11, "Muted")), Star),
            (b.HardStop ? Badge("hard-stop", C("Red")) : new BoxView { WidthRequest = 0 }, Auto),
            (IconBtn("pencil", () => Edit(b), "Bearbeiten", 13), Auto),
            (IconBtn("trash", async () =>
            {
                if (!await Dialogs.Confirm("Budget löschen?", b.Name, "Löschen")) return;
                _data.Budgets.Value.Budgets.Remove(b);
                await _data.Budgets.SaveAsync();
                Render();
            }, "Löschen", 13), Auto));
        var value = Row((new Label { Text = Fmt(b, s.Used), FontSize = 24, FontFamily = MonoFont, FontAttributes = FontAttributes.Bold, TextColor = color }, Auto),
            (Text($"von {Fmt(b, b.Amount)} · {s.PeriodLabel}", 12, "Muted"), Star),
            (Mono($"{s.Fraction * 100:0}%", 12, s.Fraction >= 1 ? "Red" : "Text2", true), Auto));
        var rest = b.Amount - s.Used;
        var hint = rest >= 0 ? $"Noch {Fmt(b, rest)} frei" : $"{Fmt(b, -rest)} über Budget";
        if (b.Period == BudgetPeriod.Week && s.Used > 0)
        {
            var (from, to, _, _) = BudgetService.Range(BudgetPeriod.Week, DateTime.Now);
            var elapsed = Math.Max(0.15, (DateTime.Now - from).TotalDays);
            var forecast = s.Used / elapsed * 7;
            hint += $" · Prognose Wochenende {Fmt(b, forecast)}";
        }
        return Ui.Card(Stack(10, head, value, Bar(s.Fraction, color, 5), Text(hint, 11.5, "Text2"),
            Text($"Warnungen bei {string.Join(" / ", b.WarnAt.Select(w => w + " %"))}", 10.5, "Muted")), stroke: s.Fraction >= 1 ? "Red" : "Line");
    }

    private View Breakdown(string title, List<(string Name, double Cost, double Hours)> rows, double total)
    {
        var list = Stack(8, Caption(title));
        if (rows.Count == 0) list.Add(Text("Keine Daten", 12, "Muted"));
        foreach (var (name, cost, hours) in rows.Take(12))
            list.Add(Stack(4, Row((Square(ColorFor(name), 7), Auto), (Text(name, 12.5), Star), (Mono($"{hours:0.#} h", 11, "Muted"), Auto), (Mono(Money(cost), 11.5, "Accent"), Px(64))),
                Bar(total > 0 ? cost / total : 0, ColorFor(name), 3)));
        return Ui.Card(list);
    }

    // ------------------------------------------------------------ Editor

    private void Edit(Budget? existing)
    {
        var b = existing ?? new Budget { Name = "Wochenbudget", Amount = 50 };
        string name = b.Name, amount = b.Amount.ToString("0.##"), warn = string.Join(",", b.WarnAt);
        var type = b.Type; var period = b.Period; var hard = b.HardStop;
        var folders = b.Folders.ToList();
        var form = new VerticalStackLayout { Spacing = 12 };
        void Build()
        {
            form.Children.Clear();
            form.Add(Row((Stack(2, Caption(existing == null ? "Neues Budget" : "Budget bearbeiten"), H2(string.IsNullOrWhiteSpace(name) ? "Budget" : name)), Star), (IconBtn("x", Close), Auto)));
            form.Add(Stack(4, Caption("Name"), Input(name, "z. B. Shop – Woche", v => name = v)));
            form.Add(Row((Stack(4, Caption("Art"), Seg([("Cost", "Kosten $"), ("Hours", "Stunden")], type.ToString(), k => { type = Enum.Parse<BudgetType>(k); Build(); })), Auto),
                (Stack(4, Caption("Zeitraum"), Seg([("Week", "Woche"), ("Month", "Monat"), ("Total", "Gesamt")], period.ToString(), k => { period = Enum.Parse<BudgetPeriod>(k); Build(); })), Auto),
                (Stack(4, Caption(type == BudgetType.Cost ? "Betrag (USD)" : "Stunden"), Input(amount, "50", v => amount = v)), Star)));
            var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var p in _data.Projects.Value.Projects.Where(p => !string.IsNullOrWhiteSpace(p.SourceFolder)))
            {
                var on = folders.Any(f => string.Equals(f, p.SourceFolder, StringComparison.OrdinalIgnoreCase));
                var chip = Box(Mono(p.Name.ToUpperInvariant(), 10.5, on ? "Text" : "Muted"), on ? C("AccentSoft") : Colors.Transparent, new Thickness(10, 5), on ? C("Accent") : C("Line"));
                chip.Margin = new Thickness(0, 0, 6, 6);
                var folder = p.SourceFolder;
                OnTap(chip, () => { if (on) folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)); else folders.Add(folder); Build(); });
                chips.Add(chip);
            }
            foreach (var f in folders.Where(f => !_data.Projects.Value.Projects.Any(p => string.Equals(p.SourceFolder, f, StringComparison.OrdinalIgnoreCase))))
            {
                var ff = f;
                var chip = Box(Mono(ActivityService.ProjectName(f).ToUpperInvariant() + "  ×", 10.5, "Text"), C("AccentSoft"), new Thickness(10, 5), C("Accent"));
                chip.Margin = new Thickness(0, 0, 6, 6);
                OnTap(chip, () => { folders.Remove(ff); Build(); });
                chips.Add(chip);
            }
            form.Add(Stack(6, Caption("Projekte / Ordner (leer = alle)"), chips,
                HStack(8, Chip("Ordner wählen", async () => { var d = await Dialogs.PickFolderAsync(); if (d != null) { folders.Add(d); Build(); } }))));
            form.Add(Stack(4, Caption("Warnungen bei (%)"), Input(warn, "80,100", v => warn = v)));
            form.Add(Row((Toggle(hard, v => hard = v), Auto), (Wrap("Hard-Stop: bei 100 % keine neuen Agent-Läufe in diesen Projekten starten", 12, "Text2"), Star)));
            form.Add(Row((new BoxView(), Star), (Ghost("Abbrechen", Close), Auto), (Primary("Speichern", async () =>
            {
                if (!double.TryParse(amount.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amt) || amt <= 0)
                { _toast.Show("Ungültiger Betrag", amount, Core.Services.NoticeLevel.Warning); return; }
                b.Name = string.IsNullOrWhiteSpace(name) ? "Budget" : name.Trim();
                b.Type = type; b.Period = period; b.Amount = amt; b.HardStop = hard; b.Folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                b.WarnAt = warn.Split(',', ';', ' ').Select(x => int.TryParse(x.Trim().TrimEnd('%'), out var n) ? n : -1).Where(n => n is > 0 and <= 500).Distinct().OrderBy(n => n).ToList();
                if (existing == null) _data.Budgets.Value.Budgets.Add(b);
                await _data.Budgets.SaveAsync();
                Close();
                Render();
            }), Auto)));
        }
        Build();
        _overlay.Children.Clear();
        _overlay.Add(new Border
        {
            Content = new ScrollView { Content = form }, WidthRequest = 600, MaximumHeightRequest = 760, Padding = new Thickness(22, 18),
            BackgroundColor = C("Panel"), Stroke = C("Line"), StrokeShape = new Microsoft.Maui.Controls.Shapes.Rectangle(),
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        });
        _overlay.IsVisible = true;
    }

    private void Close() => _overlay.IsVisible = false;
}
