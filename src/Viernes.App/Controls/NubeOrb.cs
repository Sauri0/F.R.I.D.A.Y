using System.Windows;
using System.Windows.Media;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Controls;

/// <summary>
/// La nube. Un núcleo de puntos, tres anillos de círculo máximo con inclinaciones distintas, un
/// borde de flecos y una capa de polvo que orbita alrededor de todo eso.
/// </summary>
/// <remarks>
/// Acá <b>sí puede girar</b>, y es la mitad de su vocabulario: no hay reflejo especular que se
/// despegue del borde. Es justo lo que la Gota tiene prohibido.
/// <para>
/// Se dibuja acumulando los puntos de cada bucket de profundidad en dos <see cref="StreamGeometry"/>
/// —una de grano y una de resplandor— y pintando cada una de un saque. Con quinientas formas
/// sueltas por cuadro no alcanzaría; con veinticuatro geometrías sí. Es exactamente la advertencia
/// del boceto: <em>el polvo no son setecientos visuales</em>.
/// </para>
/// <para>
/// La fusión aditiva del boceto no existe en WPF y no se intenta emular: se aproxima con alfa
/// acumulada y con el halo central, que es una sola forma. A 108 px la diferencia no se lee.
/// </para>
/// </remarks>
internal sealed class NubeOrb : FrameworkElement, IOrbBody
{
    /// <summary>
    /// Puntos del cuerpo. Es la única perilla de presupuesto de render que hay que tocar.
    /// </summary>
    /// <remarks>
    /// El boceto usa 547 en el orbe chico. Acá van menos porque cada punto son dos figuras de
    /// geometría —grano y resplandor— en vez de un sprite ya horneado. Si alguna vez sobra máquina,
    /// este número sube y no hay nada más que cambiar.
    /// </remarks>
    private const int TotalPoints = 380;

    /// <summary>Granos de polvo. Van aparte porque son la capa más barata y la que más se nota.</summary>
    private const int DustPoints = 110;

    private const double CenterX = 35.0;
    private const double CenterY = 36.5;
    private const double Radius = 25.0;
    private const double ScaleX = 1.035;
    private const double ScaleY = 0.965;

    /// <summary>Inclinación de cámara: mirar la esfera algo desde arriba evita que los anillos se vean como rayas.</summary>
    private const double CameraTilt = 0.34;

    /// <summary>
    /// 30 cuadros por segundo. El boceto es explícito: 30 fps con estela se lee mejor que 60 sin ella.
    /// </summary>
    /// <remarks>
    /// Acá no hay estela —eso pide un <c>RenderTargetBitmap</c> redibujado sobre sí mismo y es lo
    /// primero que el propio boceto autoriza a resignar—, pero el techo se mantiene igual: a 108 px
    /// nadie distingue 30 de 60 en una nube, y la mitad de los cuadros es la mitad del consumo en
    /// una aplicación que está encendida todo el día.
    /// </remarks>
    private const double FrameSeconds = 1.0 / 30.0;

    private const int DepthBuckets = 6;

    private static readonly double[] RingTilts = [0.30, 0.72, 1.00];
    private static readonly double[] RingRolls = [0.0, 1.05, 2.09];

    private readonly CorePoint[] _core;
    private readonly RingPoint[][] _rings = new RingPoint[3][];
    private readonly CorePoint[] _fringe;
    private readonly DustPoint[] _dust;
    private readonly List<Splat>[] _buckets = new List<Splat>[DepthBuckets];
    private readonly List<Ripple> _ripples = [];
    private readonly OrbMoodClock _moods = new();

    /// <summary>Sólo para el destello de los nudos. No toca la forma, así que no rompe el determinismo del cuerpo.</summary>
    private readonly Random _flicker = new(20260817);

    private readonly double[] _ringPhase = new double[3];
    private readonly double[] _ringAngle = new double[3];

    private double _yaw;
    private double _pitch = CameraTilt;
    private double _clock;
    private double _wallClock;
    private double _pending;
    private long _lastTicks;
    private bool _isRunning;

    private OrbStateProfile _from = OrbPalette.For(AssistantVisualState.Idle);
    private OrbStateProfile _to = OrbPalette.For(AssistantVisualState.Idle);
    private double _transition = 1.0;
    private bool _capacityChange;

    /// <summary>Escala del cuerpo entero y su velocidad: un resorte, no una animación.</summary>
    private double _scale = 1.0;
    private double _scaleVelocity;

