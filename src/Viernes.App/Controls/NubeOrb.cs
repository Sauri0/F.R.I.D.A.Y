using System.Windows;
using System.Windows.Media;
using Viernes.App.ViewModels;

using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Viernes.App.Controls;

/// <summary>
/// Una esfera de puntos proyectada en perspectiva: de la profundidad de cada punto salen su tamaño
/// y su alfa. Encima giran tres anillos de círculo máximo con inclinaciones distintas; donde se
/// cruzan sube la densidad y aparecen los nudos brillantes.
/// </summary>
/// <remarks>
/// Acá <b>sí puede girar</b>, y es la mitad de su vocabulario: no hay reflejo especular que se
/// despegue del borde. Es justo lo que la Gota tiene prohibido.
/// <para>
/// Se dibuja acumulando los puntos de cada bucket de profundidad en una sola
/// <see cref="StreamGeometry"/> y pintándola de un saque. Con 460 formas sueltas por cuadro no
/// alcanzaría; con quince geometrías sí.
/// </para>
/// </remarks>
internal sealed class NubeOrb : FrameworkElement
{
    private const int TotalPoints = 460;
    private const double CenterX = 35.0;
    private const double CenterY = 36.5;
    private const double Radius = 25.0;
    private const double ScaleX = 1.035;
    private const double ScaleY = 0.965;

    /// <summary>Inclinación de cámara: mirar la esfera algo desde arriba evita que los anillos se vean como rayas.</summary>
    private const double CameraTilt = 0.34;

    private const int DepthBuckets = 5;
    private static readonly double[] RingTilts = [0.0, 0.63, 1.15];
    private static readonly TimeSpan StateTransition = TimeSpan.FromMilliseconds(320);

    private readonly CorePoint[] _core;
    private readonly RingPoint[][] _rings = new RingPoint[3][];
    private readonly CorePoint[] _fringe;
    private readonly List<Splat>[] _buckets = new List<Splat>[DepthBuckets];

    private readonly double[] _ringPhase = new double[3];
    private double _yaw;
    private double _wobbleClock;
    private double _clock;
    private long _lastTicks;
    private bool _isRunning;

    private NubeProfile _from = NubeProfile.For(AssistantVisualState.Idle);
    private NubeProfile _to = NubeProfile.For(AssistantVisualState.Idle);
    private double _transition = 1.0;

