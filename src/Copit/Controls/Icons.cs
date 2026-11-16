using System.Windows.Input;
using Microsoft.Maui.Graphics;

namespace Copit.Controls;

/// <summary>
/// Linien-Icons als SVG-Pfaddaten (24×24 ViewBox, Stroke-basiert, harte Kanten).
/// Werden vektoriell gezeichnet und lassen sich frei einfärben.
/// </summary>
public static class IconSet
{
    public record Def(string Path, (float X, float Y, float R)[] Circles);

    private static Def D(string path, params (float, float, float)[] circles) => new(path, circles);

    public static readonly Dictionary<string, Def> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = D("M3 10 L12 3 L21 10 V21 H3 Z M9 21 V14 H15 V21"),
        ["calendar"] = D("M3 5 H21 V21 H3 Z M3 10 H21 M8 3 V7 M16 3 V7"),
        ["folder"] = D("M3 5 H9 L11 7 H21 V19 H3 Z"),
        ["chat"] = D("M4 4 H20 V16 H10 L5 20 V16 H4 Z"),
        ["grid"] = D("M4 4 H10 V10 H4 Z M14 4 H20 V10 H14 Z M4 14 H10 V20 H4 Z M14 14 H20 V20 H14 Z"),
        ["sliders"] = D("M3 6 H21 M3 12 H21 M3 18 H21 M8 4 V8 M16 10 V14 M10 16 V20"),
        ["plus"] = D("M12 5 V19 M5 12 H19"),
        ["minus"] = D("M5 12 H19"),
        ["x"] = D("M6 6 L18 18 M18 6 L6 18"),
        ["check"] = D("M5 12.5 L10 17.5 L19 7"),
        ["refresh"] = D("M20 12 C20 16.4 16.4 20 12 20 C7.6 20 4 16.4 4 12 C4 7.6 7.6 4 12 4 C14.6 4 16.9 5.2 18.4 7.2 M19 3 V7.5 H14.5"),
        ["chevron-left"] = D("M15 5 L8 12 L15 19"),
        ["chevron-right"] = D("M9 5 L16 12 L9 19"),
        ["chevron-down"] = D("M5 9 L12 16 L19 9"),
        ["chevron-up"] = D("M5 15 L12 8 L19 15"),
        ["branch"] = D("M6 3 V15 M18 9 C18 14 14 18 9 18", (18, 6, 3), (6, 18, 3)),
        ["commit"] = D("M12 3 V9 M12 15 V21", (12, 12, 3)),
        ["merge"] = D("M6 9 V21 M18 15 V10 C18 8 17 7 15 7 H11 M13.5 4.5 L11 7 L13.5 9.5", (6, 6, 3), (18, 18, 3)),
        ["play"] = D("M7 4 L19 12 L7 20 Z"),
        ["stop"] = D("M6 6 H18 V18 H6 Z"),
        ["pause"] = D("M8 5 V19 M16 5 V19"),
        ["send"] = D("M12 20 V5 M5 12 L12 5 L19 12"),
        ["pull"] = D("M12 4 V17 M6 11 L12 17 L18 11 M4 21 H20"),
        ["push"] = D("M12 20 V7 M6 13 L12 7 L18 13 M4 3 H20"),
        ["terminal"] = D("M4 17 L10 11 L4 5 M12 19 H20"),
        ["code"] = D("M16 18 L22 12 L16 6 M8 6 L2 12 L8 18"),
        ["external"] = D("M14 4 H20 V10 M20 4 L11 13 M18 14 V20 H4 V6 H10"),
        ["pencil"] = D("M4 20 H8 L19 9 L15 5 L4 16 Z M13 7 L17 11"),
        ["trash"] = D("M4 7 H20 M10 11 V17 M14 11 V17 M6 7 L7 20 H17 L18 7 M9 7 V4 H15 V7"),
        ["pin"] = D("M9 4 H15 M10 4 V10 L7 14 H17 L14 10 V4 M12 14 V21"),
        ["file"] = D("M6 3 H14 L19 8 V21 H6 Z M14 3 V8 H19"),
        ["diff"] = D("M6 3 H14 L19 8 V21 H6 Z M14 3 V8 H19 M9.5 12 H15.5 M12.5 9 V15 M9.5 18 H15.5"),
        ["search"] = D("M16 16 L21 21", (11, 11, 7)),
        ["alert"] = D("M12 3 L22 20 H2 Z M12 9.5 V14 M12 16.5 V17.5"),
        ["info"] = D("M12 11 V16 M12 8 V8.6", (12, 12, 9)),
        ["question"] = D("M9.5 9.5 C9.5 8 10.6 7 12 7 C13.4 7 14.5 8 14.5 9.3 C14.5 11 12 11.2 12 13.2 M12 16.4 V17", (12, 12, 9)),
        ["circle"] = D("", (12, 12, 9)),
        ["check-circle"] = D("M8 12.2 L11 15 L16.2 9.4", (12, 12, 9)),
        ["x-circle"] = D("M9 9 L15 15 M15 9 L9 15", (12, 12, 9)),
        ["clock"] = D("M12 7 V12 L15.5 14", (12, 12, 9)),
        ["loader"] = D("M12 3 V6 M12 18 V21 M3 12 H6 M18 12 H21 M5.6 5.6 L7.8 7.8 M16.2 16.2 L18.4 18.4 M5.6 18.4 L7.8 16.2 M16.2 7.8 L18.4 5.6"),
        ["bot"] = D("M5 8 H19 V19 H5 Z M12 4 V8 M9.5 12.5 V14.5 M14.5 12.5 V14.5 M2 13 H5 M19 13 H22"),
        ["user"] = D("M4 21 C4 16.5 7.5 15 12 15 C16.5 15 20 16.5 20 21", (12, 8, 4)),
        ["list"] = D("M8 6 H21 M8 12 H21 M8 18 H21 M3 6 H4.5 M3 12 H4.5 M3 18 H4.5"),
        ["panel-left"] = D("M3 4 H21 V20 H3 Z M9 4 V20"),
        ["panel-right"] = D("M3 4 H21 V20 H3 Z M15 4 V20"),
        ["book"] = D("M3 4 H9 C10.7 4 12 5.3 12 7 V20 C12 18.9 11.1 18 10 18 H3 Z M21 4 H15 C13.3 4 12 5.3 12 7 V20 C12 18.9 12.9 18 14 18 H21 Z"),
        ["copy"] = D("M8 8 H20 V20 H8 Z M16 8 V4 H4 V16 H8"),
        ["note"] = D("M4 4 H20 V14 L14 20 H4 Z M14 20 V14 H20"),
        ["zap"] = D("M13 2 L4 14 H12 L11 22 L20 10 H12 Z"),
        ["link"] = D("M10 14 L14 10 M11 6.5 L13 4.5 C15 2.5 18.5 2.5 19.8 4.2 C21.5 5.5 21.5 9 19.5 11 L17.5 13 M12.5 17.5 L10.5 19.5 C8.5 21.5 5 21.5 3.7 19.8 C2 18.5 2 15 4 13 L6 11"),
        ["lock"] = D("M5 11 H19 V21 H5 Z M8 11 V7 C8 4.8 9.8 3 12 3 C14.2 3 16 4.8 16 7 V11"),
        ["ticket"] = D("M3 6 H21 V10 C19.9 10 19 10.9 19 12 C19 13.1 19.9 14 21 14 V18 H3 V14 C4.1 14 5 13.1 5 12 C5 10.9 4.1 10 3 10 Z M14 6 V18"),
        ["flow"] = D("M9 6 H15 M6 9 V15 M9 18 H15", (6, 6, 3), (18, 6, 3), (6, 18, 3)),
        ["skip"] = D("M5 5 L19 19", (12, 12, 9)),
        ["dot"] = D("", (12, 12, 3)),
        ["square"] = D("M7 7 H17 V17 H7 Z"),
        ["activity"] = D("M3 12 H7 L10 4 L14 20 L17 12 H21"),
        ["sun"] = D("M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22 M4.9 4.9 L6.3 6.3 M17.7 17.7 L19.1 19.1 M4.9 19.1 L6.3 17.7 M17.7 6.3 L19.1 4.9", (12, 12, 4)),
        ["package"] = D("M3 7 L12 3 L21 7 V17 L12 21 L3 17 Z M3 7 L12 11 L21 7 M12 11 V21"),
        ["review"] = D("M4 4 H20 V16 H10 L5 20 V16 H4 Z M8 10 L10.5 12.5 L16 7.5"),
    };

    /// <summary>Glyph-Namen für Pipeline-/Job-Status.</summary>
    public static string ForStatus(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "success" or "passed" => "check-circle",
        "failed" => "x-circle",
        "running" => "loader",
        "pending" or "created" or "waiting_for_resource" or "preparing" or "scheduled" => "clock",
        "canceled" => "stop",
        "manual" => "play",
        "skipped" => "skip",
        _ => "circle"
    };

    public static string ForTool(string tool) => tool switch
    {
        "Read" or "NotebookRead" => "file",
        "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => "pencil",
        "Bash" or "BashOutput" or "KillShell" => "terminal",
        "Grep" or "Glob" or "LS" => "search",
        "WebFetch" or "WebSearch" => "link",
        "TodoWrite" => "list",
        "Task" => "flow",
        _ => "zap"
    };
}

