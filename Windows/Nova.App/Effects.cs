using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace Nova.App;

// Continuous animations as composition custom visuals: they are drawn on the render thread, frame by
// frame, with no work on the UI thread (the Windows counterpart of the Core Animation layers in
// CAEffects.swift). Every effect pauses while its window or page is hidden.

/// Hosts one custom visual. Subclasses send their parameters with `Send`.
public abstract class EffectLayer : Control
{
    CompositionCustomVisual? _visual;
    object? _last;
    bool _paused;

    protected EffectLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    protected abstract EffectHandler CreateHandler();

    protected void Send(object message)
    {
        _last = message;
        _visual?.SendHandlerMessage(message);
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        _visual?.SendHandlerMessage(new PauseMessage(paused));
        Ticker.Update(this);
    }

    /// One UI-thread timer wakes every running effect 24 times a second. Between ticks the render
    /// loop sleeps (asking for every animation frame kept it spinning at 60 Hz: ~12% CPU for a pulse dot).
    static class Ticker
    {
        static readonly HashSet<EffectLayer> Running = [];
        static readonly Avalonia.Threading.DispatcherTimer Timer = new(Avalonia.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 24) };
        static readonly TickMessage Tick = new();

        static Ticker() => Timer.Tick += (_, _) =>
        {
            foreach (var l in Running) l._visual?.SendHandlerMessage(Tick);
        };

        public static void Update(EffectLayer l)
        {
            if (l._visual != null && !l._paused) Running.Add(l); else Running.Remove(l);
            if (Running.Count > 0) { if (!Timer.IsEnabled) Timer.Start(); }
            else Timer.Stop();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;
        _visual = compositor.CreateCustomVisual(CreateHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        _visual.SendHandlerMessage(new PauseMessage(_paused));
        if (_last != null) _visual.SendHandlerMessage(_last);
        Ticker.Update(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        Ticker.Update(this);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual != null) _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
    }

    /// Pauses (or resumes) every effect under `root`: hidden pages, hidden windows.
    public static void Gate(Visual root, bool active)
    {
        foreach (var v in root.GetSelfAndVisualDescendants())
            if (v is EffectLayer l) l.SetPaused(!active);
    }
}

public sealed record PauseMessage(bool Paused);
public sealed class TickMessage;

public abstract class EffectHandler : CompositionCustomVisualHandler
{
    bool _paused;
    /// False for effects that are momentarily static (nothing moves): no frames requested.
    protected virtual bool Animating => true;
    /// How far outside its box the effect draws (glows, ripples).
    protected virtual double Overflow => 0;

    public override void OnMessage(object message)
    {
        if (message is TickMessage)
        {
            // Frames come from the shared ticker; slower effects skip some ticks.
            var now = CompositionNow.TotalSeconds;
            if (_paused || !Animating || now - _lastFrame < 1 / Fps - 0.004) return;
            _lastFrame = now;
            Invalidate();
            return;
        }
        if (message is PauseMessage p) _paused = p.Paused;
        else Receive(message);
        Invalidate();
    }

    protected abstract void Receive(object message);

    /// Frames per second, at most the ticker's 24: every frame makes the compositor repaint what lies
    /// under the effect, and these slow loops look the same at 24.
    protected virtual double Fps => 24;
    double _lastFrame = -1;

    public override Rect GetRenderBounds() => new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y).Inflate(Overflow);

    public override void OnRender(ImmediateDrawingContext ctx) =>
        Draw(ctx, CompositionNow.TotalSeconds, new Size(EffectiveSize.X, EffectiveSize.Y));

    protected abstract void Draw(ImmediateDrawingContext ctx, double t, Size size);

    // MARK: helpers

    protected static IImmutableBrush Solid(Color c, double a = 1) => new ImmutableSolidColorBrush(c, a);

    protected static ImmutablePen Pen(Color c, double a, double thickness) =>
        new(Solid(c, a), thickness, null, PenLineCap.Round, PenLineJoin.Round);

    /// CSS-like cubic-bezier timing curve, solved for y at x.
    protected static double Bezier(double x, double x1, double y1, double x2, double y2)
    {
        double lo = 0, hi = 1, u = x;
        for (var i = 0; i < 24; i++)
        {
            u = (lo + hi) / 2;
            var bx = 3 * (1 - u) * (1 - u) * u * x1 + 3 * (1 - u) * u * u * x2 + u * u * u;
            if (bx < x) lo = u; else hi = u;
        }
        return 3 * (1 - u) * (1 - u) * u * y1 + 3 * (1 - u) * u * u * y2 + u * u * u;
    }

    protected static double EaseInOut(double x) => Bezier(x, 0.42, 0, 0.58, 1);

    protected static double Phase(double t, double period, double offset = 0) => ((t + offset) % period + period) % period / period;

