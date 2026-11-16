using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;

namespace Copit.Controls;

/// <summary>
/// Eigene Dialoge im Copit-Stil statt der Windows-Standarddialoge:
/// Dropdown unter einem Element, Auswahl-Sheet, Eingabe, Bestätigung. Klick daneben bricht ab.
/// </summary>
public static class Overlay
{
    /// <summary>Ebene über dem ganzen Fenster (wird von der MainPage gesetzt).</summary>
    public static Grid? Host { get; set; }

    private static Color C(string key) => (Color)Application.Current!.Resources[key];
    private const string Mono = "Cascadia Mono";

    private static TaskCompletionSource<T> Open<T>(View content, bool dim, Func<T> cancelValue)
    {
        var tcs = new TaskCompletionSource<T>();
        var layer = new AbsoluteLayout { BackgroundColor = dim ? Color.FromArgb("#B0100C0A") : Color.FromArgb("#01000000"), Opacity = 0 };
        layer.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(layer, () => tcs.TrySetResult(cancelValue()))) });
        // Klicks im Inhalt nicht zum Hintergrund durchreichen
        content.GestureRecognizers.Add(new TapGestureRecognizer());
        layer.Add(content);
        layer.BindingContext = tcs;
        Host!.Children.Add(layer);
        Host.IsVisible = true;
        _ = layer.FadeTo(1, 110, Easing.CubicOut);
        tcs.Task.ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => Close(layer, null)));
        return tcs;
    }

    private static async void Close(AbsoluteLayout layer, Action? then)
    {
        then?.Invoke();
        if (Host == null || !Host.Children.Contains(layer)) return;
        await layer.FadeTo(0, 90);
        Host.Children.Remove(layer);
        if (Host.Children.Count == 0) Host.IsVisible = false;
    }

    /// <summary>Position eines Elements relativ zum Fenster.</summary>
    public static Point PositionOf(VisualElement v)
    {
        double x = 0, y = 0;
        Element? e = v;
        while (e is VisualElement ve && e is not Page)
        {
            x += ve.X + ve.TranslationX;
            y += ve.Y + ve.TranslationY;
            if (ve.Parent is ScrollView sv) { x -= sv.ScrollX; y -= sv.ScrollY; }
            e = ve.Parent;
        }
        return new Point(x, y);
    }

    private static View Row(string text, bool selected, string? hint, Action onTap)
    {
        var g = new Grid { ColumnDefinitions = [new(new GridLength(2)), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 10, Padding = new Thickness(0, 0, 12, 0), HeightRequest = hint == null ? 34 : 46, BackgroundColor = Colors.Transparent };
        g.Add(new BoxView { Color = selected ? C("Accent") : Colors.Transparent }, 0);
        var texts = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Padding = new Thickness(8, 0, 0, 0) };
        texts.Add(new Label { Text = text, FontSize = 13, TextColor = selected ? C("Text") : C("Text2"), LineBreakMode = LineBreakMode.TailTruncation });
        if (hint != null) texts.Add(new Label { Text = hint, FontSize = 11, TextColor = C("Muted"), LineBreakMode = LineBreakMode.TailTruncation });
        g.Add(texts, 1);
        if (selected) g.Add(new Icon { Name = "check", Size = 13, Color = C("Accent"), VerticalOptions = LayoutOptions.Center }, 2);
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => g.BackgroundColor = C("CardHover");
        hover.PointerExited += (_, _) => g.BackgroundColor = Colors.Transparent;
        g.GestureRecognizers.Add(hover);
        g.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(onTap) });
        return g;
    }

    private static Border Panel(View content, double width) => new()
    {
        Content = content, WidthRequest = width, BackgroundColor = C("Panel"), Stroke = C("Line"), StrokeThickness = 1,
        StrokeShape = new Rectangle(), Padding = new Thickness(0),
        Shadow = new Shadow { Brush = Colors.Black, Opacity = 0.45f, Radius = 18, Offset = new Point(0, 6) },
    };

    /// <summary>Auswahl. Mit anchor als Dropdown direkt unter dem Element, sonst als Sheet in der Mitte.</summary>
    public static Task<string?> Choose(string title, IReadOnlyList<string> options, VisualElement? anchor = null, string? selected = null, IReadOnlyList<string?>? hints = null)
    {
        if (Host == null) return Task.FromResult<string?>(null);
        TaskCompletionSource<string?>? tcs = null;
        var list = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(0, 4) };
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            list.Add(Row(o, o == selected, hints != null && i < hints.Count ? hints[i] : null, () => tcs!.TrySetResult(o)));
        }
        var scroll = new ScrollView { Content = list, MaximumHeightRequest = 380 };
        View content;
        if (anchor != null)
        {
            var pos = PositionOf(anchor);
            var width = Math.Max(anchor.Width, 220);
            var panel = Panel(scroll, width);
            var itemsH = Math.Min(380, options.Count * 34 + 8);
            var below = pos.Y + anchor.Height + 4;
            var y = below + itemsH > Host.Height - 8 ? Math.Max(8, pos.Y - itemsH - 4) : below;
            var x = Math.Min(pos.X, Math.Max(8, Host.Width - width - 8));
            AbsoluteLayout.SetLayoutBounds(panel, new Rect(x, y, width, AbsoluteLayout.AutoSize));
            content = panel;
        }
        else
        {
            var stack = new VerticalStackLayout { Spacing = 0 };
            stack.Add(new Label { Text = title.ToUpperInvariant(), FontFamily = Mono, FontSize = 10.5, CharacterSpacing = 1.3, TextColor = C("Muted"), Padding = new Thickness(14, 12, 14, 6), LineBreakMode = LineBreakMode.WordWrap });
            stack.Add(new BoxView { HeightRequest = 1, Color = C("LineSoft") });
            stack.Add(scroll);
            var panel = Panel(stack, 420);
            Center(panel);
            content = panel;
        }
        tcs = Open<string?>(content, anchor == null, () => null);
        return tcs.Task;
    }

    private static void Center(View v)
    {
        AbsoluteLayout.SetLayoutFlags(v, AbsoluteLayoutFlags.PositionProportional);
        AbsoluteLayout.SetLayoutBounds(v, new Rect(0.5, 0.42, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
    }

    private static Button Btn(string text, bool primary, Action a)
    {
        var b = new Button { Text = text, Style = (Style)Application.Current!.Resources[primary ? "Primary" : "Ghost"] };
        b.Clicked += (_, _) => a();
        return b;
    }

    private static View Frame(string title, string? message, View? body, View buttons)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(20, 18) };
        stack.Add(new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = C("Text"), LineBreakMode = LineBreakMode.WordWrap });
        if (!string.IsNullOrWhiteSpace(message)) stack.Add(new Label { Text = message, FontSize = 12.5, TextColor = C("Text2"), LineBreakMode = LineBreakMode.WordWrap });
        if (body != null) stack.Add(body);
        stack.Add(buttons);
        var panel = Panel(stack, 460);
        Center(panel);
        return panel;
    }

    private static View Buttons(params View[] items)
    {
        var row = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var i in items) row.Add(i);
        return row;
    }

    public static Task<bool> Confirm(string title, string message, string ok = "Ja", string cancel = "Abbrechen")
    {
        if (Host == null) return Task.FromResult(false);
        TaskCompletionSource<bool>? tcs = null;
        var view = Frame(title, message, null, Buttons(Btn(cancel, false, () => tcs!.TrySetResult(false)), Btn(ok, true, () => tcs!.TrySetResult(true))));
        tcs = Open(view, true, () => false);
        return tcs.Task;
    }

    public static Task Alert(string title, string message)
    {
        if (Host == null) return Task.CompletedTask;
        TaskCompletionSource<bool>? tcs = null;
        var view = Frame(title, message, null, Buttons(Btn("OK", true, () => tcs!.TrySetResult(true))));
        tcs = Open(view, true, () => true);
        return tcs.Task;
    }

    public static Task<string?> Prompt(string title, string message, string initial = "", string placeholder = "", bool password = false)
    {
        if (Host == null) return Task.FromResult<string?>(null);
        TaskCompletionSource<string?>? tcs = null;
        var entry = new Entry { Text = initial, Placeholder = placeholder, FontSize = 13, IsPassword = password };
        entry.Completed += (_, _) => tcs!.TrySetResult(entry.Text ?? "");
        var view = Frame(title, message, entry, Buttons(Btn("Abbrechen", false, () => tcs!.TrySetResult(null)), Btn("OK", true, () => tcs!.TrySetResult(entry.Text ?? ""))));
        tcs = Open<string?>(view, true, () => null);
        MainThread.BeginInvokeOnMainThread(async () => { await Task.Delay(60); entry.Focus(); entry.CursorPosition = entry.Text?.Length ?? 0; });
        return tcs.Task;
    }
}

