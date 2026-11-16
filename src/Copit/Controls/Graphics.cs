using Microsoft.Maui.Graphics;

namespace Copit.Controls;

internal static class Palette
{
    public static Color Get(string key, string fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : Color.FromArgb(fallback);
    public static Color Accent => Get("Accent", "#E8743B");
    public static Color Line => Get("Line", "#3A312A");
    public static Color Text => Get("Text", "#EDE6DD");
}

/// <summary>
/// "Arbeitet"-Indikator wie in der VSCodium-Extension: 2×2 Quadrate, ein oranges Feld springt
/// im Uhrzeigersinn weiter (harte Schritte, 250 ms). Im Leerlauf leuchtet nur das erste Feld gedimmt.
/// </summary>
public class AiOrb : GraphicsView, IDrawable
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(AiOrb), false, propertyChanged: (b, _, _) => ((AiOrb)b).Invalidate());

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    // Zellen: 0 1 / 2 3 – Reihenfolge im Uhrzeigersinn wie @keyframes cp (1 → 2 → 4 → 3)
    private static readonly int[] Order = [0, 1, 3, 2];
    private int _step;
    private bool _running;

    public AiOrb()
    {
        Drawable = this;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _running = false;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
        {
            if (!_running) return false;
            if (!IsVisible || !IsActive) return true;
            _step = (_step + 1) % 4;
            Invalidate();
            return true;
        });
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var size = Math.Min(Math.Min(rect.Width, rect.Height), 64f);
        var gap = Math.Max(2f, size / 6f);
        var cell = (size - gap) / 2f;
        var x0 = rect.Center.X - size / 2; var y0 = rect.Center.Y - size / 2;
        var lit = Order[_step];
        for (int i = 0; i < 4; i++)
        {
            var x = x0 + (i % 2) * (cell + gap);
            var y = y0 + (i / 2) * (cell + gap);
            canvas.FillColor = IsActive && i == lit ? Palette.Accent : !IsActive && i == 0 ? Palette.Accent.WithAlpha(0.45f) : Palette.Line;
            canvas.FillRectangle(x, y, cell, cell);
        }
    }
}

/// <summary>Kleine Quadrat-Explosion für erfolgreiche Buchungen.</summary>
public class ConfettiView : GraphicsView, IDrawable
{
    private record struct P(float X, float Y, float Vx, float Vy, float Rot, float Vr, Color C, float S);
    private readonly List<P> _ps = [];
    private readonly Random _rnd = new();
    private bool _running;

    public ConfettiView()
    {
        Drawable = this;
        InputTransparent = true;
        BackgroundColor = Colors.Transparent;
    }

    public void Burst(int count = 120)
    {
        var w = (float)Width; var h = (float)Height;
        if (w <= 0 || h <= 0) return;
        Color[] palette = [Palette.Accent, Palette.Get("Green", "#6BBF7A"), Palette.Text, Palette.Get("Amber", "#D9A441")];
        for (int i = 0; i < count; i++)
        {
            var ang = (float)(_rnd.NextDouble() * Math.PI - Math.PI);
            var speed = 5 + (float)_rnd.NextDouble() * 11;
            _ps.Add(new P(w * 0.75f, h * 0.55f, MathF.Cos(ang) * speed, MathF.Sin(ang) * speed - 3, 0,
                (float)(_rnd.NextDouble() - 0.5) * 16, palette[_rnd.Next(palette.Length)], 3 + _rnd.Next(5)));
        }
        if (_running) return;
        _running = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(16), () =>
        {
            for (int i = _ps.Count - 1; i >= 0; i--)
            {
                var p = _ps[i];
                p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.32f; p.Vx *= 0.985f; p.Rot += p.Vr;
                if (p.Y > Height + 20) _ps.RemoveAt(i); else _ps[i] = p;
            }
            Invalidate();
            _running = _ps.Count > 0;
            return _running;
        });
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        foreach (var p in _ps)
        {
            canvas.SaveState();
            canvas.Translate(p.X, p.Y);
            canvas.Rotate(p.Rot);
            canvas.FillColor = p.C;
            canvas.FillRectangle(-p.S / 2, -p.S / 2, p.S, p.S);
            canvas.RestoreState();
        }
    }
}

/// <summary>Fortschrittsring (Stunden heute, Kontextfenster) – dünn, ohne Rundungen.</summary>
public class RingView : GraphicsView, IDrawable
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(nameof(Progress), typeof(double), typeof(RingView), 0.0,
        propertyChanged: (b, o, n) => ((RingView)b).AnimateTo((double)n));
    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(RingView), Color.FromArgb("#E8743B"),
        propertyChanged: (b, _, _) => ((RingView)b).Invalidate());
    public static readonly BindableProperty ThicknessProperty = BindableProperty.Create(nameof(Thickness), typeof(float), typeof(RingView), 3f);

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public float Thickness { get => (float)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private double _shown;

    public RingView() { Drawable = this; BackgroundColor = Colors.Transparent; InputTransparent = true; }

    private void AnimateTo(double target)
    {
        var from = _shown;
        this.AbortAnimation("ring");
        new Animation(v => { _shown = v; Invalidate(); }, from, target, Easing.CubicOut).Commit(this, "ring", 16, 600);
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var t = Thickness;
        var r = new RectF(rect.X + t / 2, rect.Y + t / 2, rect.Width - t, rect.Height - t);
        canvas.StrokeSize = t;
        canvas.StrokeLineCap = LineCap.Butt;
        canvas.StrokeColor = Palette.Line;
        canvas.DrawEllipse(r);
        var p = Math.Clamp(_shown, 0, 1);
        if (p <= 0.001) return;
        canvas.StrokeColor = Color;
        canvas.DrawArc(r, 90, 90 - (float)(360 * p), true, false);
    }
}