    /// Smoothly approaches a target value (state changes cross-fade over ~0.7 s).
    protected static double Approach(double current, double target, double dt, double seconds = 0.7) =>
        current + (target - current) * Math.Min(1, dt / seconds * 3);

    /// Polyline helpers: dashes walked along a sampled path.
    protected static void DrawDashes(ImmediateDrawingContext ctx, IReadOnlyList<Point> pts, IReadOnlyList<double> cum, ImmutablePen pen, double dash, double gap, double phase)
    {
        var total = cum[^1];
        var period = dash + gap;
        var start = ((phase % period) + period) % period - period;
        for (var s = start; s < total; s += period)
        {
            var a = Math.Max(0, s);
            var b = Math.Min(total, s + dash);
            if (b <= a) continue;
            ctx.DrawLine(pen, At(pts, cum, a), At(pts, cum, b));
        }
    }

    protected static Point At(IReadOnlyList<Point> pts, IReadOnlyList<double> cum, double dist)
    {
        if (dist <= 0) return pts[0];
        for (var i = 1; i < pts.Count; i++)
        {
            if (cum[i] < dist) continue;
            var seg = cum[i] - cum[i - 1];
            var f = seg <= 0 ? 0 : (dist - cum[i - 1]) / seg;
            return new Point(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * f, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * f);
        }
        return pts[^1];
    }

    protected static List<double> Cumulative(IReadOnlyList<Point> pts)
    {
        var cum = new List<double>(pts.Count) { 0 };
        for (var i = 1; i < pts.Count; i++)
        {
            var d = pts[i] - pts[i - 1];
            cum.Add(cum[i - 1] + Math.Sqrt(d.X * d.X + d.Y * d.Y));
        }
        return cum;
    }

    protected static List<Point> Arc(Point c, double r, double from, double to, int steps = 48)
    {
        var pts = new List<Point>(steps + 1);
        for (var i = 0; i <= steps; i++)
        {
            var a = from + (to - from) * i / steps;
            pts.Add(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)));
        }
        return pts;
    }

    protected static List<Point> RoundedRect(Rect r, double radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var pts = new List<Point>();
        void Corner(double cx, double cy, double a0) => pts.AddRange(Arc(new Point(cx, cy), radius, a0, a0 + Math.PI / 2, 8));
        Corner(r.Right - radius, r.Top + radius, -Math.PI / 2);
        Corner(r.Right - radius, r.Bottom - radius, 0);
        Corner(r.Left + radius, r.Bottom - radius, Math.PI / 2);
        Corner(r.Left + radius, r.Top + radius, Math.PI);
        pts.Add(pts[0]);
        return pts;
    }
}

// MARK: - Nova orb effects (halo, rings, sparks, connecting arc)

public enum OrbState { Off, Connecting, On, Warning, Error }

public sealed class OrbEffects : EffectLayer
{
    // Wider than the button: the halo and ripples spill past it (a custom visual is clipped to its size).
    public OrbEffects() { Width = 260; Height = 260; Margin = new Thickness(-52); }
    public void Apply(OrbState s) => Send(s);
    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        OrbState _state = OrbState.Off;
        double _halo, _haloTarget, _lastT = -1;
        protected override bool Animating => true;
        protected override double Overflow => 50;

        static readonly (double X, double Y, double Offset)[] SparkSpots =
            [(-63, -44, 0), (70, -26, 0.4), (-44, 63, 0.9), (51, 56, 1.3), (-5, -74, 1.8), (-74, 10, 2.1)];

        protected override void Receive(object message)
        {
            if (message is not OrbState s) return;
            _state = s;
            _haloTarget = s switch { OrbState.On or OrbState.Warning => 0.85, OrbState.Connecting => 0.4, OrbState.Error => 0.35, _ => 0 };
        }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            var dt = _lastT < 0 ? 1 : t - _lastT;
            _lastT = t;
            _halo = Approach(_halo, _haloTarget, dt);
            var c = new Point(size.Width / 2, size.Height / 2);
            var lit = _state is OrbState.On or OrbState.Warning;
            var accent = _state switch { OrbState.Warning => Look.Amber, OrbState.Error => Look.Danger, _ => Look.Violet };

            // Halo: a conic gradient ring with a soft radial edge (stands in for the blurred CA layer).
            if (_halo > 0.01)
            {
                var period = _state == OrbState.Connecting ? 1.4 : 7.2;
                var angle = _state == OrbState.Off ? 0 : Phase(t, period) * 360;
                Color[] colors = _state == OrbState.Error ? [Look.Danger, Look.Coral, Look.Danger] : [Look.Violet, Look.Coral, Look.Mint, Look.Violet];
                var stops = colors.Select((col, i) => new ImmutableGradientStop((double)i / (colors.Length - 1), col)).ToList();
                var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                // Soft edge from concentric strokes, each with its own brush opacity
                // (an opacity mask would cost an offscreen pass every frame).
                for (var r = 56.0; r <= 92; r += 2)
                {
                    // Peak just outside the core, fading out like a blur.
                    var a = r < 66 ? (r - 52) / 14 : Math.Pow(1 - (r - 66) / 30, 2);
                    var conic = new ImmutableConicGradientBrush(stops, _halo * 0.32 * a, null, null, GradientSpreadMethod.Pad, center, angle);
                    ctx.DrawEllipse(null, new ImmutablePen(conic, 3), c, r, r);
                }
            }

