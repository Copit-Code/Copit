using Copit.Controls;
using Copit.Core.Models;
using Copit.Core.Services;
using Copit.Services;
using static Copit.Views.Ui;

namespace Copit.Views;

/// <summary>Anzeige des Lieferstatus (Commit → Push → MR → Pipeline → Jira) auf Karten und in der Session.</summary>
public static class DeliveryUi
{
    public static string ColorKey(StepState s) => s switch
    {
        StepState.Ok => "Green",
        StepState.Running => "Amber",
        StepState.Warn => "Accent",
        StepState.Fail => "Red",
        _ => "Muted",
    };

    public static string StateIcon(StepState s) => s switch
    {
        StepState.Ok => "check-circle",
        StepState.Running => "loader",
        StepState.Warn => "alert",
        StepState.Fail => "x-circle",
        _ => "circle",
    };

    public static string StepIcon(string key) => key switch
    {
        "commit" => "commit",
        "push" => "push",
        "mr" => "merge",
        "pipeline" => "flow",
        "jira" => "ticket",
        _ => "dot",
    };

    public static string JobIcon(string status) => status switch
    {
        "success" => "check-circle",
        "failed" => "x-circle",
        "running" or "pending" or "created" or "preparing" or "waiting_for_resource" => "loader",
        "manual" => "play",
        "skipped" or "canceled" => "skip",
        _ => "circle",
    };

    public static string JobColor(GitLabJob j) => j.Status switch
    {
        "success" => "Green",
        "failed" => j.AllowFailure ? "Amber" : "Red",
        "running" or "pending" or "created" or "preparing" or "waiting_for_resource" => "Amber",
        _ => "Muted",
    };

    /// <summary>Kompakte Zeile für die Board-Karte: farbige Schritt-Symbole + Urteil in Worten.</summary>
    public static View CardLine(TicketDelivery d)
    {
        var icons = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        foreach (var s in d.Steps)
        {
            var g = Glyph(StepIcon(s.Key), 11, ColorKey(s.State));
            ToolTipProperties.SetText(g, $"{s.Label}: {s.Detail}");
            icons.Add(g);
        }
        var row = Row((icons, Auto), (Text(d.Headline, 11, ColorKey(d.Overall), d.Overall is StepState.Fail or StepState.Ok), Star));
        row.ColumnSpacing = 8;
        return row;
    }

    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { _ = Launcher.Default.OpenAsync(new Uri(url)); } catch { }
    }
}

/// <summary>Lieferstatus-Panel in der Session: Urteil, Schritte, Pipeline-Jobs mit Log.</summary>
public class DeliveryPanel : ContentView
{
    public static readonly BindableProperty HostProperty = BindableProperty.Create(nameof(Host), typeof(SessionHost), typeof(DeliveryPanel),
        propertyChanged: (b, _, _) => ((DeliveryPanel)b).HostChanged());

    public SessionHost? Host { get => (SessionHost?)GetValue(HostProperty); set => SetValue(HostProperty, value); }

    private SessionManager? _mgr;
    private bool _expanded;
    private bool _busy;
    private long? _logJob;
    private string _log = "";
    private string? _forAgent;

    public DeliveryPanel()
    {
        IsVisible = false;
    }

    private void HostChanged()
    {
        var h = Host;
        if (h == null) { IsVisible = false; return; }
        if (_mgr == null)
        {
            _mgr = h.Manager;
            _mgr.DeliveryChanged += id => { if (Host?.Session.Id == id) MainThread.BeginInvokeOnMainThread(Render); };
            _mgr.AgentsChanged += () => { if (Host != null) Render(); };
        }
        if (_forAgent != h.Session.Id) { _forAgent = h.Session.Id; _logJob = null; _log = ""; _expanded = false; }
        Render();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (Host == null || _mgr == null || _busy) return;
        _busy = true; Render();
        try { await _mgr.RefreshDeliveryAsync(Host.Session); }
        finally { _busy = false; MainThread.BeginInvokeOnMainThread(Render); }
    }

