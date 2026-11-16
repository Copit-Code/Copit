using Copit.Controls;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Kanban-Board der Agents: Inbox → In Progress → Waiting for Subagent → Need Input → Review → Done.</summary>
public class BoardView : ContentView, IActivatable
{
    private readonly SessionManager _mgr;
    private readonly DataService _data;
    private readonly SettingsService _settings;
    private readonly NavigationService _nav;
    private readonly ToastService _toast;
    private readonly ExtensionService _ext;
    private readonly Grid _root = new();
    private readonly Grid _overlay = new() { IsVisible = false, BackgroundColor = Color.FromArgb("#D0100C0A"), ZIndex = 10 };
    private string _projectFilter = "";
    private bool _visible;
    private IDispatcherTimer? _timer;

    public BoardView(SessionManager mgr, DataService data, SettingsService settings, NavigationService nav, ToastService toast, ExtensionService ext)
    {
        _mgr = mgr; _data = data; _settings = settings; _nav = nav; _toast = toast; _ext = ext;
        BackgroundColor = C("Bg");
        Content = new Grid { Children = { _root, _overlay } };
        mgr.AgentsChanged += () => { if (_visible) Render(); };
        nav.Navigated += (r, _) => { _visible = r == Routes.Board; if (!_visible) _timer?.Stop(); };
    }

    public void OnActivated(object? parameter)
    {
        _visible = true;
        Render();
        if (parameter as string == "new") ShowNewAgent();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
    }

    // Laufende Karten (Status-Text, Zeiten) regelmäßig auffrischen
    private void Tick(object? s, EventArgs e) { if (_visible && _mgr.RunningCount > 0) Render(); }

    private List<ChatSession> Agents() => _mgr.Agents
        .Where(a => _projectFilter == "" || a.ProjectId == _projectFilter)
        .Where(a => a.Messages.Count > 0 || a.Status != AgentStatus.Inbox || a.Ticket != null || a.Title != "Neue Session")
        .ToList();

