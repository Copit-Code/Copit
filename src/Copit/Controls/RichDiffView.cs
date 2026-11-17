using Copit.Core.Services;

namespace Copit.Controls;

/// <summary>
/// Diff einer Datei: Hunk-Köpfe mit Funktions-/Klassenkontext, Zeilennummern alt/neu, farbige Zeilen über die volle Breite
/// und hervorgehobene geänderte Wortbereiche bei ersetzten Zeilen.
/// </summary>
public class RichDiffView : ContentView
{
    private const string Font = "Cascadia Mono";
    private const double Size = 12;
    private const int MaxLines = 3000;
    private readonly VerticalStackLayout _stack = new() { Spacing = 10 };

    /// <summary>Klick auf eine Zeilennummer (neue Zeile) – z. B. Datei im Editor an dieser Stelle öffnen.</summary>
    public event Action<int>? LineTapped;

    public RichDiffView() => Content = _stack;

    private static Color R(string key) => (Color)Application.Current!.Resources[key];

    public void Show(FileDiff? file)
    {
        _stack.Children.Clear();
        if (file == null) return;
        if (file.Binary) { _stack.Add(Note("Binärdatei – keine Textänderungen darstellbar.")); return; }
        if (file.Hunks.Count == 0) { _stack.Add(Note(file.Status == "R" ? $"Nur umbenannt: {file.OldPath} → {file.Path}" : "Keine Textänderungen.")); return; }
        var shown = 0;
        foreach (var h in file.Hunks)
        {
            if (shown > MaxLines) { _stack.Add(Note("… weitere Änderungen ausgeblendet (sehr große Datei).")); break; }
            _stack.Add(Hunk(h));
            shown += h.Lines.Count;
        }
    }

    private static Label Note(string t) => new() { Text = t, FontSize = 12, TextColor = R("Muted"), Margin = new Thickness(4, 8) };

    private View Hunk(DiffHunk h)
    {
        Color green = R("Green"), red = R("Red"), muted = R("Muted"), text = R("Text2");
        Color addBg = Color.FromArgb("#1D271D"), delBg = Color.FromArgb("#351C18");
        Color addStrong = Color.FromArgb("#2F4A2F"), delStrong = Color.FromArgb("#5A2A22");
        var lines = h.Lines;
        var width = Math.Min(240, Math.Max(60, lines.Count == 0 ? 0 : lines.Max(l => Expand(l.Text).Length) + 2));

        var oldNos = new FormattedString(); var newNos = new FormattedString(); var code = new FormattedString(); var signs = new FormattedString();
        // Paare aus entfernten/hinzugefügten Zeilen für Wort-Hervorhebung
        var pairs = Pairs(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var nl = i < lines.Count - 1 ? "\n" : "";
            var t = Expand(l.Text);
            Color fg = l.Kind switch { DiffLineKind.Added => green, DiffLineKind.Removed => red, DiffLineKind.Note => muted, _ => text };
            Color? bg = l.Kind switch { DiffLineKind.Added => addBg, DiffLineKind.Removed => delBg, _ => null };
            oldNos.Spans.Add(new Span { Text = (l.OldNo?.ToString() ?? "") + nl, TextColor = muted });
            newNos.Spans.Add(new Span { Text = (l.NewNo?.ToString() ?? "") + nl, TextColor = muted });
            signs.Spans.Add(Bg(new Span { Text = (l.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "−", _ => " " }) + nl, TextColor = fg }, bg));
            if (l.Kind == DiffLineKind.Note) { code.Spans.Add(new Span { Text = t + nl, TextColor = muted, FontAttributes = FontAttributes.Italic }); continue; }
            var pad = new string(' ', Math.Max(0, width - t.Length));
            if (pairs.TryGetValue(i, out var other) && bg != null)
            {
                var (pre, mid, suf) = Split(t, Expand(lines[other].Text));
                code.Spans.Add(Bg(new Span { Text = pre, TextColor = fg }, bg));
                code.Spans.Add(Bg(new Span { Text = mid, TextColor = fg, FontAttributes = FontAttributes.Bold }, l.Kind == DiffLineKind.Added ? addStrong : delStrong));
                code.Spans.Add(Bg(new Span { Text = suf + pad + nl, TextColor = fg }, bg));
            }
            else code.Spans.Add(Bg(new Span { Text = t + pad + nl, TextColor = fg }, bg));
        }

        Label L(FormattedString fs, TextAlignment align = TextAlignment.Start) => new() { FormattedText = fs, FontFamily = Font, FontSize = Size, LineBreakMode = LineBreakMode.NoWrap, HorizontalTextAlignment = align };
        var oldL = L(oldNos, TextAlignment.End); var newL = L(newNos, TextAlignment.End);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { var first = lines.FirstOrDefault(x => x.NewNo != null)?.NewNo; if (first != null) LineTapped?.Invoke(first.Value); };
        newL.GestureRecognizers.Add(tap);