    private void Render()
    {
        var h = Host;
        var d = h == null || _mgr == null ? null : _mgr.DeliveryOf(h.Session);
        if (h == null || d == null) { IsVisible = false; return; }
        IsVisible = true;
        if (d.Overall == StepState.Fail && !_expanded && _logJob == null) _expanded = true;

        var body = new VerticalStackLayout { Spacing = 8 };

        // Kopf: Urteil in einem Satz
        var head = Row(
            (Glyph(DeliveryUi.StateIcon(d.Overall), 15, DeliveryUi.ColorKey(d.Overall)), Auto),
            (Text(d.Headline, 13, DeliveryUi.ColorKey(d.Overall), true), Star),
            (HStack(4, Glyph("branch", 11, "Muted"), Mono(d.Branch, 10.5, "Muted")), Auto),
            (Mono(_busy ? "prüft…" : d.CheckedAt.ToString("HH:mm:ss"), 10, "Muted"), Auto),
            (IconBtn("refresh", () => _ = RefreshAsync(), "Neu prüfen", 13), Auto),
            (IconBtn(_expanded ? "chevron-up" : "chevron-down", () => { _expanded = !_expanded; Render(); }, _expanded ? "Einklappen" : "Details", 13), Auto));
        head.ColumnSpacing = 8;
        body.Add(head);

        // Schritte als Kette
        var chain = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var s in d.Steps)
        {
            var chip = Box(HStack(5, Glyph(DeliveryUi.StepIcon(s.Key), 12, DeliveryUi.ColorKey(s.State)),
                    Mono(s.Label.ToUpperInvariant(), 9.5, "Muted"), Text(s.Detail, 11.5, s.State == StepState.None ? "Muted" : "Text")),
                C(s.State switch { StepState.Ok => "GreenSoft", StepState.Fail => "RedSoft", StepState.Running => "AmberSoft", StepState.Warn => "AccentSoft", _ => "Panel" }),
                new Thickness(8, 4));
            chip.Margin = new Thickness(0, 0, 6, 6);
            if (s.Url != null) { OnTap(chip, () => DeliveryUi.Open(s.Url)); ToolTipProperties.SetText(chip, "In " + (s.Key == "jira" ? "Jira" : "GitLab") + " öffnen"); }
            chain.Add(chip);
        }
        body.Add(chain);

        if (_expanded && d.Pipeline != null)
        {
            var p = d.Pipeline;
            var pipeHead = Row((Caption($"Pipeline #{p.Id} · {p.Status}"), Star),
                (Ghost("In GitLab", () => DeliveryUi.Open(p.WebUrl)), Auto));
            if (p.Status is "failed" or "canceled" && d.GitLabProject != null)
                pipeHead = Row((Caption($"Pipeline #{p.Id} · {p.Status}"), Star),
                    (Ghost("Erneut starten", () => _ = RetryAsync(d)), Auto),
                    (Ghost("In GitLab", () => DeliveryUi.Open(p.WebUrl)), Auto));
            body.Add(pipeHead);

            var jobs = new VerticalStackLayout { Spacing = 0 };
            foreach (var stage in d.Jobs.GroupBy(j => j.Stage))
            {
                jobs.Add(Mono(stage.Key.ToUpperInvariant(), 9.5, "Muted"));
                foreach (var j in stage)
                {
                    var jr = Row(
                        (Glyph(DeliveryUi.JobIcon(j.Status), 12, DeliveryUi.JobColor(j)), Auto),
                        (Text(j.Name, 12, "Text"), Star),
                        (Mono(j.Status + (j.AllowFailure && j.Status == "failed" ? " (erlaubt)" : ""), 10.5, DeliveryUi.JobColor(j)), Auto),
                        (Mono(j.Duration is { } sec ? Dur(TimeSpan.FromSeconds(sec)) : "", 10.5, "Muted"), Px(54)),
                        (Btn(_logJob == j.Id ? "Log ▲" : "Log", () => _ = ToggleLogAsync(d, j)), Auto));
                    jr.Padding = new Thickness(4, 2);
                    jobs.Add(Hover(jr));
                    if (_logJob == j.Id)
                    {
                        var log = new Label { Text = _log, FontFamily = MonoFont, FontSize = 11, TextColor = C("Text2"), LineBreakMode = LineBreakMode.CharacterWrap };
                        var scroll = new ScrollView { Content = log, MaximumHeightRequest = 280, HeightRequest = 280 };
                        jobs.Add(Box(scroll, C("Bg"), new Thickness(10, 8), C("LineSoft")));
                        Dispatcher.Dispatch(async () => { try { await scroll.ScrollToAsync(log, ScrollToPosition.End, false); } catch { } });
                    }
                }
            }
            body.Add(jobs);

            // Fehlgeschlagenen Job gleich öffnen
            if (_logJob == null && d.Jobs.FirstOrDefault(j => j.Status == "failed" && !j.AllowFailure) is { } first && d.GitLabProject != null)
                Dispatcher.Dispatch(() => _ = ToggleLogAsync(d, first));
        }

        Content = Ui.Card(body, new Thickness(12, 10), d.Overall == StepState.Fail ? "Red" : "LineSoft");
    }

    private async Task ToggleLogAsync(TicketDelivery d, GitLabJob j)
    {
        if (_mgr == null || d.GitLabProject == null) return;
        if (_logJob == j.Id) { _logJob = null; _log = ""; Render(); return; }
        _logJob = j.Id; _log = "Lade Log…"; Render();
        try { _log = await _mgr.Delivery.JobLogAsync(d.GitLabProject, j.Id); if (string.IsNullOrWhiteSpace(_log)) _log = "(kein Log)"; }
        catch (Exception e) { _log = "Log konnte nicht geladen werden: " + e.Message; }
        MainThread.BeginInvokeOnMainThread(Render);
    }

    private async Task RetryAsync(TicketDelivery d)
    {
        if (_mgr == null || d.GitLabProject == null || d.Pipeline == null) return;
        try { await _mgr.Delivery.RetryPipelineAsync(d.GitLabProject, d.Pipeline.Id); }
        catch (Exception e) { _log = "Neustart fehlgeschlagen: " + e.Message; }
        await Task.Delay(1500);
        await RefreshAsync();
    }
}
