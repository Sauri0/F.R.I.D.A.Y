using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Viernes.App.Controls;

/// <summary>
/// La gota. Ocho puntos sobre una elipse, cada uno oscilando con su propio período, unidos con
/// tangentes Catmull-Rom y reconstruidos cuadro a cuadro.
/// </summary>
/// <remarks>
/// Dos reglas que se pagaron caro y no conviene volver a probar: <b>deformar más no la hace más
/// líquida</b> —una gota en reposo es casi esférica y lo que la vuelve agua es la luz—, y <b>nada
/// rota</b>, porque girar un cuerpo ovoide barre la silueta y despega los reflejos.
/// </remarks>
internal partial class LiquidOrb : UserControl
{
    private const int Points = 8;
    private const double Radius = 25.0;
    private const double CenterX = 35.0;
    private const double CenterY = 36.5;
    private const double ScaleX = 1.035;
    private const double ScaleY = 0.965;

    /// <summary>Tangente Catmull-Rom. A ocho puntos el error contra el círculo es menor al 0,1 %.</summary>
    private const double Tangent = 0.1875;

    /// <summary>Períodos deliberadamente no conmensurables: el conjunto nunca vuelve a alinearse.</summary>
    private static readonly double[] Periods = [6.3, 7.1, 8.7, 7.9, 9.3, 6.7, 8.1, 10.3];
    private static readonly double[] Phases = [0, 1.7, 3.1, 4.6, 2.2, 5.4, 0.9, 3.8];

    private static readonly TimeSpan StateTransition = TimeSpan.FromMilliseconds(320);

    private readonly Point[] _points = new Point[Points];
    private StateProfile _from = StateProfile.For(AssistantVisualState.Idle);
    private StateProfile _to = StateProfile.For(AssistantVisualState.Idle);
    private double _transition = 1.0;
    private double _phase;
    private double _clock;
    private double _levelSmoothed;
    private long _lastTicks;
    private bool _isRunning;