            if (lit)
            {
                // Rings ripple out and fade.
                for (var i = 0; i < 3; i++)
                {
                    var p = Phase(t, 3.6, i * 1.2);
                    var e = Bezier(p, 0.15, 0.6, 0.3, 1);
                    var scale = 0.72 + (1.32 - 0.72) * e;
                    var opacity = 0.9 * (1 - e);
                    ctx.DrawEllipse(null, Pen(accent, 0.6 * opacity, 1.5), c, 78 * scale, 78 * scale);
                }
                // Sparks twinkle.
                foreach (var (x, y, off) in SparkSpots)
                {
                    var p = Phase(t, 2.4, off);
                    var k = EaseInOut(p < 0.5 ? p * 2 : (1 - p) * 2);
                    var rad = 2 * (0.4 + 0.8 * k);
                    ctx.DrawEllipse(Solid(Colors.White, k), null, new Point(c.X + x, c.Y + y), rad, rad);
                }
            }

            if (_state == OrbState.Connecting)
            {
                var start = Phase(t, 0.9) * 2 * Math.PI - Math.PI / 2;
                var pts = Arc(c, 64, start, start + 0.28 * 2 * Math.PI, 32);
                var pen = Pen(Look.Violet, 1, 3);
                for (var i = 1; i < pts.Count; i++) ctx.DrawLine(pen, pts[i - 1], pts[i]);
            }
        }
    }
}

// MARK: - Pulsing status dot

public sealed class PulseDot : EffectLayer
{
    public PulseDot(Color color)
    {
        Width = 8;
        Height = 8;
        SetColor(color);
    }

    public void SetColor(Color c) => Send(c);
    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        Color _color = Look.Mint;
        protected override double Overflow => 8;
        protected override double Fps => 15;
        protected override void Receive(object message) { if (message is Color c) _color = c; }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            var c = new Point(size.Width / 2, size.Height / 2);
            var p = Phase(t, 1.6);
            var e = Bezier(p, 0, 0, 0.58, 1);
            var scale = 1 + 1.2 * e;
            ctx.DrawEllipse(null, Pen(_color, 0.6 * (1 - e), 2), c, 3 * scale, 3 * scale);
            ctx.DrawEllipse(Solid(_color), null, c, 4, 4);
        }
    }
}

// MARK: - Routes map: curves, marching dashes, particles

public sealed record FlowRoute(Point From, Point To, Color Color, int Particles);

/// The static wide curves under the routes: an ordinary control, rendered once per layout change.
public sealed class FlowPipes : Control
{
    IReadOnlyList<FlowRoute> _routes = [];

    public FlowPipes() => IsHitTestVisible = false;

    public void Update(IReadOnlyList<FlowRoute> routes)
    {
        _routes = routes;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        foreach (var r in _routes)
        {
            var mid = (r.From.X + r.To.X) / 2;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(r.From, false);
                g.CubicBezierTo(new Point(mid, r.From.Y), new Point(mid, r.To.Y), r.To);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(r.Color, 0.08), 12, lineCap: PenLineCap.Round), geo);
        }
    }
}

public sealed class FlowLayer : EffectLayer
{
    public void Update(IReadOnlyList<FlowRoute> routes) => Send(routes.ToList());
    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        sealed record Built(FlowRoute Route, List<Point> Points, List<double> Cum);
        List<Built> _routes = [];
        protected override bool Animating => _routes.Count > 0;
        protected override double Overflow => 12;

        protected override void Receive(object message)
        {
            if (message is not List<FlowRoute> routes) return;
            _routes = routes.Select(r =>
            {
                var pts = Curve(r.From, r.To);
                return new Built(r, pts, Cumulative(pts));
            }).ToList();
        }

