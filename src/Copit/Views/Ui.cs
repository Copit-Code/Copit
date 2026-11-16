using Copit.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace Copit.Views;

/// <summary>Kleiner Baukasten für code-gebaute Ansichten im Copit-Stil (hartkantig, Mono-Labels, ein Akzent).</summary>
public static class Ui
{
    public const string MonoFont = "Cascadia Mono";

    public static Color C(string key) => (Color)Application.Current!.Resources[key];
    public static Color Hex(string hex) => Color.FromArgb(hex);

    public static Label Text(string text, double size = 13, string color = "Text", bool bold = false, LineBreakMode wrap = LineBreakMode.TailTruncation) => new()
    {
        Text = text, FontSize = size, TextColor = C(color), FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        LineBreakMode = wrap, VerticalOptions = LayoutOptions.Center,
    };

    public static Label Wrap(string text, double size = 12.5, string color = "Text2") => Text(text, size, color, false, LineBreakMode.WordWrap);

    public static Label Mono(string text, double size = 11, string color = "Text2", bool bold = false) => new()
    {
        Text = text, FontSize = size, FontFamily = MonoFont, TextColor = C(color), VerticalOptions = LayoutOptions.Center,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, LineBreakMode = LineBreakMode.TailTruncation,
    };

    public static Label Caption(string text, string color = "Muted") => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10, FontFamily = MonoFont, CharacterSpacing = 1.3, TextColor = C(color), VerticalOptions = LayoutOptions.Center,
    };

    public static Label H1(string text) => new() { Text = text, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = C("Text") };
    public static Label H2(string text) => new() { Text = text, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = C("Text") };

    public static Border Card(View content, Thickness? padding = null, string stroke = "Line", string bg = "Card") => new()
    {
        Content = content, Padding = padding ?? new Thickness(16, 14), BackgroundColor = C(bg), Stroke = C(stroke), StrokeThickness = 1,
        StrokeShape = new Rectangle(),
    };

    public static Border Box(View content, Color bg, Thickness? padding = null, Color? stroke = null) => new()
    {
        Content = content, Padding = padding ?? new Thickness(8, 4), BackgroundColor = bg, Stroke = stroke ?? Colors.Transparent,
        StrokeThickness = stroke == null ? 0 : 1, StrokeShape = new Rectangle(),
    };

    /// <summary>Kleines Abzeichen, z. B. GLOBAL / PROJEKT.</summary>
    public static Border Badge(string text, Color color) => BadgeBox(text, color);
    private static Border BadgeBox(string text, Color color) { var b = BadgeRaw(text, color); b.VerticalOptions = LayoutOptions.Center; b.HorizontalOptions = LayoutOptions.Start; return b; }
    private static Border BadgeRaw(string text, Color color) =>
        Box(new Label { Text = text.ToUpperInvariant(), FontSize = 9.5, FontFamily = MonoFont, CharacterSpacing = 1, TextColor = color }, color.WithAlpha(0.12f), new Thickness(6, 2), color.WithAlpha(0.45f));

    public static Button Btn(string text, Action onClick, string kind = "Btn", string? tooltip = null)
    {
        var b = new Button { Text = text, Style = (Style)Application.Current!.Resources[kind] };
        b.Clicked += (_, _) => onClick();
        if (tooltip != null) ToolTipProperties.SetText(b, tooltip);
        return b;
    }

    public static Button Primary(string text, Action onClick) => Btn(text, onClick, "Primary");
    public static Button Ghost(string text, Action onClick) => Btn(text, onClick, "Ghost");
    public static Button Chip(string text, Action onClick) => Btn(text, onClick, "Chip");

    public static IconButton IconBtn(string icon, Action onClick, string? tooltip = null, double size = 14, Color? color = null)
    {
        var b = new IconButton { Icon = icon, IconSize = size, WidthRequest = 28, HeightRequest = 28, Command = new Command(onClick) };
        if (color != null) b.Color = color;
        if (tooltip != null) ToolTipProperties.SetText(b, tooltip);
        return b;
    }

    public static Icon Glyph(string name, double size = 14, string color = "Text2") => new() { Name = name, Size = size, Color = C(color), VerticalOptions = LayoutOptions.Center };

    /// <summary>Segment-Auswahl (z. B. Tag | Woche).</summary>
    public static View Seg(IEnumerable<(string Key, string Label)> items, string selected, Action<string> onSelect, Func<string, Color>? activeColor = null)
    {
        var row = new HorizontalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center };
        foreach (var (key, label) in items)
        {
            var active = key == selected;
            var col = activeColor?.Invoke(key) ?? C("Accent");
            var lbl = new Label
            {
                Text = label.ToUpperInvariant(), FontSize = 10.5, FontFamily = MonoFont, CharacterSpacing = 1,
                TextColor = active ? C("Text") : C("Muted"), VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center,
            };
            var cell = new Border
            {
                Content = lbl, Padding = new Thickness(11, 6), StrokeShape = new Rectangle(), StrokeThickness = 1,
                Stroke = active ? col : C("Line"), BackgroundColor = active ? col.WithAlpha(0.16f) : Colors.Transparent,
                Margin = new Thickness(0, 0, -1, 0),
            };
            var k = key;
            cell.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => onSelect(k)) });
            row.Add(cell);
        }
        return row;
    }

    /// <summary>Fortschrittsbalken (hartkantig).</summary>
    public static Grid Bar(double fraction, Color color, double height = 4)
    {
        var g = new Grid { HeightRequest = height, BackgroundColor = C("LineSoft"), ColumnDefinitions = [new(new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star)), new(new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star))] };
        g.Add(new BoxView { Color = color }, 0);
        return g;
    }

    public static Entry Input(string text, string placeholder, Action<string> onChange, double? width = null)
    {
        var e = new Entry { Text = text, Placeholder = placeholder, FontSize = 12.5 };
        if (width != null) e.WidthRequest = width.Value;
        e.TextChanged += (_, a) => onChange(a.NewTextValue ?? "");
        return e;
    }

    public static Editor Area(string text, string placeholder, Action<string> onChange, double minHeight = 70)
    {
        var e = new Editor { Text = text, Placeholder = placeholder, FontSize = 12.5, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = minHeight };
        e.TextChanged += (_, a) => onChange(a.NewTextValue ?? "");
        return e;
    }

    /// <summary>Dropdown im Copit-Stil (eigenes Menü statt Windows-Picker).</summary>
    public static Dropdown Pick(IList<string> items, int index, Action<int> onChange, double? width = null)
    {
        var d = new Dropdown(items, index);
        if (width != null) d.WidthRequest = width.Value;
        d.SelectedIndexChanged += onChange;
        return d;
    }

    public static Switch Toggle(bool on, Action<bool> onChange)
    {
        var s = new Switch { IsToggled = on, OnColor = C("Accent"), VerticalOptions = LayoutOptions.Center };
        s.Toggled += (_, a) => onChange(a.Value);
        return s;
    }

    public static Grid Row(params (View View, GridLength Width)[] cells)
    {
        var g = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < cells.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(cells[i].Width));
            g.Add(cells[i].View, i);
        }
        return g;
    }

    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => GridLength.Star;
    public static GridLength Px(double v) => new(v);

    public static VerticalStackLayout Stack(double spacing, params View[] children)
    {
        var s = new VerticalStackLayout { Spacing = spacing };
        foreach (var c in children) s.Add(c);
        return s;
    }

    public static HorizontalStackLayout HStack(double spacing, params View[] children)
    {
        var s = new HorizontalStackLayout { Spacing = spacing };
        foreach (var c in children) s.Add(c);
        return s;
    }

    public static BoxView Square(Color color, double size = 8) => new() { Color = color, WidthRequest = size, HeightRequest = size, VerticalOptions = LayoutOptions.Center };

    public static BoxView Line() => new() { HeightRequest = 1, Color = C("LineSoft") };

    /// <summary>Kennzahl-Kachel (Label + großer Wert + Zusatz).</summary>
    public static Border Stat(string caption, string value, string? sub = null, string valueColor = "Text") =>
        Card(Stack(4, Caption(caption), new Label { Text = value, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = C(valueColor), FontFamily = MonoFont },
            Text(sub ?? "", 11, "Muted")), new Thickness(16, 12));

    /// <summary>Hover-Hintergrund für klickbare Zeilen.</summary>
    public static T Hover<T>(T v, Color? normal = null) where T : View
    {
        var n = normal ?? Colors.Transparent;
        v.BackgroundColor = n;
        var g = new PointerGestureRecognizer();
        g.PointerEntered += (_, _) => v.BackgroundColor = C("CardHover");
        g.PointerExited += (_, _) => v.BackgroundColor = n;
        v.GestureRecognizers.Add(g);
        return v;
    }

    public static T OnTap<T>(T v, Action a) where T : View
    {
        v.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(a) });
        return v;
    }

    public static string Money(double usd) => usd >= 100 ? $"${usd:0}" : usd >= 10 ? $"${usd:0.0}" : $"${usd:0.00}";
    public static string Dur(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0}s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m" : $"{(int)t.TotalHours}h {t.Minutes:00}m";
    public static string Tok(long n) => n >= 1_000_000_000 ? $"{n / 1e9:0.##}B" : n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    /// <summary>Feste Farbpalette für Projekte (warm, gedämpft).</summary>
    public static readonly string[] Palette = ["#E8743B", "#4FB3D9", "#7BC47F", "#D9A441", "#D96BA0", "#8B7CF6", "#5FBFAE", "#C98A5B", "#9AA7B5", "#E0604F"];
    public static Color ColorFor(string key) => Hex(Palette[(int)((uint)StableHash(key) % (uint)Palette.Length)]);
    private static int StableHash(string s) { unchecked { int h = 23; foreach (var c in s) h = h * 31 + c; return h; } }
}