    private void Render()
    {
        var agents = Agents();
        var page = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)], Padding = new Thickness(26, 22, 26, 16), RowSpacing = 18 };

        // Kopf
        var running = agents.Count(a => _mgr.Host(a).IsRunning);
        var cost = agents.Sum(a => a.TotalCostUsd);
        var head = Row(
            (Stack(3, Caption("Agents"), H1("Board"),
                Text($"{agents.Count} Agents · {running} aktiv · {agents.Count(a => a.Status == AgentStatus.NeedInput)} warten auf dich · {Money(cost)} gesamt", 12, "Muted")), Star),
            (ProjectFilter(), Auto),
            (Primary("Neuer Agent", ShowNewAgent), Auto));
        head.VerticalOptions = LayoutOptions.End;
        page.Add(head, 0, 0);

        // Spalten
        var cols = new HorizontalStackLayout { Spacing = 12 };
        foreach (var (status, title, color) in AgentStatusInfo.Columns)
        {
            var items = agents.Where(a => a.Status == status).OrderByDescending(a => a.UpdatedAt).ToList();
            if (status == AgentStatus.Failed && items.Count == 0) continue;
            cols.Add(Column(status, title, Hex(color), items));
        }
        page.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = cols }, 0, 1);

        _root.Children.Clear();
        _root.Add(page);
    }

    private View ProjectFilter()
    {
        var items = new List<(string, string)> { ("", "Alle") };
        items.AddRange(_data.Projects.Value.Projects.Take(6).Select(p => (p.Id, string.IsNullOrEmpty(p.EpicKey) ? p.Name : p.EpicKey)));
        return Seg(items, _projectFilter, k => { _projectFilter = k; Render(); });
    }

    private View Column(AgentStatus status, string title, Color color, List<ChatSession> items)
    {
        var list = new VerticalStackLayout { Spacing = 8 };
        foreach (var a in items) list.Add(Card(a));
        if (items.Count == 0) list.Add(new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.Rectangle(), Stroke = C("LineSoft"), StrokeDashArray = [3, 3], StrokeThickness = 1, HeightRequest = 64,
            Content = new Label { Text = "Karte hierher ziehen", FontSize = 11, TextColor = C("Muted"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
        });

        var head = Row((Square(color), Auto), (Mono(title.ToUpperInvariant(), 10.5, "Text", true), Star), (Mono(items.Count.ToString(), 10.5, "Muted"), Auto));
        head.ColumnSpacing = 8;
        var col = new Grid { WidthRequest = 284, RowDefinitions = [new(GridLength.Auto), new(new GridLength(2)), new(GridLength.Star)], RowSpacing = 8, BackgroundColor = C("Bg2"), Padding = new Thickness(10, 10, 10, 10) };
        col.Add(head, 0, 0);
        col.Add(new BoxView { Color = color.WithAlpha(0.6f) }, 0, 1);
        col.Add(new ScrollView { Content = list }, 0, 2);

        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, e) => { col.BackgroundColor = color.WithAlpha(0.08f); e.AcceptedOperation = DataPackageOperation.Copy; };
        drop.DragLeave += (_, _) => col.BackgroundColor = C("Bg2");
        drop.Drop += (_, e) =>
        {
            col.BackgroundColor = C("Bg2");
            if (e.Data.Properties.TryGetValue("agent", out var id) && id is string sid && _mgr.Agents.FirstOrDefault(x => x.Id == sid) is { } ag)
                Move(ag, status);
        };
        col.GestureRecognizers.Add(drop);
        return col;
    }

    private void Move(ChatSession a, AgentStatus to)
    {
        var h = _mgr.Host(a);
        if (h.IsRunning && to is AgentStatus.Done or AgentStatus.Inbox) h.Stop();
        h.BoardStatus = to;
        Render();
    }

    private View Card(ChatSession a)
    {
        var h = _mgr.Host(a);
        var p = _data.Project(a.ProjectId);
        var color = Hex(AgentStatusInfo.Color(a.Status));

        IconButton? menuBtn = null;
        menuBtn = IconBtn("list", () => _ = Menu(a, menuBtn), "Aktionen", 13);
        var top = Row((Text(a.Title, 13, "Text", true), Star), (menuBtn, Auto));
        var meta = new HorizontalStackLayout { Spacing = 8 };
        if (a.Ticket != null) meta.Add(Mono(a.Ticket, 10.5, "Accent", true));
        if (a.Branch != null) meta.Add(HStack(4, Glyph("branch", 11, "Muted"), Mono(a.Branch, 10.5, "Muted")));
        if (a.Worktree) meta.Add(Badge("worktree", C("Cyan")));
        var sub = Text($"{p?.Name ?? "?"} · {a.Provider}{(string.IsNullOrEmpty(h.ModelName) && string.IsNullOrEmpty(a.Model) ? "" : " · " + Core.Services.Pricing.PrettyModel(string.IsNullOrEmpty(h.ModelName) ? a.Model : h.ModelName))}", 11, "Muted");

        var body = Stack(6, top, sub);
        if (meta.Children.Count > 0) body.Add(meta);

        if (h.IsRunning)
        {
            var live = Row((new AiOrb { IsActive = true, WidthRequest = 12, HeightRequest = 12, VerticalOptions = LayoutOptions.Center }, Auto),
                (Text(string.IsNullOrEmpty(h.Status) ? "arbeitet…" : h.Status, 11, "Text2"), Star), (Mono(h.Elapsed, 10.5, "Muted"), Auto));
            live.ColumnSpacing = 6;
            body.Add(live);
        }
        if (a.Status == AgentStatus.NeedInput)
        {
            var need = a.PendingPermissions.Count > 0 ? $"{a.PendingPermissions.Count} Freigabe(n): {string.Join(", ", a.PendingPermissions.Select(d => d.ToolName).Distinct())}"
                : a.PendingQuestion ?? "Wartet auf deine Antwort";
            body.Add(Box(Wrap(need.Length > 140 ? need[..140] + "…" : need, 11.5, "Text"), C("AccentSoft"), new Thickness(8, 6)));
        }
        if (a.Error != null && a.Status == AgentStatus.Failed)
            body.Add(Wrap(a.Error.Length > 160 ? a.Error[..160] + "…" : a.Error, 11, "Red"));
        else if (a.Summary != null && a.Status is AgentStatus.Review or AgentStatus.Done)
            body.Add(Wrap(a.Summary.Length > 150 ? a.Summary[..150] + "…" : a.Summary, 11, "Text2"));

        // Kennzahlen: AI-Zeit · Agent-Zeit · Kosten · Zeilen
        var stats = new HorizontalStackLayout { Spacing = 10 };
        stats.Add(HStack(3, Mono("AI", 9.5, "Muted"), Mono(SessionHost.FmtMs(a.AiMs), 10.5, "Text2")));
        stats.Add(HStack(3, Mono("AGENT", 9.5, "Muted"), Mono(SessionHost.FmtMs(a.AgentMs), 10.5, "Text2")));
        if (a.TotalCostUsd > 0) stats.Add(Mono(Money(a.TotalCostUsd), 10.5, "Text2"));
        if (a.LinesAdded + a.LinesRemoved > 0)
            stats.Add(HStack(3, Mono($"+{a.LinesAdded}", 10.5, "Green"), Mono($"−{a.LinesRemoved}", 10.5, "Red")));
        body.Add(Line());
        body.Add(stats);

        var card = new Grid { ColumnDefinitions = [new(new GridLength(2)), new(GridLength.Star)] };
        card.Add(new BoxView { Color = color }, 0);
        var inner = Ui.Card(body, new Thickness(12, 10), "LineSoft");
        card.Add(inner, 1);
        Hover(inner, C("Card"));
        OnTap(inner, () => _nav.Go(Routes.Session, h));

        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, e) => { e.Data.Properties["agent"] = a.Id; e.Data.Text = a.Title; };
        card.GestureRecognizers.Add(drag);
        return card;
    }

    private async Task Menu(ChatSession a, VisualElement? anchor = null)
    {
        var h = _mgr.Host(a);
        var opts = new List<string> { "Öffnen" };
        if (h.IsRunning) opts.Add("Stoppen");
        opts.AddRange(AgentStatusInfo.Columns.Where(c => c.Status != a.Status).Select(c => "→ " + c.Title));
        opts.Add("Archivieren");
        opts.Add("Löschen");
        var pick = anchor != null ? await Copit.Controls.Overlay.Choose(a.Title, opts, anchor) : await Dialogs.Choose(a.Title, [.. opts]);
        if (pick == null) return;
        if (pick == "Öffnen") _nav.Go(Routes.Session, h);
        else if (pick == "Stoppen") h.Stop();
        else if (pick.StartsWith("→ ")) Move(a, AgentStatusInfo.Columns.First(c => c.Title == pick[2..]).Status);
        else if (pick == "Archivieren") _mgr.Archive(a);
        else if (pick == "Löschen" && await Dialogs.Confirm("Agent löschen?", $"{a.Title}{(a.Worktree ? "\nDer Worktree wird ebenfalls entfernt." : "")}", "Löschen"))
            await _mgr.DeleteAsync(a);
        Render();
    }

    // ------------------------------------------------------------ Neuer Agent

    private void ShowNewAgent()
    {
        var projects = _data.Projects.Value.Projects.OrderByDescending(p => p.LastOpened).ToList();
        if (projects.Count == 0) { _toast.Show("Erst ein Projekt anlegen", "Projekte → Neues Projekt", NoticeLevel.Warning); return; }
        var project = projects.FirstOrDefault(p => p.Id == _projectFilter) ?? projects[0];
        string title = "", ticket = "", branch = "", prompt = "", model = "";
        var provider = _settings.Settings.Ai.DefaultProvider;
        var mode = AgentMode.Edit;
        var worktree = _settings.Settings.Ai.UseWorktrees;
        var startNow = true;
        List<string> extraTools = [];
        var templates = _ext.AgentTemplates.ToList();
        var templateIndex = 0;

        var form = new VerticalStackLayout { Spacing = 12 };
        void Build()
        {
            form.Children.Clear();
            form.Add(Row((Stack(2, Caption("Neuer Agent"), H2("Auftrag anlegen")), Star), (IconBtn("x", Close, "Schließen"), Auto)));
            if (templates.Count > 0)
                form.Add(Stack(4, Caption("Vorlage (aus Extensions)"), Pick(["Keine Vorlage", .. templates.Select(t => $"{t.Template.Title} · {t.Ext.Manifest.Name}")], templateIndex, i =>
                {
                    templateIndex = i;
                    if (i == 0) { extraTools = []; Build(); return; }
                    var t = templates[i - 1].Template;
                    string Fill(string x) => x.Replace("{{ticket}}", ticket.Trim().ToUpperInvariant()).Replace("{{project}}", project.Name).Replace("{{branch}}", branch);
                    title = Fill(t.Title);
                    prompt = Fill(t.Prompt);
                    if (Enum.TryParse<AgentMode>(t.Mode, true, out var md)) mode = md;
                    if (!string.IsNullOrWhiteSpace(t.Branch) && branch.Length == 0) branch = Fill(t.Branch!);
                    extraTools = t.AllowedTools;
                    Build();
                })));
            form.Add(Stack(4, Caption("Projekt"), Pick(projects.Select(p => $"{p.Name}{(string.IsNullOrEmpty(p.EpicKey) ? "" : " · " + p.EpicKey)}").ToList(), projects.IndexOf(project), i => project = projects[i])));
            form.Add(Stack(4, Caption("Titel"), Input(title, "z. B. Checkout-Validierung refactoren", v => title = v)));
            form.Add(Row((Stack(4, Caption("Ticket"), Input(ticket, "SHOP-123", v =>
            {
                ticket = v;
            })), Star), (Stack(4, Caption("Branch (optional)"), Input(branch, "feature/SHOP-123", v => branch = v)), Star)));
            form.Add(Stack(4, Caption("Auftrag"), Area(prompt, "Was soll der Agent tun? Kontext, Akzeptanzkriterien, Grenzen…", v => prompt = v, 110)));
            form.Add(Row(
                (Stack(4, Caption("Anbieter"), Seg([("Claude", "Claude"), ("Copilot", "Copilot")], provider.ToString(), k => { provider = Enum.Parse<AiProvider>(k); Build(); })), Auto),
                (Stack(4, Caption("Modus"), Seg([("Ask", "Fragen"), ("Plan", "Plan"), ("Edit", "Edit"), ("Auto", "Auto")], mode.ToString(), k => { mode = Enum.Parse<AgentMode>(k); Build(); })), Auto),
                (Stack(4, Caption("Modell"), Input(model, provider == AiProvider.Claude ? "Standard (z. B. opus)" : "Standard", v => model = v)), Star)));
            form.Add(Row((Toggle(worktree, v => worktree = v), Auto), (Wrap("Eigener git worktree (parallel arbeiten, ohne deinen Branch zu wechseln)", 12, "Text2"), Star)));
            form.Add(Row((Toggle(startNow, v => startNow = v), Auto), (Wrap("Sofort starten (sonst bleibt die Karte in der Inbox)", 12, "Text2"), Star)));
            var create = Primary("Agent anlegen", async () =>
            {
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(prompt)) { _toast.Show("Titel oder Auftrag fehlt", "", NoticeLevel.Warning); return; }
                if (string.IsNullOrWhiteSpace(title)) title = prompt.Split('\n')[0] is var t && t.Length > 60 ? t[..60] : prompt.Split('\n')[0];
                if (string.IsNullOrWhiteSpace(branch) && !string.IsNullOrWhiteSpace(ticket) && worktree) branch = $"feature/{ticket.Trim().ToUpperInvariant()}";
                Close();
                try
                {
                    var h = await _mgr.CreateAgentAsync(project, title, ticket, branch, mode, provider, model.Trim(), worktree);
                    if (extraTools.Count > 0) { h.Session.AllowedTools.AddRange(extraTools.Where(x => !h.Session.AllowedTools.Contains(x))); h.Save(); }
                    if (h.Session.Error != null) _toast.Show("Hinweis", h.Session.Error, NoticeLevel.Warning);
                    if (startNow && !string.IsNullOrWhiteSpace(prompt))
                    {
                        var full = string.IsNullOrWhiteSpace(ticket) ? prompt : $"Ticket {ticket.Trim().ToUpperInvariant()}: {prompt}";
                        _ = h.SendAsync(full, prompt);
                    }
                    else if (!string.IsNullOrWhiteSpace(prompt)) h.Session.PendingQuestion = null;
                    Render();
                }
                catch (Exception e) { _toast.Error(e, "Agent anlegen"); }
            });
            form.Add(Row((new BoxView(), Star), (Ghost("Abbrechen", Close), Auto), (create, Auto)));
        }
        Build();
        _overlay.Children.Clear();
        _overlay.Add(new Border
        {
            Content = new ScrollView { Content = form }, WidthRequest = 620, MaximumHeightRequest = 760, Padding = new Thickness(22, 18),
            BackgroundColor = C("Panel"), Stroke = C("Line"), StrokeShape = new Microsoft.Maui.Controls.Shapes.Rectangle(),
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        });
        _overlay.IsVisible = true;
    }

    private void Close() => _overlay.IsVisible = false;
}
