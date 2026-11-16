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
/// "Arbeitet"-Indikator: 3×3 Raster aus Quadraten, durch das eine Welle läuft.
/// Im Leerlauf ruht es mit einem einzelnen Akzent-Quadrat.
/// </summary>
public class AiOrb : GraphicsView, IDrawable
{
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(AiOrb), false);

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    // Reihenfolge der Zellen: Spirale im Uhrzeigersinn, Mitte zuletzt
    private static readonly int[] Order = [0, 1, 2, 5, 8, 7, 6, 3, 4];
    private readonly float[] _heat = new float[9];
    private double _t;
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
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(33), () =>
        {
            if (!_running) return false;
            if (!IsVisible) return true;
            _t += IsActive ? 0.033 * 9 : 0.033 * 1.2;
            var head = (int)_t % 9;
            for (int i = 0; i < 9; i++) _heat[i] = Math.Max(0, _heat[i] - (IsActive ? 0.09f : 0.03f));
            if (IsActive) _heat[Order[head]] = 1f;
            else _heat[4] = 0.55f + 0.25f * (float)Math.Sin(_t * 2);
            Invalidate();
            return true;
        });
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var size = Math.Min(Math.Min(rect.Width, rect.Height), 72f);
        var gap = size * 0.1f;
        var cell = (size - gap * 2) / 3f;
        var x0 = rect.Center.X - size / 2; var y0 = rect.Center.Y - size / 2;
        var accent = Palette.Accent; var line = Palette.Line;
        for (int i = 0; i < 9; i++)
        {
            var x = x0 + (i % 3) * (cell + gap);
            var y = y0 + (i / 3) * (cell + gap);
            canvas.StrokeColor = line;
            canvas.StrokeSize = 1;
            canvas.DrawRectangle(x + 0.5f, y + 0.5f, cell - 1, cell - 1);
            var h = _heat[i];
            if (h > 0.01f)
            {
                canvas.FillColor = accent.WithAlpha(h);
                canvas.FillRectangle(x, y, cell, cell);
            }
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
    public LogoView() { Drawable = this; BackgroundColor = Colors.Transparent; InputTransparent = true; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var s = Math.Min(rect.Width, rect.Height);
        var r = new RectF(rect.Center.X - s / 2, rect.Center.Y - s / 2, s, s);
        canvas.FillColor = Palette.Accent;
        canvas.FillRoundedRectangle(r, s * 0.14f);
        canvas.StrokeColor = Color.FromArgb("#15110E");
        canvas.StrokeSize = s * 0.12f;
        canvas.StrokeLineCap = LineCap.Butt;
        canvas.StrokeLineJoin = LineJoin.Miter;
        var p = new PathF();
        float l = r.X + s * 0.3f, t = r.Y + s * 0.28f, rr = r.X + s * 0.7f, b = r.Y + s * 0.72f;
        p.MoveTo(rr, t); p.LineTo(l, t); p.LineTo(l, b); p.LineTo(rr, b);
        canvas.DrawPath(p);
        canvas.FillColor = Color.FromArgb("#15110E");
        canvas.FillRectangle(rr - s * 0.06f, r.Center.Y - s * 0.06f, s * 0.12f, s * 0.12f);
    }
}
