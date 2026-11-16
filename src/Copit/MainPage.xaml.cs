using Copit.Controls;
using Copit.Core.Services;
using Copit.Services;
using Copit.ViewModels;
using Copit.Views;

namespace Copit;

public partial class MainPage : ContentPage
{
    private readonly IServiceProvider _sp;
    private readonly NavigationService _nav;
    private readonly Dictionary<string, View> _views = [];
    private readonly Dictionary<string, (Grid Row, BoxView Bar, Icon Icon, Label Label)> _navRows = [];
    private View? _current;

    private static readonly (string Route, string Icon, string Label, bool Bottom)[] NavItems =
    [
        (Routes.Home, "home", "Cockpit", false),
        (Routes.Time, "calendar", "Zeitbuchung", false),
        (Routes.Board, "list", "Agents", false),
        (Routes.Session, "chat", "Session", false),
        (Routes.Activity, "activity", "Activity", false),
        (Routes.Budgets, "package", "Budgets", false),
        (Routes.Projects, "folder", "Projekte", false),
        (Routes.Automations, "flow", "Automationen", false),
        (Routes.Tools, "grid", "Tools", false),
        (Routes.Customize, "lock", "Customize", true),
        (Routes.Settings, "sliders", "Einstellungen", true),
    ];

    public MainPage(IServiceProvider sp, NavigationService nav, ToastService toasts, ShellViewModel shell,
        AutomationService automations, DataService data)
    {
        _sp = sp; _nav = nav;
        InitializeComponent();
        BindingContext = shell;

        foreach (var item in NavItems)
        {
            var row = NavRow(item.Route, item.Icon, item.Label);
            (item.Bottom ? NavBottom : NavStack).Children.Add(row);
        }

        nav.Navigated += Show;
        toasts.Shown += ShowToast;
        automations.Notified += n => toasts.Show(n.Title, n.Message, n.Level, n.Action, n.ActionArg);

        Loaded += async (_, _) =>
        {
            data.EnsureSeeds();
            shell.LoadProjects();
            var settings = sp.GetRequiredService<SettingsService>();
            var start = Environment.GetEnvironmentVariable("COPIT_START_ROUTE");
            Show(!string.IsNullOrEmpty(start) ? start : settings.JiraConfigured || settings.GitLabConfigured ? Routes.Home : Routes.Settings, null);
            shell.StartClock();
            sp.GetRequiredService<SessionManager>(); // verbindet Agents mit dem Automations-Motor
            automations.Start();
            StartBudgetWatch(data, toasts);
            StartUpdateWatch(toasts);
            await shell.RefreshConnectionsAsync();
        };
    }

    private void StartUpdateWatch(ToastService toasts)
    {
        var settings = _sp.GetRequiredService<SettingsService>();
        var updates = _sp.GetRequiredService<UpdateService>();
        async Task Check()
        {
            if (!settings.Settings.Update.Auto) return;
            try
            {
                var r = await updates.CheckAsync();
                if (r != null) toasts.Show($"Copit {r.Tag} ist verfügbar", "Klicken zum Installieren", NoticeLevel.Info, "update", r.Tag);
            }
            catch { /* offline oder kein Token – still */ }
        }
        Dispatcher.StartTimer(TimeSpan.FromSeconds(30), () => { _ = Check(); return false; });
        Dispatcher.StartTimer(TimeSpan.FromHours(6), () => { _ = Check(); return true; });
    }