        static List<Point> Curve(Point a, Point b)
        {
            var mid = (a.X + b.X) / 2;
            Point c1 = new(mid, a.Y), c2 = new(mid, b.Y);
            var pts = new List<Point>(65);
            for (var i = 0; i <= 64; i++)
            {
                var u = i / 64.0;
                var v = 1 - u;
                pts.Add(new Point(
                    v * v * v * a.X + 3 * v * v * u * c1.X + 3 * v * u * u * c2.X + u * u * u * b.X,
                    v * v * v * a.Y + 3 * v * v * u * c1.Y + 3 * v * u * u * c2.Y + u * u * u * b.Y));
            }
            return pts;
        }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            for (var i = 0; i < _routes.Count; i++)
            {
                var (r, pts, cum) = _routes[i];
                // The wide static "pipe" under each route is drawn once by FlowPipes, not every frame.
                DrawDashes(ctx, pts, cum, Pen(r.Color, 0.7, 2), 3, 13, Phase(t, 0.9) * 16);

                var total = cum[^1];
                for (var k = 0; k < r.Particles; k++)
                {
                    const double period = 2.6;
                    var p = Phase(t, period, period * k / r.Particles + i * 0.37);
                    var pos = At(pts, cum, total * Bezier(p, 0.45, 0.05, 0.55, 0.95));
                    var fade = p < 0.1 ? p / 0.1 : p > 0.9 ? (1 - p) / 0.1 : 1;
                    // Glow as two translucent discs (a gradient brush per particle per frame cost more).
                    ctx.DrawEllipse(Solid(r.Color, 0.22 * fade), null, pos, 10, 10);
                    ctx.DrawEllipse(Solid(r.Color, 0.45 * fade), null, pos, 6.5, 6.5);
                    ctx.DrawEllipse(Solid(Colors.White, 0.95 * fade), null, pos, 4, 4);
                }
            }
        }
    }
}

// MARK: - Small decorative effects

/// A rounded outline whose opacity breathes; for live server nodes.
public sealed class BreathingRim : EffectLayer
{
    public BreathingRim(double cornerRadius, bool active) => Send((cornerRadius, active));
    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        double _radius = 18;
        bool _active;
        protected override bool Animating => _active;
        protected override double Fps => 20;
        protected override void Receive(object message) { if (message is (double r, bool a)) { _radius = r; _active = a; } }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            var p = Phase(t, 2.8);
            var opacity = _active ? 0.18 + 0.32 * EaseInOut(p < 0.5 ? p * 2 : (1 - p) * 2) : 0.18;
            var r = new Rect(size).Deflate(0.6);
            ctx.DrawRectangle(null, new ImmutablePen(Solid(Colors.White, opacity), 1.2), r, _radius, _radius, default);
        }
    }
}

/// A soft band of light sweeping across (horizontal) or down (vertical).
public sealed class SweepingLight : EffectLayer
{
    public SweepingLight(bool horizontal, Color color, double thickness, double period, bool active = true)
    {
        ClipToBounds = true;
        Send((horizontal, color, thickness, period, active));
    }

    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        bool _horizontal = true, _active = true;
        Color _color;
        double _thickness = 120, _period = 3;
        protected override bool Animating => _active;

        protected override void Receive(object message)
        {
            if (message is ValueTuple<bool, Color, double, double, bool> m)
                (_horizontal, _color, _thickness, _period, _active) = m;
        }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            if (!_active || size.Width <= 0) return;
            var e = EaseInOut(Phase(t, _period));
            var span = (_horizontal ? size.Width : size.Height) + _thickness;
            var pos = -_thickness + span * e;
            var rect = _horizontal ? new Rect(pos, 0, _thickness, size.Height) : new Rect(0, pos, size.Width, _thickness);
            var brush = new ImmutableLinearGradientBrush(
                [new(0, _color.WithAlpha(0)), new(0.5, _color), new(1, _color.WithAlpha(0))], 1, null, null, GradientSpreadMethod.Pad,
                new RelativePoint(0, 0, RelativeUnit.Relative), _horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative));
            using (ctx.PushClip(new Rect(size)))
                ctx.FillRectangle(brush, rect, 0);
        }
    }
}

/// Dashed rounded border whose dashes march around.
public sealed class MarchingBorder : EffectLayer
{
    public MarchingBorder(double cornerRadius, Color color) => Send((cornerRadius, color));
    protected override EffectHandler CreateHandler() => new Handler();

    sealed class Handler : EffectHandler
    {
        double _radius = 18;
        Color _color = Look.Violet;
        Size _builtFor;
        List<Point> _pts = [];
        List<double> _cum = [0];
        protected override double Fps => 20;
        protected override void Receive(object message) { if (message is (double r, Color c)) { _radius = r; _color = c; _builtFor = default; } }

        protected override void Draw(ImmediateDrawingContext ctx, double t, Size size)
        {
            if (size.Width <= 2 || size.Height <= 2) return;
            if (size != _builtFor)
            {
                _builtFor = size;
                _pts = RoundedRect(new Rect(size).Deflate(0.75), _radius);
                _cum = Cumulative(_pts);
            }
            DrawDashes(ctx, _pts, _cum, Pen(_color, 1, 1.5), 8, 8, Phase(t, 1) * 16);
        }
    }
}
