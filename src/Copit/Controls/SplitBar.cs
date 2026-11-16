namespace Copit.Controls;

/// <summary>
/// Segmentierter Balken: Abschnitte nebeneinander im Verhältnis ihrer Werte (z. B. Stunden mit Account grün,
/// ohne Account orange, unbekannt grau), danach der freie Rest bis <see cref="Scale"/> als Spur.
/// </summary>
public class SplitBar : ContentView
{
    public static readonly BindableProperty FirstProperty = Create(nameof(First));
    public static readonly BindableProperty SecondProperty = Create(nameof(Second));
    public static readonly BindableProperty ThirdProperty = Create(nameof(Third));
    public static readonly BindableProperty ScaleProperty = Create(nameof(Scale));

    private static BindableProperty Create(string name) =>
        BindableProperty.Create(name, typeof(double), typeof(SplitBar), 0.0, propertyChanged: (b, _, _) => ((SplitBar)b).Rebuild());

    public double First { get => (double)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    public double Second { get => (double)GetValue(SecondProperty); set => SetValue(SecondProperty, value); }
    public double Third { get => (double)GetValue(ThirdProperty); set => SetValue(ThirdProperty, value); }
    /// <summary>Gesamtlänge des Balkens (z. B. Soll-Stunden). Ist die Summe größer, füllt sie den Balken.</summary>
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    private Color _first = Colors.Green, _second = Colors.Orange, _third = Colors.Gray, _track = Colors.Transparent;
    public Color FirstColor { get => _first; set { _first = value; Rebuild(); } }
    public Color SecondColor { get => _second; set { _second = value; Rebuild(); } }
    public Color ThirdColor { get => _third; set { _third = value; Rebuild(); } }
    public Color TrackColor { get => _track; set { _track = value; Rebuild(); } }

    private readonly Grid _grid = new() { ColumnSpacing = 0 };

    public SplitBar()
    {
        HeightRequest = 4;
        Content = _grid;
        Rebuild();
    }


    private void Rebuild()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.BackgroundColor = TrackColor;
        var parts = new[] { (First, FirstColor), (Second, SecondColor), (Third, ThirdColor) };
        var sum = parts.Sum(p => Math.Max(0, p.Item1));
        var rest = Math.Max(0, Scale - sum);
        var col = 0;
        foreach (var (v, c) in parts)
        {
            if (v <= 0) continue;
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(v, GridUnitType.Star)));
            _grid.Add(new BoxView { Color = c }, col++);
        }
        if (rest > 0 || col == 0) _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(rest, 0.0001), GridUnitType.Star)));
    }
}