    public LiquidOrb()
    {
        InitializeComponent();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(LiquidOrb),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

    internal AssistantVisualState State
    {
        get => (AssistantVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// Micrófono armado pero sin capturar. No dibuja geometría nueva: tiñe de verde el rebote que
    /// ya existía, porque a 108 px cualquier anillo compite con el borde oscuro que da densidad.
    /// </summary>
    public static readonly DependencyProperty IsMicrophoneArmedProperty = DependencyProperty.Register(
        nameof(IsMicrophoneArmed),
        typeof(bool),
        typeof(LiquidOrb),
        new PropertyMetadata(false));

    internal bool IsMicrophoneArmed
    {
        get => (bool)GetValue(IsMicrophoneArmedProperty);
        set => SetValue(IsMicrophoneArmedProperty, value);
    }

    /// <summary>Conservada por compatibilidad con el enlace del shell.</summary>
    public static readonly DependencyProperty IsMicrophoneActiveProperty = DependencyProperty.Register(
        nameof(IsMicrophoneActive),
        typeof(bool),
        typeof(LiquidOrb),
        new PropertyMetadata(false));

    internal bool IsMicrophoneActive
    {
        get => (bool)GetValue(IsMicrophoneActiveProperty);
        set => SetValue(IsMicrophoneActiveProperty, value);
    }

    /// <summary>
    /// Nivel del micrófono, de 0 a 1. Mientras escucha, la gota crece con tu voz.
    /// </summary>
    /// <remarks>
    /// Es lo que vuelve inequívoco el «te escucho»: un color fijo no distingue atender de estar
    /// colgado, pero una forma que se mueve cuando hablás sí. Es la misma señal que usa la burbuja
    /// de ChatGPT y la razón por la que ahí nunca dudás de si te está oyendo.
    /// </remarks>
    public static readonly DependencyProperty AudioLevelProperty = DependencyProperty.Register(
        nameof(AudioLevel),
        typeof(double),
        typeof(LiquidOrb),
        new PropertyMetadata(0.0));

    internal double AudioLevel
    {
        get => (double)GetValue(AudioLevelProperty);
        set => SetValue(AudioLevelProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var orb = (LiquidOrb)d;

        // Se interpola desde lo que se está viendo, no desde el estado anterior nominal: si el
        // cambio llega a mitad de una transición, no hay salto.
        orb._from = StateProfile.Lerp(orb._from, orb._to, orb._transition);
        orb._to = StateProfile.For((AssistantVisualState)e.NewValue);
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

        // Un cuadro perdido no puede empujar la fase varios segundos de golpe.
        delta = Math.Clamp(delta, 0, 0.1);
        _clock += delta;

        if (_transition < 1)
        {
            _transition = Math.Min(1, _transition + (delta / StateTransition.TotalSeconds));
        }

        var eased = SineInOut(_transition);
        var current = StateProfile.Lerp(_from, _to, eased);

        // La viscosidad es velocidad del mismo fluido: acelera el reloj de la ondulación, no la amplitud.
        _phase += delta * current.Viscosity;

        ApplyGeometry(current);
        ApplyPalette(current);
    }

    private void ApplyGeometry(StateProfile profile)
    {
        var (bias, excursionFactor) = profile.Character.Evaluate(_clock);
        var excursion = profile.Excursion * excursionFactor;

        // La voz del usuario entra como crecimiento del radio, no como agitación del contorno:
        // hablarle más fuerte la hincha, y eso se lee de inmediato como «me está oyendo».
        // El seguimiento es asimétrico —sube rápido, baja lento— porque una caída instantánea
        // parpadea con cada sílaba en vez de acompañar la frase.
        var target = State == AssistantVisualState.Listening ? Math.Clamp(AudioLevel * 7.0, 0, 1) : 0;
        _levelSmoothed += (target - _levelSmoothed) * (target > _levelSmoothed ? 0.45 : 0.08);
        bias += _levelSmoothed * 3.2;

        for (var i = 0; i < Points; i++)
        {
            var angle = (-Math.PI / 2) + (i * 2 * Math.PI / Points);
            var radius = Radius + bias + (excursion * Math.Sin((2 * Math.PI * _phase / Periods[i]) + Phases[i]));
            _points[i] = new Point(
                CenterX + (radius * ScaleX * Math.Cos(angle)),
                CenterY + (radius * ScaleY * Math.Sin(angle)));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(_points[0], isFilled: true, isClosed: true);
            for (var i = 0; i < Points; i++)
            {
                var previous = _points[(i - 1 + Points) % Points];
                var start = _points[i];
                var end = _points[(i + 1) % Points];
                var next = _points[(i + 2) % Points];

                context.BezierTo(
                    new Point(
                        start.X + ((end.X - previous.X) * Tangent),
                        start.Y + ((end.Y - previous.Y) * Tangent)),
                    new Point(
                        end.X - ((next.X - start.X) * Tangent),
                        end.Y - ((next.Y - start.Y) * Tangent)),
                    end,
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        Mass.Data = geometry;

        // Recortar los reflejos contra la silueta es lo que hace que la luz resbale por el borde.
        Reflections.Clip = geometry;
    }

    private void ApplyPalette(StateProfile profile)
    {
        StopLight.Color = Lighten(profile.Body, 0.26);
        StopBody.Color = profile.Body;
        StopDeep.Color = profile.Depth;
        StopRim.Color = profile.Rim;
        MassGlow.Color = profile.Body;

        // Armado: verde sobre la luz que ya existe, en vez de geometría nueva.
        var bounce = IsMicrophoneArmed && State == AssistantVisualState.Idle
            ? Color.FromRgb(0x72, 0xF0, 0xC0)
            : profile.Body;
        var strength = IsMicrophoneArmed && State == AssistantVisualState.Idle ? 0x4D : 0x57;
        BounceCore.Color = Color.FromArgb((byte)strength, bounce.R, bounce.G, bounce.B);
        BounceEdge.Color = Color.FromArgb(0x00, bounce.R, bounce.G, bounce.B);
    }

    private static double SineInOut(double t) => 0.5 - (Math.Cos(Math.PI * Math.Clamp(t, 0, 1)) / 2);

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + ((255 - color.R) * amount)),
        (byte)(color.G + ((255 - color.G) * amount)),
        (byte)(color.B + ((255 - color.B) * amount)));

    /// <summary>
    /// El término propio de cada estado. La velocidad sola no alcanzaba para distinguirlos: cada uno
    /// suma su carácter sobre el radio, en tiempo de reloj y no acumulado.
    /// </summary>
    private readonly record struct CharacterTerm(CharacterKind Kind, double Amplitude, double Period)
    {
        public static CharacterTerm None => new(CharacterKind.None, 0, 1);

        /// <summary>Devuelve el corrimiento de radio y el factor que multiplica la excursión.</summary>
        public (double Bias, double ExcursionFactor) Evaluate(double clock) => Kind switch
        {
            // Temblor y respiración mueven el radio entero: la masa crece bajo la misma luz.
            CharacterKind.Tremor or CharacterKind.Breath =>
                (Amplitude * Math.Sin(2 * Math.PI * clock / Period), 1.0),

            // La envolvente silábica modula cuánto ondula, entre el 52 % y el 100 %.
            CharacterKind.Syllabic =>
                (0.0, 0.52 + (0.48 * (0.5 + (0.5 * Math.Sin(2 * Math.PI * clock / Period))))),

            _ => (0.0, 1.0)
        };

        public static CharacterTerm Lerp(CharacterTerm start, CharacterTerm target, double t)
        {
            // Con el mismo carácter, la amplitud se interpola. Con caracteres distintos no hay mezcla
            // posible —un temblor no es medio una respiración—, así que se cambia a mitad de camino.
            if (start.Kind == target.Kind)
            {
                return target with { Amplitude = start.Amplitude + ((target.Amplitude - start.Amplitude) * t) };
            }

            return t >= 0.5 ? target : start;
        }
    }

    private enum CharacterKind
    {
        None,
        Tremor,
        Breath,
        Syllabic
    }

    private readonly record struct StateProfile(
        Color Body,
        Color Depth,
        Color Rim,
        double Viscosity,
        double Excursion,
        CharacterTerm Character)
    {
        public static StateProfile For(AssistantVisualState state) => state switch
        {
            AssistantVisualState.Listening => new(
                Rgb(0x72, 0xF0, 0xC0), Rgb(0x16, 0x6B, 0x54), Rgb(0x07, 0x36, 0x2A),
                2.6, 2.8, new CharacterTerm(CharacterKind.Tremor, 0.5, 0.42)),
            AssistantVisualState.Thinking => new(
                Rgb(0x9B, 0xB7, 0xFF), Rgb(0x30, 0x44, 0x86), Rgb(0x15, 0x1E, 0x45),
                3.0, 3.6, new CharacterTerm(CharacterKind.Breath, 1.0, 2.4)),
            AssistantVisualState.Speaking => new(
                Rgb(0xFF, 0xCE, 0x82), Rgb(0x7D, 0x54, 0x1C), Rgb(0x3E, 0x28, 0x08),
                4.0, 4.3, new CharacterTerm(CharacterKind.Syllabic, 0, 0.34)),
            AssistantVisualState.Attention => new(
                Rgb(0xFF, 0xB3, 0x47), Rgb(0x7D, 0x4D, 0x13), Rgb(0x3E, 0x23, 0x04),
                1.7, 2.0, new CharacterTerm(CharacterKind.Breath, 1.6, 1.6)),
            AssistantVisualState.Error => new(
                Rgb(0xFF, 0x73, 0x85), Rgb(0x78, 0x26, 0x32), Rgb(0x3B, 0x0F, 0x17),
                2.2, 2.5, new CharacterTerm(CharacterKind.Tremor, 0.65, 0.28)),
            _ => new(
                Rgb(0x72, 0xD9, 0xFF), Rgb(0x17, 0x60, 0x7F), Rgb(0x08, 0x31, 0x4A),
                1.0, 1.7, CharacterTerm.None)
        };

        public static StateProfile Lerp(StateProfile start, StateProfile target, double t) => new(
            LerpColor(start.Body, target.Body, t),
            LerpColor(start.Depth, target.Depth, t),
            LerpColor(start.Rim, target.Rim, t),
            start.Viscosity + ((target.Viscosity - start.Viscosity) * t),
            start.Excursion + ((target.Excursion - start.Excursion) * t),
            CharacterTerm.Lerp(start.Character, target.Character, t));

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Color LerpColor(Color start, Color target, double t) => Color.FromRgb(
            (byte)(start.R + ((target.R - start.R) * t)),
            (byte)(start.G + ((target.G - start.G) * t)),
            (byte)(start.B + ((target.B - start.B) * t)));
    }
}