    public NubeOrb()
    {
        // Semilla fija: la nube tiene que verse igual en cada arranque, no distinta cada vez.
        var random = new Random(20260816);

        var coreCount = (int)Math.Round(TotalPoints * 0.326);
        var ringCount = (int)Math.Round(TotalPoints * 0.196);
        var fringeCount = (int)Math.Round(TotalPoints * 0.087);

        _core = new CorePoint[coreCount];
        for (var i = 0; i < coreCount; i++)
        {
            // Radio con raíz cúbica para que el llenado del volumen sea parejo y no se apelotone.
            _core[i] = CorePoint.OnSphere(random, Math.Cbrt(random.NextDouble()) * 0.52);
        }

        for (var shell = 0; shell < 3; shell++)
        {
            _rings[shell] = new RingPoint[ringCount];
            for (var i = 0; i < ringCount; i++)
            {
                _rings[shell][i] = new RingPoint(
                    ((double)i / ringCount * 2 * Math.PI) + (random.NextDouble() * 0.05),
                    0.84 + (shell * 0.08) + ((random.NextDouble() - 0.5) * 0.055),
                    (random.NextDouble() - 0.5) * 0.075,
                    random.NextDouble() * 10,
                    6 + (random.NextDouble() * 6.5),
                    random.NextDouble());
            }
        }

        _fringe = new CorePoint[fringeCount];
        for (var i = 0; i < fringeCount; i++)
        {
            _fringe[i] = CorePoint.OnSphere(random, 1.02 + (random.NextDouble() * 0.16));
        }

        for (var i = 0; i < DepthBuckets; i++)
        {
            _buckets[i] = new List<Splat>(TotalPoints);
        }

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(NubeOrb),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

    internal AssistantVisualState State
    {
        get => (AssistantVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Sobre escritorio claro la nube se dibuja con el color de profundidad, no con el cuerpo.</summary>
    public static readonly DependencyProperty IsLightDesktopProperty = DependencyProperty.Register(
        nameof(IsLightDesktop),
        typeof(bool),
        typeof(NubeOrb),
        new PropertyMetadata(false));

    internal bool IsLightDesktop
    {
        get => (bool)GetValue(IsLightDesktopProperty);
        set => SetValue(IsLightDesktopProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var orb = (NubeOrb)d;
        orb._from = NubeProfile.Lerp(orb._from, orb._to, orb._transition);
        orb._to = NubeProfile.For((AssistantVisualState)e.NewValue);
        orb._transition = 0;
    }

    private void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _lastTicks = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        var ticks = rendering.RenderingTime.Ticks;
        var delta = _lastTicks == 0 ? 0 : (ticks - _lastTicks) / (double)TimeSpan.TicksPerSecond;
        _lastTicks = ticks;
        delta = Math.Clamp(delta, 0, 0.1);

        _clock += delta;
        if (_transition < 1)
        {
            _transition = Math.Min(1, _transition + (delta / StateTransition.TotalSeconds));
        }

        // El giro no interpola: acumula. Por eso nunca salta al cambiar de estado.
        _wobbleClock += delta;
        _yaw += delta * _to.Yaw;
        for (var i = 0; i < 3; i++)
        {
            _ringPhase[i] += delta * _to.Spin[i];
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var eased = 0.5 - (Math.Cos(Math.PI * Math.Clamp(_transition, 0, 1)) / 2);
        var current = NubeProfile.Lerp(_from, _to, eased);
        var light = IsLightDesktop;
        var tint = light ? current.Depth : current.Body;

        var scale = side / 70.0;
        context.PushTransform(new ScaleTransform(scale, scale));

        DrawHalo(context, tint, light);

        var extra = 0.0;
        if (_to.Breath is { } breath)
        {
            extra += breath.Amplitude * Math.Sin(2 * Math.PI * _clock / breath.Period) * eased;
        }

        if (_to.Tremor is { } tremor)
        {
            extra += tremor.Amplitude * Math.Sin(2 * Math.PI * _clock / tremor.Period) * eased;
        }

        DrawCore(context, current, tint, light, extra);
        DrawRings(context, current, tint, light, extra);
        DrawFringe(context, current, tint, light, extra);

        context.Pop();
    }

    private void DrawHalo(DrawingContext context, Color tint, bool light)
    {
        // El halo da masa: sin él los puntos se desarman a 108 px.
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            [
                new GradientStop(WithAlpha(tint, light ? 0.10 : 0.22), 0),
                new GradientStop(WithAlpha(tint, light ? 0.05 : 0.10), 0.6),
                new GradientStop(WithAlpha(tint, 0), 1)
            ]
        };
        brush.Freeze();

        var span = Radius * 1.3;
        context.DrawEllipse(brush, null, new Point(CenterX, CenterY), span, span);
    }

    private void DrawCore(DrawingContext context, NubeProfile profile, Color tint, bool light, double extra)
    {
        ClearBuckets();
        var yawCos = Math.Cos(_yaw);
        var yawSin = Math.Sin(_yaw);

        foreach (var point in _core)
        {
            var radius = point.Radius * profile.Dispersion *
                (1 + extra + (profile.Wobble * Math.Sin((2 * Math.PI * _wobbleClock / point.Period) + point.Offset)));
            var x = point.X * radius;
            var y = point.Y * radius;
            var z = point.Z * radius;
            Push((x * yawCos) + (z * yawSin), y, (-x * yawSin) + (z * yawCos), radius, 0.85, rim: false);
        }

        Paint(context, tint, light ? 0.5 : 0.42, light, rim: false);
    }

    private void DrawRings(DrawingContext context, NubeProfile profile, Color tint, bool light, double extra)
    {
        ClearBuckets();

        for (var shell = 0; shell < 3; shell++)
        {
            var tilt = RingTilts[shell] * profile.Tilt;
            var tiltCos = Math.Cos(tilt);
            var tiltSin = Math.Sin(tilt);
            var phase = _ringPhase[shell] + _yaw;
            var phaseCos = Math.Cos(phase);
            var phaseSin = Math.Sin(phase);

            foreach (var point in _rings[shell])
            {
                // En error el anillo se rompe: un tramo de cada vuelta simplemente no se dibuja.
                if (profile.IsBroken && (point.Hash + (_clock * 0.12)) % 1 < 0.22)
                {
                    continue;
                }

                var radius = point.Base * profile.Dispersion *
                    (1 + extra + (profile.Wobble * Math.Sin((2 * Math.PI * _wobbleClock / point.Period) + point.Offset)));
                if (profile.IsBroken)
                {
                    radius *= 1 + (0.14 * Math.Sin((2 * Math.PI * _clock / 0.21) + (point.Offset * 3.1)));
                }

                var lx = Math.Cos(point.Angle) * radius;
                var ly = Math.Sin(point.Angle) * radius;
                var lz = point.OutOfPlane * radius;
                var y = (ly * tiltCos) - (lz * tiltSin);
                var z = (ly * tiltSin) + (lz * tiltCos);
                Push((lx * phaseCos) + (z * phaseSin), y, (-lx * phaseSin) + (z * phaseCos), radius, 0.82, rim: true);
            }
        }

        var ringTint = light ? tint : Mix(tint, Colors.White, 0.24);
        Paint(context, ringTint, light ? 0.85 : 0.72, light, rim: true);
    }

    private void DrawFringe(DrawingContext context, NubeProfile profile, Color tint, bool light, double extra)
    {
        ClearBuckets();
        var yawCos = Math.Cos(_yaw);
        var yawSin = Math.Sin(_yaw);

        foreach (var point in _fringe)
        {
            // Los flecos rompen el círculo perfecto: sin ellos parece un planeta.
            var radius = point.Radius * profile.Dispersion *
                (1 + (extra * 1.6) + (profile.Wobble * 1.8 * Math.Sin((2 * Math.PI * _wobbleClock / point.Period) + point.Offset)));
            var x = point.X * radius;
            var y = point.Y * radius;
            var z = point.Z * radius;
            Push((x * yawCos) + (z * yawSin), y, (-x * yawSin) + (z * yawCos), radius, 0.7, rim: false);
        }

        var fringeTint = light ? tint : Mix(tint, Colors.White, 0.34);
        Paint(context, fringeTint, light ? 0.34 : 0.26, light, rim: false);
    }

    /// <summary>Proyecta un punto y lo guarda en el bucket de profundidad que le toca.</summary>
    private void Push(double x, double y, double z, double radius, double size, bool rim)
    {
        var cameraCos = Math.Cos(CameraTilt);
        var cameraSin = Math.Sin(CameraTilt);
        var projectedY = (y * cameraCos) - (z * cameraSin);
        var projectedZ = (y * cameraSin) + (z * cameraCos);

        var depth = ((projectedZ / radius) + 1) / 2;
        var bucket = Math.Clamp((int)(depth * DepthBuckets), 0, DepthBuckets - 1);

        // Los puntos del canto —donde la esfera se dobla— son los que dibujan la silueta solos.
        var rimStrength = rim ? 1 - Math.Abs(projectedZ / radius) : 0;

        _buckets[bucket].Add(new Splat(
            CenterX + (x * Radius * ScaleX),
            CenterY + (projectedY * Radius * ScaleY),
            size * (0.72 + (0.56 * depth)),
            rimStrength));
    }

    private void Paint(DrawingContext context, Color tint, double baseAlpha, bool light, bool rim)
    {
        for (var i = 0; i < DepthBuckets; i++)
        {
            var splats = _buckets[i];
            if (splats.Count == 0)
            {
                continue;
            }

            var depth = (double)i / (DepthBuckets - 1);
            var alpha = baseAlpha * (light
                ? 1.0 - (0.45 * (1 - depth))
                : 0.42 + (0.58 * depth));

            context.DrawGeometry(SolidBrush(tint, alpha), null, BuildSplats(splats, rimOnly: false));

            if (rim)
            {
                var rimGeometry = BuildSplats(splats, rimOnly: true);
                if (!rimGeometry.IsEmpty())
                {
                    context.DrawGeometry(SolidBrush(tint, baseAlpha * 0.55), null, rimGeometry);
                }
            }
        }
    }

    private static StreamGeometry BuildSplats(List<Splat> splats, bool rimOnly)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var splat in splats)
            {
                if (rimOnly && splat.Rim < 0.68)
                {
                    continue;
                }

                var half = splat.Size / 2;
                ctx.BeginFigure(new Point(splat.X - half, splat.Y - half), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(splat.X + half, splat.Y - half), false, false);
                ctx.LineTo(new Point(splat.X + half, splat.Y + half), false, false);
                ctx.LineTo(new Point(splat.X - half, splat.Y + half), false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void ClearBuckets()
    {
        for (var i = 0; i < DepthBuckets; i++)
        {
            _buckets[i].Clear();
        }
    }

    private static SolidColorBrush SolidBrush(Color color, double alpha)
    {
        var brush = new SolidColorBrush(WithAlpha(color, Math.Clamp(alpha, 0, 1)));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

    private static Color Mix(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * t)),
        (byte)(from.G + ((to.G - from.G) * t)),
        (byte)(from.B + ((to.B - from.B) * t)));

    private readonly record struct Splat(double X, double Y, double Size, double Rim);

    private readonly record struct CorePoint(double X, double Y, double Z, double Radius, double Offset, double Period)
    {
        public static CorePoint OnSphere(Random random, double radius)
        {
            var theta = random.NextDouble() * 2 * Math.PI;
            var z = (random.NextDouble() * 2) - 1;
            var ring = Math.Sqrt(1 - (z * z));
            return new CorePoint(
                ring * Math.Cos(theta),
                ring * Math.Sin(theta),
                z,
                radius,
                random.NextDouble() * 10,
                5.5 + (random.NextDouble() * 7));
        }
    }

    private readonly record struct RingPoint(
        double Angle,
        double Base,
        double OutOfPlane,
        double Offset,
        double Period,
        double Hash);

    private readonly record struct Pulse(double Amplitude, double Period);

    private readonly record struct NubeProfile(
        Color Body,
        Color Depth,
        double Dispersion,
        double Wobble,
        double Yaw,
        double[] Spin,
        double Tilt,
        Pulse? Breath,
        Pulse? Tremor,
        bool IsBroken)
    {
        public static NubeProfile For(AssistantVisualState state) => state switch
        {
            AssistantVisualState.Listening => new(
                Rgb(0x72, 0xF0, 0xC0), Rgb(0x16, 0x6B, 0x54),
                1.06, 0.020, 0.10, [0.16, -0.13, 0.10], 1.75,
                null, new Pulse(0.020, 0.42), false),
            AssistantVisualState.Thinking => new(
                Rgb(0x9B, 0xB7, 0xFF), Rgb(0x30, 0x44, 0x86),
                1.02, 0.030, 0.34, [0.62, -0.50, 0.38], 0.95,
                null, null, false),
            AssistantVisualState.Speaking => new(
                Rgb(0xFF, 0xCE, 0x82), Rgb(0x7D, 0x54, 0x1C),
                1.05, 0.026, 0.14, [0.18, -0.15, 0.12], 0.22,
                null, null, false),
            AssistantVisualState.Attention => new(
                Rgb(0xFF, 0xB3, 0x47), Rgb(0x7D, 0x4D, 0x13),
                0.86, 0.012, 0.02, [0.02, -0.02, 0.01], 0.30,
                new Pulse(0.05, 1.6), null, false),
            AssistantVisualState.Error => new(
                Rgb(0xFF, 0x73, 0x85), Rgb(0x78, 0x26, 0x32),
                1.18, 0.055, 0.20, [0.30, -0.25, 0.20], 1.35,
                null, new Pulse(0.035, 0.19), true),
            _ => new(
                Rgb(0x72, 0xD9, 0xFF), Rgb(0x17, 0x60, 0x7F),
                1.00, 0.016, 0.055, [0.05, -0.04, 0.03], 1.0,
                null, null, false)
        };

        public static NubeProfile Lerp(NubeProfile start, NubeProfile target, double t) => target with
        {
            Body = LerpColor(start.Body, target.Body, t),
            Depth = LerpColor(start.Depth, target.Depth, t),
            Dispersion = start.Dispersion + ((target.Dispersion - start.Dispersion) * t),
            Wobble = start.Wobble + ((target.Wobble - start.Wobble) * t),
            Tilt = start.Tilt + ((target.Tilt - start.Tilt) * t)
        };

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Color LerpColor(Color start, Color target, double t) => Color.FromRgb(
            (byte)(start.R + ((target.R - start.R) * t)),
            (byte)(start.G + ((target.G - start.G) * t)),
            (byte)(start.B + ((target.B - start.B) * t)));
    }
}