        var body = new Grid
        {
            ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            ColumnSpacing = 0, BackgroundColor = R("Bg2"),
        };
        body.Add(new ContentView { Content = oldL, Padding = new Thickness(8, 4, 6, 4), MinimumWidthRequest = 44, BackgroundColor = R("Panel") }, 0);
        body.Add(new ContentView { Content = newL, Padding = new Thickness(6, 4, 8, 4), MinimumWidthRequest = 44, BackgroundColor = R("Panel") }, 1);
        body.Add(new ContentView { Content = L(signs), Padding = new Thickness(6, 4, 2, 4) }, 2);
        body.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = new ContentView { Content = L(code), Padding = new Thickness(0, 4, 12, 4) } }, 3);

        var head = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10, Padding = new Thickness(10, 5), BackgroundColor = R("AccentSoft") };
        head.Add(new Label { Text = h.Header.Split("@@").ElementAtOrDefault(1)?.Trim() ?? h.Header, FontFamily = Font, FontSize = 11, TextColor = R("Cyan"), VerticalOptions = LayoutOptions.Center });
        head.Add(new Label { Text = h.Context, FontFamily = Font, FontSize = 11, TextColor = R("Text2"), LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center }, 1);
        return new Border { StrokeThickness = 1, Stroke = R("Line"), Padding = 0, Content = new VerticalStackLayout { Spacing = 0, Children = { head, body } } };
    }

    private static Span Bg(Span s, Color? bg) { if (bg != null) s.BackgroundColor = bg; return s; }
    private static string Expand(string s) => s.Replace("\t", "    ");

    /// <summary>Ordnet Blöcke "n entfernt, gefolgt von n hinzugefügt" zeilenweise einander zu.</summary>
    private static Dictionary<int, int> Pairs(List<DiffLine> lines)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].Kind != DiffLineKind.Removed) { i++; continue; }
            var rs = i; while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
            var a = i; while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;
            var n = Math.Min(a - rs, i - a);
            for (var k = 0; k < n; k++) { map[rs + k] = a + k; map[a + k] = rs + k; }
        }
        return map;
    }

    /// <summary>Gemeinsamer Anfang/Ende zweier Zeilen → (Anfang, geänderter Teil, Ende) von <paramref name="a"/>.</summary>
    public static (string Pre, string Mid, string Suf) Split(string a, string b)
    {
        var p = 0; while (p < a.Length && p < b.Length && a[p] == b[p]) p++;
        var s = 0; while (s < a.Length - p && s < b.Length - p && a[a.Length - 1 - s] == b[b.Length - 1 - s]) s++;
        // auf Wortgrenzen erweitern, damit nicht mitten im Bezeichner markiert wird
        while (p > 0 && char.IsLetterOrDigit(a[p - 1])) p--;
        while (s > 0 && a.Length - s < a.Length && char.IsLetterOrDigit(a[a.Length - s])) s--;
        return (a[..p], a[p..(a.Length - s)], a[(a.Length - s)..]);
    }
}

/// <summary>Ganzer Unified-Diff (eine oder mehrere Dateien) in der Rich-Darstellung – bindbar über <see cref="Diff"/>.</summary>
public class RichDiffList : ContentView
{
    public static readonly BindableProperty DiffProperty = BindableProperty.Create(nameof(Diff), typeof(string), typeof(RichDiffList), "",
        propertyChanged: (b, _, n) => ((RichDiffList)b).Render((string?)n));

    public string Diff { get => (string)GetValue(DiffProperty); set => SetValue(DiffProperty, value); }

    private readonly VerticalStackLayout _stack = new() { Spacing = 14, Padding = new Thickness(16, 12) };

    public RichDiffList() => Content = new ScrollView { Content = _stack };

    private static Color R(string key) => (Color)Application.Current!.Resources[key];

    private void Render(string? diff)
    {
        _stack.Children.Clear();
        var files = DiffParser.Parse(diff);
        if (files.Count == 0 || files.All(f => f.Hunks.Count == 0 && !f.Binary && f.Status == "M"))
        {
            // Kein Diff (z. B. „Lade Diff…“ oder Fehlermeldung) → als Text zeigen
            _stack.Add(new Label { Text = string.IsNullOrWhiteSpace(diff) ? "Keine Änderungen." : diff, FontSize = 12.5, TextColor = R("Text2"), LineBreakMode = LineBreakMode.WordWrap });
            return;
        }
        foreach (var f in files)
        {
            var head = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 10, Padding = new Thickness(10, 6), BackgroundColor = R("Panel") };
            var (st, col) = f.Status switch { "A" => ("NEU", "Green"), "D" => ("GELÖSCHT", "Red"), "R" => ("UMBENANNT", "Cyan"), _ => ("GEÄNDERT", "Amber") };
            head.Add(new Label { Text = st, FontSize = 9.5, FontFamily = "Cascadia Mono", TextColor = R(col), VerticalOptions = LayoutOptions.Center }, 0);
            head.Add(new Label { Text = f.OldPath != null ? $"{f.OldPath} → {f.Path}" : f.Path, FontSize = 12.5, FontFamily = "Cascadia Mono", TextColor = R("Text"), FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center }, 1);
            head.Add(new Label { Text = $"+{f.Added}", FontSize = 11, FontFamily = "Cascadia Mono", TextColor = R("Green"), VerticalOptions = LayoutOptions.Center }, 2);
            head.Add(new Label { Text = $"−{f.Removed}", FontSize = 11, FontFamily = "Cascadia Mono", TextColor = R("Red"), VerticalOptions = LayoutOptions.Center }, 3);
            var view = new RichDiffView();
            view.Show(f);
            _stack.Add(new VerticalStackLayout { Spacing = 6, Children = { head, view } });
        }
    }
}