    /// <summary>Arrastre lateral que dejó el último empujón de un ánimo. Se apaga solo.</summary>
    private double _driftX;
    private double _driftY;

    private double _nextThinkRipple;
    private double _flareTime = double.NegativeInfinity;
    private double _nextFlare;
    private int _flareShell;
    private int _flareIndex = -1;

    private OrbMoodShape _mood = OrbMoodShape.Neutral;

    // Estado de dibujo del cuadro en curso. Vive en campos y no en parámetros porque lo consume
    // Push(), que se llama quinientas veces por cuadro y no puede recibir veinte argumentos.
    private double _originX;
    private double _originY;
    private double _squashY = 1;
    private double _shear;
    private OrbGlyphPoint[] _glyph = [];
    private double _glyphRadius;
    private double _glyphReveal;
    private double _glyphSeed;

    public NubeOrb()
    {
        // Semilla fija: la nube tiene que verse igual en cada arranque, no distinta cada vez.
        var random = new Random(20260816);

        var coreCount = (int)Math.Round(TotalPoints * 0.19);
        var ringCount = (int)Math.Round(TotalPoints * 0.255);
        var fringeCount = (int)Math.Round(TotalPoints * 0.08);

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
                    random.NextDouble(),
                    random.NextDouble());
            }
        }

        _fringe = new CorePoint[fringeCount];
        for (var i = 0; i < fringeCount; i++)
        {
            _fringe[i] = CorePoint.OnSphere(random, 1.02 + (random.NextDouble() * 0.16));
        }

        _dust = new DustPoint[DustPoints];
        for (var i = 0; i < DustPoints; i++)
        {
            _dust[i] = new DustPoint(
                random.NextDouble() * 2 * Math.PI,
                (random.NextDouble() - 0.5) * 2.6,
                0.42 + (Math.Pow(random.NextDouble(), 0.7) * 1.05),
                0.35 + (random.NextDouble() * 1.5),
                (random.NextDouble() - 0.5) * 0.22,
                random.NextDouble() * 10,
                2.2 + (random.NextDouble() * 5.5),
                0.34 + (random.NextDouble() * 0.62),
                random.NextDouble(),
                random.NextDouble(),
                0);
        }

        for (var i = 0; i < DepthBuckets; i++)
        {
            _buckets[i] = new List<Splat>(TotalPoints);
        }

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>El estado de fondo.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(NubeOrb),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

    /// <inheritdoc />
    public AssistantVisualState State
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

    /// <inheritdoc />
    public bool IsLightDesktop
    {
        get => (bool)GetValue(IsLightDesktopProperty);
        set => SetValue(IsLightDesktopProperty, value);
    }

    /// <summary>Modo madrugada, de 0 a 1.</summary>
    public static readonly DependencyProperty NightModeProperty = DependencyProperty.Register(
        nameof(NightMode),
        typeof(double),
        typeof(NubeOrb),
        new PropertyMetadata(0.0));

    /// <inheritdoc />
    public double NightMode
    {
        get => (double)GetValue(NightModeProperty);
        set => SetValue(NightModeProperty, value);
    }

    /// <inheritdoc />
    public void ShowMood(OrbMood mood) => _moods.Trigger(mood);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var orb = (NubeOrb)d;
        var next = (AssistantVisualState)e.NewValue;

        // Se interpola desde lo que se está viendo, no desde el estado anterior nominal: si el
        // cambio llega a mitad de una transición, no hay salto.
        orb._from = OrbPalette.Lerp(orb._from, orb._to, OrbPalette.SineInOut(orb._transition));
        orb._to = OrbPalette.For(next);
        orb._transition = 0;

        // Basta con que uno de los dos lados sea de capacidad: entrar y salir tardan lo mismo.
        orb._capacityChange = OrbPalette.IsCapacityState((AssistantVisualState)e.OldValue) ||
            OrbPalette.IsCapacityState(next);

        // Interrumpida entra de golpe: la transición arranca terminada y la escala se hunde. Si el
        // corte tardara medio segundo no se leería como obediencia sino como que siguió pensándolo.
        if (orb._to.IsSnap)
        {
            orb._transition = 1;
            orb._ripples.Add(new Ripple(orb._wallClock, -1.15));
            orb._scaleVelocity -= 1.5;
        }
        else
        {
            orb._ripples.Add(new Ripple(orb._wallClock, 1));
            orb._scaleVelocity += 1.15;
        }
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

        // Un cuadro perdido no puede empujar la fase varios segundos de golpe.
        delta = Math.Clamp(delta, 0, 0.09);
        _pending += delta;
        if (_pending < FrameSeconds)
        {
            return;
        }

        Advance(_pending);
        _pending = 0;
        InvalidateVisual();
    }

    private void Advance(double delta)
    {
        _wallClock += delta;

        var duration = (_capacityChange ? OrbPalette.CapacityTransition : OrbPalette.Transition).TotalSeconds;
        if (_transition < 1)
        {
            _transition = Math.Min(1, _transition + (delta / duration));
        }

        _mood = _moods.Advance(delta, OrbBody.Nube);
        if (_moods.TryTakeImpulse(out var impulse))
        {
            // El empujón del ánimo entra una sola vez y se apaga solo: el polvo se corre para un
            // lado y vuelve. Si entrara como posición se vería un corte de cinta.
            _ripples.Add(new Ripple(_wallClock, impulse.Ripple));
            _scaleVelocity += impulse.Scale;
            _driftX += impulse.KickX * 0.05;
            _driftY += impulse.KickY * 0.05;
            for (var i = 0; i < _dust.Length; i++)
            {
                _dust[i] = _dust[i] with { Surge = _dust[i].Surge + (impulse.KickRadial * 0.004) };
            }
        }

        var decay = Math.Exp(-delta * 7);
        _driftX *= decay;
        _driftY *= decay;

        var speed = OrbNight.Speed(NightMode);
        _clock += delta * speed;
        _yaw += delta * _to.Yaw * speed;
        _pitch = CameraTilt + (0.062 * Math.Sin(_clock * 0.19));

        // La escala es un resorte crítico: los golpes de los ánimos y de los cambios de estado
        // entran como velocidad y el resorte los devuelve solo.
        _scaleVelocity += ((170 * (1 - _scale)) - (15 * _scaleVelocity)) * delta;
        _scale += _scaleVelocity * delta;

        var dustSpeed = _from.DustSpeed + ((_to.DustSpeed - _from.DustSpeed) * OrbPalette.SineInOut(_transition));
        for (var i = 0; i < 3; i++)
        {
            _ringPhase[i] += delta * _to.Spin[i] * speed * 0.6;
            _ringAngle[i] += delta * _to.Spin[i] * speed * 3.0;
        }

        for (var i = 0; i < _dust.Length; i++)
        {
            var d = _dust[i];
            var surge = d.Surge * Math.Exp(-delta * 6);
            var angle = d.Angle + (delta * d.Speed * dustSpeed * speed * 1.1);
            var elevation = d.Elevation + (delta * d.ElevationSpeed * dustSpeed * speed);
            if (elevation > 1.5)
            {
                elevation = -1.5;
            }
            else if (elevation < -1.5)
            {
                elevation = 1.5;
            }

            _dust[i] = d with { Angle = angle, Elevation = elevation, Surge = surge };
        }

        if (_to.ThinkPeriod > 0 && _wallClock > _nextThinkRipple)
        {
            _ripples.Add(new Ripple(_wallClock, 0.40));
            _nextThinkRipple = _wallClock + _to.ThinkPeriod;
        }

        if (_wallClock > _nextFlare)
        {
            // Un nudo cualquiera de un anillo cualquiera destella. Es lo único aleatorio que queda
            // vivo en el dibujo, y es lo que impide que la nube se lea como un bucle.
            _flareShell = _flicker.Next(3);
            _flareIndex = _flicker.Next(_rings[0].Length);
            _flareTime = _wallClock;
            _nextFlare = _wallClock + 0.62 + (_flicker.NextDouble() * 1.5);
        }

        _ripples.RemoveAll(r => _wallClock - r.Start > 1.5);
    }

    /// <summary>Las ondas que salen del centro, sumadas para un radio dado.</summary>
    private double RippleAt(double radius)
    {
        var value = 0.0;
        foreach (var ripple in _ripples)
        {
            var tau = _wallClock - ripple.Start;
            if (tau > 1.5)
            {
                continue;
            }

            value += ripple.Amplitude * Math.Exp(-tau * 2.0) * Math.Sin(2 * Math.PI * ((radius * 1.25) - (tau * 2.15)));
        }

        return value * 0.072;
    }

    protected override void OnRender(DrawingContext context)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var eased = OrbPalette.SineInOut(_transition);
        var profile = OrbPalette.Lerp(_from, _to, eased);
        var light = IsLightDesktop;
        var night = Math.Clamp(NightMode, 0, 1);
        var seconds = _wallClock;

        var baseColor = OrbNight.Tint(light ? profile.Depth : profile.Body, night);
        var alpha = profile.Alpha * OrbNight.Alpha(night) * _mood.AlphaFactor;
        var coreColor = OrbPalette.Wash(baseColor, _mood.Tint, _mood.WashCore);
        var ringColor = OrbPalette.Wash(baseColor, _mood.Tint, _mood.WashRim);
        var dustColor = OrbPalette.Wash(baseColor, _mood.Tint, _mood.WashDust);

        _originX = CenterX + (profile.Lean * 25) + _mood.OffsetX;
        _originY = CenterY + (0.55 * Math.Sin(seconds / 3.7)) + _mood.OffsetY;
        _squashY = _mood.SquashY;
        _shear = _mood.Shear;

        _glyph = OrbGlyph.Points(_mood.Glyph);
        _glyphReveal = _mood.GlyphReveal;
        _glyphRadius = Radius * 1.5 * _mood.GlyphScale;

        // Los gestos propios del estado: respiración, temblor, golpeteo y estirón. Van multiplicados
        // por la transición para que no aparezcan de golpe al cambiar de estado.
        var extra = _mood.Extra;
        if (profile.Breath is { } breath)
        {
            extra += breath.Amplitude * Math.Sin(2 * Math.PI * seconds / breath.Period) * eased;
        }

        if (profile.Tremor > 0)
        {
            extra += profile.Tremor * Math.Sin(2 * Math.PI * seconds / 0.085) * eased;
        }

        if (profile.Knock is { } knock)
        {
            var u = (seconds % knock.Period) / knock.Period;
            if (u < 0.16)
            {
                extra += knock.Amplitude * Math.Sin(Math.PI * u / 0.16);
            }
            else if (u is > 0.22 and < 0.38)
            {
                extra += knock.Amplitude * 0.68 * Math.Sin(Math.PI * (u - 0.22) / 0.16);
            }
        }

        var reach = 0.0;
        if (profile.ReachPeriod > 0)
        {
            var u = (seconds % profile.ReachPeriod) / profile.ReachPeriod;
            reach = u < 0.46 ? 0.20 * Math.Sin(Math.PI * u / 0.46) : 0;
        }

        // Oído y boca son la misma idea con el signo cambiado: escuchar es que entre, hablar es que
        // salga. Por eso uno resta radio y el otro lo suma, y los dos suman actividad.
        var earIn = profile.Ear > 0
            ? profile.Ear * Math.Pow(Math.Abs(Math.Sin(seconds * 2.35) * Math.Sin((seconds * 0.73) + 0.4)), 1.25)
            : 0;
        var mouthOut = profile.Wave is not null
            ? (profile.Mouth > 0 ? profile.Mouth : 0.13) *
                Math.Pow(Math.Abs(Math.Sin(seconds * 3.05) * Math.Sin((seconds * 1.13) + 0.6)), 1.45)
            : 0;
        var sound = (mouthOut - earIn) * eased;
        var soundEnergy = (mouthOut + earIn) * eased;

        var dispersion = profile.Dispersion * _scale;
        var turbulence = profile.Turbulence * _mood.FlowFactor;
        var waveEnvelope = profile.Wave is not null
            ? 0.30 + (0.70 * Math.Abs(Math.Sin(seconds * 3.05) * Math.Sin((seconds * 1.13) + 0.6)))
            : 1;

        var scale = side / 70.0;
        context.PushTransform(new ScaleTransform(scale, scale));

        var pulse = Math.Min(1, Math.Abs(_scale - 1) * 11);
        DrawHalo(context, baseColor, light, alpha * (1 + (0.38 * pulse) + (1.1 * soundEnergy)));

        DrawDust(context, profile, dustColor, alpha, light, dispersion, extra, reach, sound, soundEnergy, turbulence, waveEnvelope, seconds);
        DrawCore(context, profile, coreColor, alpha, light, dispersion, extra, sound, turbulence, waveEnvelope, seconds);
        DrawRings(context, profile, ringColor, alpha, light, dispersion, extra, sound, waveEnvelope, seconds);
        DrawFringe(context, profile, dustColor, alpha, light, dispersion, extra, turbulence);

        context.Pop();
    }

    private void DrawHalo(DrawingContext context, Color tint, bool light, double strength)
    {
        // El halo da masa: sin él los puntos se desarman a 108 px. Es también lo que reemplaza al
        // resplandor horneado de cada grano, que en WPF no se puede pagar.
        var top = Math.Clamp((light ? 0.11 : 0.26) * strength, 0, 1);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.52),
            Center = new Point(0.5, 0.52),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            [
                new GradientStop(WithAlpha(tint, top), 0),
                new GradientStop(WithAlpha(tint, top * 0.4), 0.42),
                new GradientStop(WithAlpha(tint, 0), 0.72)
            ]
        };
        brush.Freeze();

        var span = Radius * 1.36;
        context.DrawEllipse(brush, null, new Point(_originX, _originY), span, span);
    }

    private void DrawDust(
        DrawingContext context,
        OrbStateProfile profile,
        Color tint,
        double alpha,
        bool light,
        double dispersion,
        double extra,
        double reach,
        double sound,
        double soundEnergy,
        double turbulence,
        double waveEnvelope,
        double seconds)
    {
        ClearBuckets();
        var yawCos = Math.Cos(_yaw);
        var yawSin = Math.Sin(_yaw);

        foreach (var grain in _dust)
        {
            var radius = grain.Radius * profile.DustRadius * dispersion *
                (1 + (extra * 1.2) + (0.09 * Math.Sin((2 * Math.PI * _clock / grain.Period) + grain.Offset)));
            var fade = 0.45 + (0.55 * Math.Abs(Math.Sin((2 * Math.PI * _clock / (grain.Period * 0.7)) + grain.Offset)));

            if (profile.IsBroken)
            {
                // En error el polvo sale disparado y se apaga: la nube pierde material.
                var u = ((_clock * 0.5) + (grain.Offset * 0.1)) % 1;
                radius *= 1 + (0.9 * u);
                fade *= 1 - (0.9 * u);
            }

            if (profile.Wave is { } wave)
            {
                radius *= 1 + (wave.Amplitude * waveEnvelope * 1.3 *
                    Math.Sin(2 * Math.PI * ((seconds / wave.Period) - (grain.Radius * 1.4))));
            }

            radius *= 1 + RippleAt(grain.Radius) + (sound * 1.7) + reach + grain.Surge;
            fade *= 1 + (soundEnergy * 2.4) - (reach * 1.8);

            if (_mood.AsymmetryY != 0)
            {
                radius *= 1 + (_mood.AsymmetryY * Math.Sin(grain.Elevation));
            }

            var elevationCos = Math.Cos(grain.Elevation);
            var x = elevationCos * Math.Cos(grain.Angle) * radius;
            var y = Math.Sin(grain.Elevation) * radius * 0.72;
            var z = elevationCos * Math.Sin(grain.Angle) * radius;
            (x, y, z) = Flow(x, y, z, grain.Radius, turbulence);

            var drop = _mood.DropY > 0 && grain.Delay > 0.58 ? (grain.Delay - 0.58) / 0.42 : 0;
            var sway = _mood.Sway * (0.25 + (0.75 * (0.5 - (0.5 * Math.Sin(grain.Elevation)))));
            var shimmer = _mood.ShimmerAmount > 0
                ? 1 + (_mood.ShimmerAmount * Math.Max(0, Math.Cos((x * 1.7) - _mood.ShimmerPhase)))
                : 1;

            _glyphSeed = grain.Seed;
            Push(
                (x * yawCos) + (z * yawSin),
                y,
                (-x * yawSin) + (z * yawCos),
                radius,
                grain.Size * fade * 1.5 * shimmer * (1 - (0.8 * drop)),
                rim: false,
                sway + _driftX,
                (_mood.DriftY * (0.35 + (0.65 * grain.Delay)) * 0.02) + (_mood.DropY * drop * 0.34) + _driftY);
        }

        Paint(context, light ? tint : Mix(tint, Colors.White, 0.18), (light ? 0.30 : 0.20) * alpha, light, rim: false, flat: false, glow: 3.3);
    }

    private void DrawCore(
        DrawingContext context,
        OrbStateProfile profile,
        Color tint,
        double alpha,
        bool light,
        double dispersion,
        double extra,
        double sound,
        double turbulence,
        double waveEnvelope,
        double seconds)
    {
        ClearBuckets();
        var yawCos = Math.Cos(_yaw);
        var yawSin = Math.Sin(_yaw);

        foreach (var point in _core)
        {
            var radius = point.Radius * dispersion *
                (1 + extra + (profile.Wobble * Math.Sin((2 * Math.PI * _clock / point.Period) + point.Offset)));

            if (profile.Wave is { } wave)
            {
                radius *= 1 + (wave.Amplitude * waveEnvelope *
                    Math.Sin(2 * Math.PI * ((seconds / wave.Period) - (point.Radius * 1.6))));
            }

            radius *= (1 + RippleAt(point.Radius) + (sound * 0.55)) * _mood.CoreRadius;

            var x = point.X * radius;
            var y = point.Y * radius;
            var z = point.Z * radius;
            (x, y, z) = Flow(x, y, z, point.Radius, turbulence);

            _glyphSeed = point.Seed;
            Push((x * yawCos) + (z * yawSin), y, (-x * yawSin) + (z * yawCos), radius, 0.85, rim: false, 0, 0);
        }

        Paint(context, tint, (light ? 0.46 : 0.34) * alpha, light, rim: false, flat: false, glow: 2.2);
    }

    private void DrawRings(
        DrawingContext context,
        OrbStateProfile profile,
        Color tint,
        double alpha,
        bool light,
        double dispersion,
        double extra,
        double sound,
        double waveEnvelope,
        double seconds)
    {
        ClearBuckets();
        var single = profile.IsSingleShell;
        var flare = Math.Max(0, 1 - ((_wallClock - _flareTime) / 0.42));

        for (var shell = 0; shell < 3; shell++)
        {
            // Sin capacidad los tres anillos colapsan en uno: no es un adorno menos, es el cuerpo
            // diciendo que le falta algo. Por eso gris y sin clave comparten este dibujo.
            var tilt = single
                ? 0
                : (RingTilts[shell] * profile.Tilt) + (0.09 * Math.Sin((_clock * 0.31) + (shell * 2.1))) + _mood.TiltBias;
            tilt = Math.Clamp(tilt, -0.95, 0.95);

            var relative = tilt - _pitch;
            var tiltCos = Math.Cos(relative);
            var tiltSin = Math.Sin(relative);

            var roll = single ? 0 : RingRolls[shell] + _ringPhase[shell] + (_mood.Roll * (shell == 1 ? -1 : 1));
            var rollCos = Math.Cos(roll);
            var rollSin = Math.Sin(roll);

            var points = _rings[shell];
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];

                // En error el anillo se rompe: un tramo de cada vuelta simplemente no se dibuja.
                if (profile.IsBroken && (point.Hash + (seconds * 0.12)) % 1 < 0.22)
                {
                    continue;
                }

                var radius = (single ? 0.95 : point.Base * profile.RingScale) * dispersion *
                    (1 + extra + (profile.Wobble * Math.Sin((2 * Math.PI * _clock / point.Period) + point.Offset)));

                if (profile.IsBroken)
                {
                    radius *= 1 + (0.14 * Math.Sin((2 * Math.PI * seconds / 0.21) + (point.Offset * 3.1)));
                }

                if (profile.Wave is { } wave)
                {
                    radius *= 1 + (wave.Amplitude * waveEnvelope *
                        Math.Sin(2 * Math.PI * ((seconds / wave.Period) - (point.Base * 1.6))));
                }

                radius *= (1 + RippleAt(point.Base) + (sound * 0.95)) * _mood.RingRadius;

                var angle = point.Angle + _ringAngle[shell] + (shell == 1 ? _mood.Desync : 0);
                var angleSin = Math.Sin(angle);
                var lx = Math.Cos(angle) * radius;
                var ly = (angleSin * radius * tiltCos) + (point.OutOfPlane * radius * tiltSin);
                var lz = (angleSin * radius * tiltSin) - (point.OutOfPlane * radius * tiltCos);

                var lit = shell == _flareShell && i == _flareIndex ? 1 + (1.8 * flare * _mood.FlareBoost) : 1;

                _glyphSeed = point.Seed;
                Push(lx, ly, lz, radius, 0.82 * lit, rim: true, 0, shell == 2 ? -_mood.RingLift : 0, rollCos, rollSin);
            }
        }

        var ringTint = light ? tint : Mix(tint, Colors.White, 0.24);
        Paint(context, ringTint, (light ? 0.82 : 0.70) * alpha * _mood.RingAlphaFactor, light, rim: true, flat: true, glow: 2.0);
    }

    private void DrawFringe(
        DrawingContext context,
        OrbStateProfile profile,
        Color tint,
        double alpha,
        bool light,
        double dispersion,
        double extra,
        double turbulence)
    {
        ClearBuckets();
        var yawCos = Math.Cos(_yaw);
        var yawSin = Math.Sin(_yaw);

        foreach (var point in _fringe)
        {
            // Los flecos rompen el círculo perfecto: sin ellos parece un planeta.
            var radius = point.Radius * dispersion *
                (1 + (extra * 1.3) + (profile.Wobble * 1.5 * Math.Sin((2 * Math.PI * _clock / point.Period) + point.Offset))) *
                (1 + RippleAt(point.Radius));

            var x = point.X * radius;
            var y = point.Y * radius;
            var z = point.Z * radius;
            (x, y, z) = Flow(x, y, z, point.Radius, turbulence);

            _glyphSeed = point.Seed;
            Push((x * yawCos) + (z * yawSin), y, (-x * yawSin) + (z * yawCos), radius, 0.66, rim: false, 0, 0);
        }

        Paint(context, light ? tint : Mix(tint, Colors.White, 0.36), (light ? 0.30 : 0.22) * alpha, light, rim: false, flat: false, glow: 2.6);
    }

    /// <summary>
    /// El campo que desordena todo. Es lo que separa una nube de una esfera de puntos.
    /// </summary>
    /// <remarks>
    /// Tres senos cruzados sobre las tres coordenadas: cada eje se desplaza con el producto de los
    /// otros dos. Es barato y no se repite, que son las dos condiciones que tenía que cumplir.
    /// </remarks>
    private (double X, double Y, double Z) Flow(double x, double y, double z, double radius, double turbulence)
    {
        var a1 = Math.Sin((x * 2.05) + (_clock * 0.88));
        var a2 = Math.Cos((y * 1.74) - (_clock * 0.68));
        var a3 = Math.Sin((z * 2.3) + (_clock * 0.5));
        var amount = 0.072 * turbulence * (0.55 + (0.45 * radius));
        return (x + (amount * a2 * a3), y + (amount * a3 * a1), z + (amount * a1 * a2));
    }

    /// <summary>Proyecta un punto, lo deforma con lo que pida el ánimo y lo guarda en su bucket.</summary>
    private void Push(
        double x,
        double y,
        double z,
        double radius,
        double size,
        bool rim,
        double offsetX,
        double offsetY,
        double rollCos = 1,
        double rollSin = 0)
    {
        var pitchCos = Math.Cos(_pitch);
        var pitchSin = Math.Sin(_pitch);
        var projectedY = (y * pitchCos) - (z * pitchSin);
        var projectedZ = (y * pitchSin) + (z * pitchCos);

        var span = radius <= 0 ? 1 : radius;
        var depth = Math.Clamp(((projectedZ / span) + 1) / 2, 0, 1);

        var dx = (x * Radius * ScaleX) + offsetX;
        var dy = (projectedY * Radius * ScaleY) + offsetY;

        if (rollSin != 0 || rollCos != 1)
        {
            var rotated = (dx * rollCos) - (dy * rollSin);
            dy = (dx * rollSin) + (dy * rollCos);
            dx = rotated;
        }

        if (_squashY != 1)
        {
            dy *= _squashY;
            dx *= 1 + ((1 - _squashY) * 0.55);
        }

        if (_shear != 0)
        {
            dx += dy * _shear;
        }

        if (_glyphReveal > 0.004 && _glyph.Length > 0)
        {
            (dx, dy) = TowardGlyph(dx, dy);
        }

        // Sin la guarda del lienzo del boceto, el polvo del error se saldría del control.
        (dx, dy) = OrbBounds.SoftLimit(dx, dy);

        var bucket = Math.Clamp((int)(depth * DepthBuckets), 0, DepthBuckets - 1);

        // Los puntos del canto —donde la esfera se dobla— son los que dibujan la silueta solos.
        var rimStrength = rim ? 1 - Math.Abs(projectedZ / span) : 0;

        _buckets[bucket].Add(new Splat(
            _originX + dx,
            _originY + dy,
            size * (0.7 + (0.62 * depth)),
            rimStrength));
    }

    /// <summary>
    /// Lleva un punto del cuerpo al punto del signo que le queda más cerca.
    /// </summary>
    /// <remarks>
    /// No es una interpolación derecha: cada grano viaja por una curva cuadrática con el control
    /// corrido en perpendicular, así que el enjambre se enrosca en vez de colapsar. Y el barrido va
    /// escalonado a lo largo del signo —el índice del punto entra en el cálculo— para que el trazo
    /// se escriba de una punta a la otra en lugar de aparecer entero.
    /// </remarks>
    private (double X, double Y) TowardGlyph(double dx, double dy)
    {
        var count = _glyph.Length;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var ddx = (_glyph[i].X * _glyphRadius) - dx;
            var ddy = (_glyph[i].Y * _glyphRadius) - dy;
            var d2 = (ddx * ddx) + (ddy * ddy);
            if (d2 < bestDistance)
            {
                bestDistance = d2;
                best = i;
            }
        }

        // El corrimiento por semilla reparte los granos que eligieron el mismo destino.
        var index = (best + (int)(_glyphSeed * 1.99)) % count;
        var target = _glyph[index];

        var smooth = _glyphReveal * _glyphReveal * (3 - (2 * _glyphReveal));
        var raw = Math.Clamp(((smooth * 1.40) - ((double)index / count * 0.46)) / 0.48, 0, 1);
        var progress = raw * raw * (3 - (2 * raw));
        if (progress <= 0.001)
        {
            return (dx, dy);
        }

        var e = 1 - Math.Pow(1 - progress, 2.0);
        var om = 1 - e;

        // El temblor por grano es lo que le da grosor al trazo: sin él el signo sería una línea de
        // un punto de ancho y se leería como un alambre, no como algo escrito.
        var jitterX = Math.Sin(_glyphSeed * 97.3) * target.Radius * _glyphRadius * 0.9;
        var jitterY = Math.Cos(_glyphSeed * 61.7) * target.Radius * _glyphRadius * 0.9;
        var tx = (target.X * _glyphRadius) + jitterX;
        var ty = (target.Y * _glyphRadius) + jitterY;

        var mx = ((dx + tx) / 2) - ((ty - dy) * 0.20);
        var my = ((dy + ty) / 2) + ((tx - dx) * 0.20);

        return (
            (om * om * dx) + (2 * om * e * mx) + (e * e * tx),
            (om * om * dy) + (2 * om * e * my) + (e * e * ty));
    }

    private void Paint(
        DrawingContext context,
        Color tint,
        double baseAlpha,
        bool light,
        bool rim,
        bool flat,
        double glow)
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
                ? (flat ? 1.0 - (0.18 * (1 - depth)) : 1.0 - (0.5 * (1 - depth)))
                : (flat ? 0.76 + (0.24 * depth) : 0.42 + (0.58 * depth)));

            // Dos pasadas: el resplandor grande y translúcido, y el grano encima. Es la aproximación
            // del sprite con el halo horneado del boceto, que en WPF costaría un DrawImage por grano.
            context.DrawGeometry(SolidBrush(tint, alpha * 0.22), null, BuildSplats(splats, glow, rimOnly: false));
            context.DrawGeometry(SolidBrush(tint, alpha), null, BuildSplats(splats, 1, rimOnly: false));

            if (rim)
            {
                var rimGeometry = BuildSplats(splats, 1.25, rimOnly: true);
                if (!rimGeometry.IsEmpty())
                {
                    context.DrawGeometry(SolidBrush(tint, baseAlpha * 0.55), null, rimGeometry);
                }
            }
        }
    }

    private static StreamGeometry BuildSplats(List<Splat> splats, double scale, bool rimOnly)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var splat in splats)
            {
                if (rimOnly && splat.Rim < 0.72)
                {
                    continue;
                }

                var r = splat.Size * scale / 2;
                if (r <= 0)
                {
                    continue;
                }

                // Un círculo con dos arcos: dos segmentos por grano y ningún objeto intermedio.
                ctx.BeginFigure(new Point(splat.X - r, splat.Y), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(splat.X + r, splat.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                ctx.ArcTo(new Point(splat.X - r, splat.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
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

    private static Color Mix(Color from, Color to, double t) => OrbPalette.LerpColor(from, to, t);

    private readonly record struct Splat(double X, double Y, double Size, double Rim);

    private readonly record struct Ripple(double Start, double Amplitude);

    private readonly record struct CorePoint(
        double X,
        double Y,
        double Z,
        double Radius,
        double Offset,
        double Period,
        double Seed)
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
                5.5 + (random.NextDouble() * 7),
                random.NextDouble());
        }
    }

    private readonly record struct RingPoint(
        double Angle,
        double Base,
        double OutOfPlane,
        double Offset,
        double Period,
        double Hash,
        double Seed);

    /// <summary>
    /// Un grano de polvo. <c>Surge</c> es el empujón radial que dejó el último ánimo y se apaga solo.
    /// </summary>
    private readonly record struct DustPoint(
        double Angle,
        double Elevation,
        double Radius,
        double Speed,
        double ElevationSpeed,
        double Offset,
        double Period,
        double Size,
        double Delay,
        double Seed,
        double Surge);
}