    private void StartBudgetWatch(DataService data, ToastService toasts)
    {
        async Task Check()
        {
            var budgets = data.Budgets.Value.Budgets;
            if (budgets.Count == 0) return;
            var mgr = _sp.GetRequiredService<SessionManager>();
            var now = DateTime.Now;
            var agents = mgr.Agents.ToList();
            var recs = await Task.Run(() => ActivityService.Records(mgr.Activity.Build(BudgetService.EarliestNeeded(budgets, now), now.AddMinutes(1), agents, id => data.Project(id)?.SourceFolder)));
            var changed = false;
            foreach (var b in budgets)
            {
                var st = BudgetService.Evaluate(b, recs, now);
                foreach (var pct in BudgetService.NewThresholds(st))
                {
                    b.Notified.Add($"{st.PeriodKey}:{pct}");
                    changed = true;
                    toasts.Show($"Budget {b.Name}: {pct} % erreicht", $"{(b.Type == BudgetType.Cost ? $"${st.Used:0.00} von ${b.Amount:0.##}" : $"{st.Used:0.#} h von {b.Amount:0.#} h")} · {st.PeriodLabel}{(b.HardStop && pct >= 100 ? " · Hard-Stop aktiv" : "")}",
                        pct >= 100 ? NoticeLevel.Error : NoticeLevel.Warning);
                }
            }
            if (changed) await data.Budgets.SaveAsync();
        }
        Dispatcher.StartTimer(TimeSpan.FromMinutes(5), () => { _ = Check(); return true; });
        Dispatcher.StartTimer(TimeSpan.FromSeconds(20), () => { _ = Check(); return false; });
    }

    private static Color C(string key) => (Color)Application.Current!.Resources[key];

    private Grid NavRow(string route, string icon, string text)
    {
        var bar = new BoxView { WidthRequest = 2, Color = Colors.Transparent, HorizontalOptions = LayoutOptions.Start };
        var ic = new Icon { Name = icon, Size = 15, Color = C("Text2") };
        var lbl = new Label { Text = text, FontSize = 13, TextColor = C("Text2"), VerticalOptions = LayoutOptions.Center };
        var row = new Grid
        {
            HeightRequest = 30,
            ColumnDefinitions = [new(new GridLength(2)), new(new GridLength(30)), new(GridLength.Star)],
            BackgroundColor = Colors.Transparent
        };
        row.Add(bar, 0);
        row.Add(ic, 1);
        row.Add(lbl, 2);
        row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => _nav.Go(route)) });
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => { if (ActiveRoute != route) row.BackgroundColor = C("Card"); };
        hover.PointerExited += (_, _) => { if (ActiveRoute != route) row.BackgroundColor = Colors.Transparent; };
        row.GestureRecognizers.Add(hover);
        _navRows[route] = (row, bar, ic, lbl);
        return row;
    }

    private string ActiveRoute { get; set; } = "";

    private View Resolve(string route)
    {
        if (_views.TryGetValue(route, out var v)) return v;
        v = route switch
        {
            Routes.Home => _sp.GetRequiredService<DashboardView>(),
            Routes.Time => _sp.GetRequiredService<TimeView>(),
            Routes.Projects => _sp.GetRequiredService<ProjectsView>(),
            Routes.Project => _sp.GetRequiredService<ProjectDetailView>(),
            Routes.Session => _sp.GetRequiredService<SessionView>(),
            Routes.Tools => _sp.GetRequiredService<ToolsView>(),
            Routes.Board => _sp.GetRequiredService<BoardView>(),
            Routes.Activity => _sp.GetRequiredService<ActivityView>(),
            Routes.Budgets => _sp.GetRequiredService<BudgetsView>(),
            Routes.Automations => _sp.GetRequiredService<AutomationsView>(),
            Routes.Customize => _sp.GetRequiredService<CustomizeView>(),
            _ => _sp.GetRequiredService<SettingsView>(),
        };
        v.IsVisible = false;
        ContentHost.Children.Add(v);
        return _views[route] = v;
    }

    private void Show(string route, object? parameter)
    {
        var view = Resolve(route);
        Highlight(route == Routes.Project ? Routes.Projects : route);
        if (view != _current)
        {
            var old = _current;
            _current = view;
            view.Opacity = 0;
            view.IsVisible = true;
            if (old != null) old.IsVisible = false;
            _ = view.FadeTo(1, 140, Easing.CubicOut);
        }
        try
        {
            if (view is IActivatable a) a.OnActivated(parameter);
            else if (view.BindingContext is IActivatable vm) vm.OnActivated(parameter);
        }
        catch (Exception e) { App.LogCrash(e); }
    }

    private void Highlight(string route)
    {
        ActiveRoute = route;
        foreach (var (r, (row, bar, icon, label)) in _navRows)
        {
            var active = r == route;
            row.BackgroundColor = active ? C("CardHover") : Colors.Transparent;
            bar.Color = active ? C("Accent") : Colors.Transparent;
            icon.Color = active ? C("Text") : C("Text2");
            label.TextColor = active ? C("Text") : C("Text2");
        }
    }

    private void ShowToast(Toast t)
    {
        var accent = t.Level switch
        {
            NoticeLevel.Success => C("Green"),
            NoticeLevel.Warning => C("Amber"),
            NoticeLevel.Error => C("Red"),
            _ => C("Accent")
        };
        var icon = t.Level switch { NoticeLevel.Success => "check-circle", NoticeLevel.Warning => "alert", NoticeLevel.Error => "x-circle", _ => "info" };
        var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        grid.Add(new Icon { Name = icon, Size = 16, Color = accent, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, 1, 0, 0) });
        var texts = new VerticalStackLayout { Spacing = 3 };
        texts.Add(new Label { Text = t.Title, FontAttributes = FontAttributes.Bold, FontSize = 12.5, LineBreakMode = LineBreakMode.WordWrap });
        if (!string.IsNullOrWhiteSpace(t.Message))
            texts.Add(new Label { Text = t.Message.Length > 300 ? t.Message[..300] + "…" : t.Message, FontSize = 12, TextColor = C("Text2"), LineBreakMode = LineBreakMode.WordWrap });
        if (t.Action != null)
            texts.Add(new Label { Text = "ÖFFNEN", FontFamily = "Cascadia Mono", CharacterSpacing = 1.4, FontSize = 10.5, TextColor = C("Accent"), Margin = new Thickness(0, 4, 0, 0) });
        grid.Add(texts, 1);
        var card = new Border
        {
            BackgroundColor = C("Panel"), Stroke = C("Line"), StrokeThickness = 1,
            Padding = new Thickness(14, 11), Content = new Grid
            {
                Children = { grid }
            },
            Opacity = 0, TranslationY = 12,
        };
        // Farbiger Rand links
        var wrap = new Grid { ColumnDefinitions = [new(new GridLength(2)), new(GridLength.Star)] };
        wrap.Add(new BoxView { Color = accent }, 0);
        wrap.Add(card, 1);
        wrap.Opacity = 0; wrap.TranslationY = 12;
        card.Opacity = 1; card.TranslationY = 0;
        wrap.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                Dismiss(wrap);
                switch (t.Action)
                {
                    case "book": _nav.Go(Routes.Time, DateOnly.Parse(t.ActionArg!)); break;
                    case "project": _nav.Go(Routes.Project, t.ActionArg); break;
                    case "automation": _nav.Go(Routes.Automations); break;
                    case "update":
                        _ = Task.Run(async () =>
                        {
                            var r = await _sp.GetRequiredService<UpdateService>().CheckAsync(ignoreSkipped: true);
                            if (r != null) MainThread.BeginInvokeOnMainThread(() => _ = _sp.GetRequiredService<CustomizeView>().InstallAsync(r));
                        });
                        break;
                    case "agent":
                        var store = _sp.GetRequiredService<SessionStore>();
                        if (store.Get(t.ActionArg!) is { } ag) _nav.Go(Routes.Session, _sp.GetRequiredService<SessionManager>().Host(ag));
                        break;
                }
            })
        });
        ToastHost.Children.Add(wrap);
        while (ToastHost.Children.Count > 4) ToastHost.Children.RemoveAt(0);
        _ = wrap.FadeTo(1, 160);
        _ = wrap.TranslateTo(0, 0, 200, Easing.CubicOut);
        var ms = t.Level == NoticeLevel.Error ? 9000 : 5500;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(ms), () => { Dismiss(wrap); return false; });
    }

    private async void Dismiss(View card)
    {
        if (!ToastHost.Children.Contains(card)) return;
        await card.FadeTo(0, 150);
        ToastHost.Children.Remove(card);
    }
}