/// <summary>Quadratischer Statusmarker, optional pulsierend.</summary>
public class PulseDot : GraphicsView, IDrawable
{
    public static readonly BindableProperty ColorProperty = BindableProperty.Create(nameof(Color), typeof(Color), typeof(PulseDot), Colors.Gray,
        propertyChanged: (b, _, _) => ((PulseDot)b).Invalidate());
    public static readonly BindableProperty PulsingProperty = BindableProperty.Create(nameof(Pulsing), typeof(bool), typeof(PulseDot), false,
        propertyChanged: (b, _, n) => { if ((bool)n) ((PulseDot)b).Start(); });

    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool Pulsing { get => (bool)GetValue(PulsingProperty); set => SetValue(PulsingProperty, value); }

    private double _t;
    private bool _running;

    public PulseDot() { Drawable = this; WidthRequest = 8; HeightRequest = 8; BackgroundColor = Colors.Transparent; InputTransparent = true; VerticalOptions = LayoutOptions.Center; }

    private void Start()
    {
        if (_running) return;
        _running = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(40), () =>
        {
            _t += 0.08; Invalidate();
            _running = Pulsing;
            return _running;
        });
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var s = Math.Min(rect.Width, rect.Height);
        var alpha = Pulsing ? 0.35f + 0.65f * (float)(0.5 + 0.5 * Math.Sin(_t * 3)) : 1f;
        canvas.FillColor = Color.WithAlpha(alpha);
        canvas.FillRectangle(rect.Center.X - s / 2, rect.Center.Y - s / 2, s, s);
    }
}

/// <summary>Logo: orangefarbenes Quadrat mit eckigem "C".</summary>
public class LogoView : GraphicsView, IDrawable
{
    /// <summary>Arbeitet gerade ein Agent? Dann läuft der Punkt wie der 2×2-Loader im Uhrzeigersinn um.</summary>
    public static readonly BindableProperty BusyProperty =
        BindableProperty.Create(nameof(Busy), typeof(bool), typeof(LogoView), false, propertyChanged: (b, _, n) => ((LogoView)b).OnBusy((bool)n));

    public bool Busy { get => (bool)GetValue(BusyProperty); set => SetValue(BusyProperty, value); }

    private int _step;
    private bool _ticking;
    // Punkt-Positionen im 24er-Raster (Mitte der Klammeröffnung + drei Ecken der Klammer) – im Uhrzeigersinn
    private static readonly (float X, float Y)[] Spots = [(15.5f, 12f), (15.5f, 8.5f), (8.5f, 8.5f), (8.5f, 15.5f), (15.5f, 15.5f)];

    public LogoView()
    {
        Drawable = this; BackgroundColor = Colors.Transparent; InputTransparent = true;
        // kurze Intro-Animation beim Start
        Loaded += (_, _) => { _intro = 8; OnBusy(true); };
    }

    private int _intro;

    private void OnBusy(bool on)
    {
        if (!on && _intro <= 0) { _step = 0; Invalidate(); return; }
        if (_ticking) return;
        _ticking = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
        {
            if (_intro > 0) _intro--;
            if (!Busy && _intro <= 0) { _ticking = false; _step = 0; Invalidate(); return false; }
            _step = _step % 4 + 1;
            Invalidate();
            return true;
        });
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var s = Math.Min(rect.Width, rect.Height);
        var k = s / 24f;
        var ox = rect.Center.X - s / 2; var oy = rect.Center.Y - s / 2;
        float X(float v) => ox + v * k; float Y(float v) => oy + v * k;
        canvas.StrokeColor = Palette.Text;
        canvas.StrokeSize = Math.Max(1.2f, 1.8f * k);
        canvas.StrokeLineCap = LineCap.Square;
        canvas.StrokeLineJoin = LineJoin.Miter;
        canvas.DrawRectangle(X(3), Y(3), 18 * k, 18 * k);
        var p = new PathF();
        p.MoveTo(X(15.5f), Y(8.5f)); p.LineTo(X(8.5f), Y(8.5f)); p.LineTo(X(8.5f), Y(15.5f)); p.LineTo(X(15.5f), Y(15.5f));
        canvas.DrawPath(p);
        var (dx, dy) = Spots[_step];
        canvas.FillColor = Palette.Accent;
        canvas.FillRectangle(X(dx - 1), Y(dy - 1), 2 * k + 0.5f, 2 * k + 0.5f);
    }
}
