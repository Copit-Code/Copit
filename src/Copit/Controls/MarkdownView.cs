using System.Text.RegularExpressions;

namespace Copit.Controls;

/// <summary>Leichtgewichtiger Markdown-Renderer (Überschriften, Listen, Code-Blöcke, **fett**, `code`).</summary>
public partial class MarkdownView : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(MarkdownView), "",
        propertyChanged: (b, _, _) => ((MarkdownView)b).ScheduleRender());
    public static readonly BindableProperty TextColorProperty = BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(MarkdownView), null);

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color? TextColor { get => (Color?)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }

    private readonly VerticalStackLayout _stack = new() { Spacing = 6 };
    private bool _scheduled;
    private DateTime _lastRender = DateTime.MinValue;

    public MarkdownView() => Content = _stack;

    private void ScheduleRender()
    {
        if (_scheduled) return;
        var wait = 90 - (DateTime.Now - _lastRender).TotalMilliseconds;
        if (wait <= 0) { Render(); return; }
        _scheduled = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(wait), () => { _scheduled = false; Render(); return false; });
    }

    private static Color Res(string key) => (Color)Application.Current!.Resources[key];

    private void Render()
    {
        _lastRender = DateTime.Now;
        _stack.Children.Clear();
        var text = Text ?? "";
        var lines = text.Replace("\r", "").Split('\n');
        var para = new List<string>();
        int i = 0;

        void FlushPara()
        {
            if (para.Count == 0) return;
            _stack.Children.Add(InlineLabel(string.Join("\n", para)));
            para.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara();
                var lang = line.Trim().TrimStart('`');
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Add(lines[i++]);
                i++;
                _stack.Children.Add(CodeBlock(string.Join("\n", code), lang));
                continue;
            }
            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                FlushPara();
                var level = h.Groups[1].Value.Length;
                var lbl = InlineLabel(h.Groups[2].Value);
                lbl.FontSize = level == 1 ? 19 : level == 2 ? 16 : 14;
                lbl.FontAttributes = FontAttributes.Bold;
                lbl.Margin = new Thickness(0, 6, 0, 0);
                _stack.Children.Add(lbl);
                i++; continue;
            }
            var b = BulletRegex().Match(line);
            if (b.Success)
            {
                FlushPara();
                var indent = b.Groups[1].Value.Length;
                var marker = b.Groups[2].Value;
                var g = new Grid { ColumnDefinitions = [new ColumnDefinition(20), new ColumnDefinition(GridLength.Star)], Margin = new Thickness(indent * 6, 0, 0, 0) };
                g.Add(new Label { Text = char.IsDigit(marker[0]) ? marker : "•", TextColor = Res("Cyan"), FontSize = 13 });
                var content = InlineLabel(b.Groups[3].Value);
                g.Add(content, 1);
                _stack.Children.Add(g);
                i++; continue;
            }
            if (line.StartsWith("> "))
            {
                FlushPara();
                var quote = new Border { BackgroundColor = Res("Panel"), Padding = new Thickness(12, 6), StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 }, Content = InlineLabel(line[2..]) };
                _stack.Children.Add(quote);
                i++; continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); i++; continue; }
            if (line.Trim() is "---" or "***") { FlushPara(); _stack.Children.Add(new BoxView { HeightRequest = 1, Color = Res("Line"), Margin = new Thickness(0, 4) }); i++; continue; }
            para.Add(line);
            i++;
        }
        FlushPara();
    }

    private Label InlineLabel(string text)
    {
        var lbl = new Label { LineBreakMode = LineBreakMode.WordWrap, FontSize = 13.5, LineHeight = 1.25, TextColor = TextColor ?? Res("Text") };
        var fs = new FormattedString();
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Groups["bold"].Success)
                fs.Spans.Add(new Span { Text = m.Groups["bold"].Value, FontAttributes = FontAttributes.Bold });
            else if (m.Groups["code"].Success)
                fs.Spans.Add(new Span { Text = m.Groups["code"].Value, FontFamily = "Cascadia Code", TextColor = Res("Cyan"), FontSize = 12.5 });
            else if (m.Groups["it"].Success)
                fs.Spans.Add(new Span { Text = m.Groups["it"].Value, FontAttributes = FontAttributes.Italic });
            else if (m.Groups["link"].Success)
                fs.Spans.Add(new Span { Text = m.Groups["link"].Value, TextColor = Res("Accent"), TextDecorations = TextDecorations.Underline });
            else
                fs.Spans.Add(new Span { Text = m.Value });
        }
        lbl.FormattedText = fs;
        return lbl;
    }

    private View CodeBlock(string code, string lang)
    {
        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        header.Add(new Label { Text = string.IsNullOrWhiteSpace(lang) ? "code" : lang, FontSize = 10.5, TextColor = Res("Muted"), FontFamily = "Cascadia Code" });
        var copy = new Label { Text = "Kopieren", FontSize = 10.5, TextColor = Res("Accent") };
        copy.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await Clipboard.Default.SetTextAsync(code);
                copy.Text = "✓ Kopiert";
                await Task.Delay(1200);
                copy.Text = "Kopieren";
            })
        });
        header.Add(copy, 1);
        var body = new Label { Text = code, FontFamily = "Cascadia Code", FontSize = 12, TextColor = Res("Text"), LineBreakMode = LineBreakMode.NoWrap };
        return new Border
        {
            BackgroundColor = Color.FromArgb("#120F0D"),
            Stroke = Res("Line"), StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(12, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children = { header, new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = body } }
            }
        };
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+\.)\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|(?<!\*)\*(?<it>[^*\s][^*]*?)\*(?!\*)|\[(?<link>[^\]]+)\]\([^)]+\)|[^*`\[]+|[*`\[]")]
    private static partial Regex InlineRegex();
}

/// <summary>Farbiger Unified-Diff.</summary>
public class DiffView : ContentView
{
    public static readonly BindableProperty DiffProperty = BindableProperty.Create(nameof(Diff), typeof(string), typeof(DiffView), "",
        propertyChanged: (b, _, n) => ((DiffView)b).Render((string?)n));

    public string Diff { get => (string)GetValue(DiffProperty); set => SetValue(DiffProperty, value); }

    private readonly Label _label = new() { FontFamily = "Cascadia Code", FontSize = 12, LineBreakMode = LineBreakMode.NoWrap };

    public DiffView() => Content = new ScrollView { Orientation = ScrollOrientation.Both, Content = _label };

    private void Render(string? diff)
    {
        var fs = new FormattedString();
        var res = Application.Current!.Resources;
        Color green = (Color)res["Green"], red = (Color)res["Red"], cyan = (Color)res["Cyan"], muted = (Color)res["Muted"], text = (Color)res["Text2"];
        var lines = (diff ?? "").Replace("\r", "").Split('\n');
        int n = 0;
        foreach (var l in lines)
        {
            if (++n > 2500) { fs.Spans.Add(new Span { Text = "… (gekürzt)\n", TextColor = muted }); break; }
            Color c; Color? bg = null;
            if (l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("diff ") || l.StartsWith("index ")) c = muted;
            else if (l.StartsWith('+')) { c = green; bg = Color.FromArgb("#1D271D"); }
            else if (l.StartsWith('-')) { c = red; bg = Color.FromArgb("#351C18"); }
            else if (l.StartsWith("@@")) c = cyan;
            else c = text;
            var span = new Span { Text = l + "\n", TextColor = c };
            if (bg != null) span.BackgroundColor = bg;
            fs.Spans.Add(span);
        }
        _label.FormattedText = fs;
    }
}