/// <summary>Zeichnet ein Icon aus <see cref="IconSet"/>.</summary>
public class Icon : GraphicsView, IDrawable
{
    public static readonly BindableProperty NameProperty = BindableProperty.Create(nameof(Name), typeof(string), typeof(Icon), "circle",
        propertyChanged: (b, _, _) => ((Icon)b).Rebuild());
    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(Icon), Color.FromArgb("#B5AA9E"),
        propertyChanged: (b, _, _) => ((Icon)b).Invalidate());
    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(Icon), 16.0,
        propertyChanged: (b, _, n) => ((Icon)b).ApplySize((double)n));
    public static readonly BindableProperty StrokeProperty = BindableProperty.Create(nameof(Stroke), typeof(float), typeof(Icon), 1.6f,
        propertyChanged: (b, _, _) => ((Icon)b).Invalidate());

    public string Name { get => (string)GetValue(NameProperty); set => SetValue(NameProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public float Stroke { get => (float)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private PathF? _path;
    private IconSet.Def? _def;

    public Icon()
    {
        Drawable = this;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        VerticalOptions = LayoutOptions.Center;
        HorizontalOptions = LayoutOptions.Center;
        ApplySize(16);
        Rebuild();
    }

    private void ApplySize(double s) { WidthRequest = s; HeightRequest = s; }

    private void Rebuild()
    {
        _def = IconSet.All.GetValueOrDefault(Name ?? "") ?? IconSet.All["circle"];
        _path = string.IsNullOrEmpty(_def.Path) ? null : PathBuilder.Build(_def.Path);
        Invalidate();
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (_def == null) return;
        var s = Math.Min(rect.Width, rect.Height) / 24f;
        canvas.SaveState();
        canvas.Translate(rect.X + (rect.Width - 24 * s) / 2, rect.Y + (rect.Height - 24 * s) / 2);
        canvas.Scale(s, s);
        canvas.StrokeColor = Color;
        canvas.StrokeSize = Stroke / s;
        canvas.StrokeLineCap = LineCap.Square;
        canvas.StrokeLineJoin = LineJoin.Miter;
        canvas.Antialias = true;
        if (_path != null) canvas.DrawPath(_path);
        foreach (var (x, y, r) in _def.Circles)
        {
            if (Name == "dot") { canvas.FillColor = Color; canvas.FillRectangle(x - r, y - r, r * 2, r * 2); }
            else canvas.DrawEllipse(x - r, y - r, r * 2, r * 2);
        }
        canvas.RestoreState();
    }
}

/// <summary>Quadratischer Icon-Button mit Hover-Fläche und Tooltip.</summary>
public class IconButton : ContentView
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(IconButton), "circle",
        propertyChanged: (b, _, n) => ((IconButton)b)._icon.Name = (string)n);
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(IconButton));
    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(IconButton));
    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(IconButton), null,
        propertyChanged: (b, _, n) => { if (n is Color c) ((IconButton)b)._icon.Color = c; });
    public static readonly BindableProperty IconSizeProperty = BindableProperty.Create(nameof(IconSize), typeof(double), typeof(IconButton), 15.0,
        propertyChanged: (b, _, n) => ((IconButton)b)._icon.Size = (double)n);

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public Color? Color { get => (Color?)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    private readonly Icon _icon = new() { Size = 15 };

    public IconButton()
    {
        WidthRequest = 30; HeightRequest = 30;
        VerticalOptions = LayoutOptions.Center;
        BackgroundColor = Colors.Transparent;
        _icon.Color = Res("Text2");
        Content = _icon;
        GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() => { if (Command?.CanExecute(CommandParameter) == true) Command.Execute(CommandParameter); })
        });
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => { BackgroundColor = Res("CardHover"); _icon.Color = Res("Text"); };
        hover.PointerExited += (_, _) => { BackgroundColor = Colors.Transparent; _icon.Color = Color ?? Res("Text2"); };
        GestureRecognizers.Add(hover);
    }

    private static Color Res(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : Colors.Gray;
}