/// <summary>Dropdown im Copit-Stil (Ersatz für den Windows-Picker).</summary>
public class Dropdown : ContentView
{
    private readonly Label _label;
    private readonly IList<string> _items;
    private int _index;
    public event Action<int>? SelectedIndexChanged;

    public Dropdown(IList<string> items, int index)
    {
        _items = items; _index = index;
        _label = new Label { FontSize = 12.5, TextColor = (Color)Application.Current!.Resources["Text"], VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8, Padding = new Thickness(10, 0), HeightRequest = 34 };
        g.Add(_label, 0);
        g.Add(new Icon { Name = "chevron-down", Size = 12, Color = (Color)Application.Current!.Resources["Muted"], VerticalOptions = LayoutOptions.Center }, 1);
        var border = new Border
        {
            Content = g, StrokeShape = new Rectangle(), StrokeThickness = 1, Stroke = (Color)Application.Current!.Resources["Line"],
            BackgroundColor = (Color)Application.Current!.Resources["Bg2"],
        };
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => border.Stroke = (Color)Application.Current!.Resources["Muted"];
        hover.PointerExited += (_, _) => border.Stroke = (Color)Application.Current!.Resources["Line"];
        border.GestureRecognizers.Add(hover);
        border.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () =>
        {
            var pick = await Overlay.Choose("", _items.ToList(), this, SelectedItem);
            if (pick == null) return;
            var i = _items.IndexOf(pick);
            if (i < 0 || i == _index) return;
            SelectedIndex = i;
            SelectedIndexChanged?.Invoke(i);
        }) });
        Content = border;
        Update();
    }

    public int SelectedIndex { get => _index; set { _index = value; Update(); } }
    public string? SelectedItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    private void Update() => _label.Text = SelectedItem ?? "–";
}
