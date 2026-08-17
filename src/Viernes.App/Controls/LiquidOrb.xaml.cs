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

    /// <summary>
    /// Entrar o salir de capacidad reducida tarda casi el triple que un cambio de estado normal.
    /// </summary>
    /// <remarks>
    /// Perder la clave o la red no es un evento: es una condición. A 320 ms se leería como que algo
    /// acaba de pasar, y lo que hay que comunicar es que algo <em>está</em> distinto. La misma
    /// lentitud al volver es, por sí sola, el aviso de que la capacidad se recuperó.
    /// </remarks>
    private static readonly TimeSpan CapacityTransition = TimeSpan.FromMilliseconds(900);

    private static bool IsCapacityState(AssistantVisualState state) =>
        state is AssistantVisualState.Unconfigured or AssistantVisualState.Offline;

    /// <summary>
    /// Los tres tramos de una tarea larga. El orbe informa <em>modo</em>, no tiempo, y por eso el
    /// tiempo hay que contarlo con otra cosa.
    /// </summary>
    /// <remarks>
    /// Acelerar no sirve: a los tres segundos nadie distingue ×3,4 de ×3,4, y arriba de eso el ojo
    /// deja de medir y sólo se cansa. Por eso a partir del segundo tramo la viscosidad <b>baja</b>.
    /// Es contraintuitivo y es lo correcto: una tarea larga no es más urgente, es más larga. Lo que
    /// sube es la cantidad de información —aparece el sedimento—, no la agitación.
    /// </remarks>
    private const double ShortTaskSeconds = 1.2;
    private const double LongTaskSeconds = 10;

    /// <summary>Techo del sedimento. Llenar la gota prometería un final que nadie puede calcular.</summary>
    private const double SedimentCeiling = 0.34;

    /// <summary>Sube 1 px cada 1500 ms sobre 108 px de alto.</summary>
    private const double SedimentRisePerSecond = 1.0 / 1.5 / 108;

    private readonly Point[] _points = new Point[Points];
    private StateProfile _from = StateProfile.For(AssistantVisualState.Idle);
    private StateProfile _to = StateProfile.For(AssistantVisualState.Idle);
    private double _transition = 1.0;
    private double _phase;
    private double _clock;
    private double _levelSmoothed;
    private bool _capacityChange;

    /// <summary>Segundos que lleva la tarea en curso. Cero cuando no hay ninguna.</summary>
    private double _taskElapsed;

    /// <summary>Progreso del latido de un paso, de 1 a 0. Fuera de un latido vale 0.</summary>
    private double _beat;
    private bool _beatRising;
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

    /// <summary>Hay autorización de gasto viva. Tiñe el borde de cálido mientras dure.</summary>
    public static readonly DependencyProperty HasSpendAuthorizationProperty = DependencyProperty.Register(
        nameof(HasSpendAuthorization),
        typeof(bool),
        typeof(LiquidOrb),
        new PropertyMetadata(false));

    internal bool HasSpendAuthorization
    {
        get => (bool)GetValue(HasSpendAuthorizationProperty);
        set => SetValue(HasSpendAuthorizationProperty, value);
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

        // Basta con que uno de los dos lados sea de capacidad: entrar y salir tardan lo mismo.
        orb._capacityChange = IsCapacityState((AssistantVisualState)e.OldValue) ||
            IsCapacityState((AssistantVisualState)e.NewValue);
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
            var duration = _capacityChange ? CapacityTransition : StateTransition;
            _transition = Math.Min(1, _transition + (delta / duration.TotalSeconds));
        }

        var eased = SineInOut(_transition);
        var current = StateProfile.Lerp(_from, _to, eased);

        current = ApplyTaskPacing(current, delta);
        AdvanceBeat(delta);

        // La viscosidad es velocidad del mismo fluido: acelera el reloj de la ondulación, no la amplitud.
        _phase += delta * current.Viscosity;

        ApplyGeometry(current);
        ApplyPalette(current);
    }

    /// <summary>
    /// Ajusta el ritmo según cuánto lleva la tarea, y devuelve el perfil ya corregido.
    /// </summary>
    private StateProfile ApplyTaskPacing(StateProfile profile, double delta)
    {
        if (State != AssistantVisualState.Thinking)
        {
            _taskElapsed = 0;
            Sediment.Height = 0;
            return profile;
        }

        _taskElapsed += delta;

        // Tramo 0: sólo color. Las tareas instantáneas no merecen cromo.
        if (_taskElapsed < ShortTaskSeconds)
        {
            Sediment.Height = 0;
            return profile;
        }

        // Tramo 1: acelera. Tramo 2: desacelera y empieza a contar con sedimento.
        var viscosity = 3.4;
        if (_taskElapsed > LongTaskSeconds)
        {
            var into = Math.Min(1, (_taskElapsed - LongTaskSeconds) / 3.0);
            viscosity = 3.4 + ((2.0 - 3.4) * SineInOut(into));

            var level = Math.Min(SedimentCeiling, (_taskElapsed - LongTaskSeconds) * SedimentRisePerSecond);
            Sediment.Height = level * 70;
            System.Windows.Controls.Canvas.SetTop(Sediment, 70 - (level * 70));
        }

        return profile with { Viscosity = viscosity };
    }

    /// <summary>
    /// Un paso real del orquestador = un latido. Es la única señal honesta de que algo pasó: nada
    /// continuo puede decir «avancé», sólo «sigo».
    /// </summary>
    public void Beat()
    {
        _beat = 0;
        _beatRising = true;
    }

    private void AdvanceBeat(double delta)
    {
        if (_beatRising)
        {
            // Sube en 160 ms y vuelve en 300: la asimetría es lo que hace que se lea como un golpe
            // de masa y no como una respiración.
            _beat = Math.Min(1, _beat + (delta / 0.160));
            if (_beat >= 1)
            {
                _beatRising = false;
            }

            return;
        }

        if (_beat > 0)
        {
            _beat = Math.Max(0, _beat - (delta / 0.300));
        }
    }

    private void ApplyGeometry(StateProfile profile)
    {
        var (bias, excursionFactor) = profile.Character.Evaluate(_clock);
        var excursion = profile.Excursion * excursionFactor;

        // La voz del usuario entra como crecimiento del radio, no como agitación del contorno:
        // hablarle más fuerte la hincha, y eso se lee de inmediato como «me está oyendo».
        // El seguimiento es asimétrico —sube rápido, baja lento— porque una caída instantánea
        // parpadea con cada sílaba en vez de acompañar la frase.
        var target = State == AssistantVisualState.Listening ? Math.Clamp(AudioLevel, 0, 1) : 0;
        _levelSmoothed += (target - _levelSmoothed) * (target > _levelSmoothed ? 0.45 : 0.08);
        bias += _levelSmoothed * 3.2;

        // El latido crece la masa entera bajo la misma luz —los reflejos están fuera de esta cuenta—,
        // y eso es exactamente lo que se lee como líquido en vez de como un objeto que se infla.
        bias += _beat * 1.2;

        var (scaleX, scaleY, shiftX) = BodyTransform(profile.Mode, _clock);

        for (var i = 0; i < Points; i++)
        {
            var angle = (-Math.PI / 2) + (i * 2 * Math.PI / Points);

            // Verticalidad del punto: +1 arriba, −1 abajo. Es lo que permite deformar asimétrico
            // sin rotar nada, que es la regla que sostiene los reflejos quietos.
            var verticality = -Math.Sin(angle);
            var wave = Deform(profile.Mode, i, verticality, _phase, _clock);
            var radius = Radius + bias + PointBias(profile.Mode, verticality, _clock) + (excursion * wave);

            _points[i] = new Point(
                CenterX + (radius * ScaleX * scaleX * Math.Cos(angle)) + shiftX,
                CenterY + (radius * ScaleY * scaleY * Math.Sin(angle)));
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

        // El sedimento usa el mismo recorte: es líquido dentro de la gota, no una banda encima.
        SedimentLayer.Clip = geometry;
    }

    private void ApplyPalette(StateProfile profile)
    {
        StopLight.Color = Lighten(profile.Body, 0.26);
        StopBody.Color = profile.Body;
        StopDeep.Color = profile.Depth;
        StopRim.Color = profile.Rim;
        MassGlow.Color = profile.Body;

        // El sedimento es el color de profundidad al 55 %: pertenece al mismo líquido, más denso.
        Sediment.Fill = new SolidColorBrush(
            Color.FromArgb(0x8C, profile.Depth.R, profile.Depth.G, profile.Depth.B));

        // Mientras hay autorización de gasto viva, el borde queda más cálido todo el día.
        //
        // No es un distintivo encima de la gota: es el propio stop de borde mezclado con ámbar. Se
        // nota sin gritar, y desaparece al revocar o al reiniciar —que es justamente la garantía:
        // la autorización vive en memoria y muere con el proceso, así que el aviso también.
        if (HasSpendAuthorization)
        {
            StopRim.Color = Mix(profile.Rim, Rgb(0x3E, 0x23, 0x04), 0.40);
        }

        // Durante el latido el borde se aclara: el golpe de masa se acompaña con luz en el contorno.
        if (_beat > 0)
        {
            StopRim.Color = Lighten(StopRim.Color, 0.18 * _beat);
        }

        // Armado: verde sobre la luz que ya existe, en vez de geometría nueva.
        var bounce = IsMicrophoneArmed && State == AssistantVisualState.Idle
            ? Color.FromRgb(0x72, 0xF0, 0xC0)
            : profile.Body;
        var strength = IsMicrophoneArmed && State == AssistantVisualState.Idle ? 0x4D : 0x57;
        BounceCore.Color = Color.FromArgb((byte)strength, bounce.R, bounce.G, bounce.B);
        BounceEdge.Color = Color.FromArgb(0x00, bounce.R, bounce.G, bounce.B);
    }

    private static double SineInOut(double t) => 0.5 - (Math.Cos(Math.PI * Math.Clamp(t, 0, 1)) / 2);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Mezcla dos colores. Se usa para teñir el borde sin reemplazar la paleta del estado.</summary>
    private static Color Mix(Color start, Color target, double amount) => Color.FromRgb(
        (byte)(start.R + ((target.R - start.R) * amount)),
        (byte)(start.G + ((target.G - start.G) * amount)),
        (byte)(start.B + ((target.B - start.B) * amount)));

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + ((255 - color.R) * amount)),
        (byte)(color.G + ((255 - color.G) * amount)),
        (byte)(color.B + ((255 - color.B) * amount)));

    /// <summary>
    /// El término propio de cada estado. La velocidad sola no alcanzaba para distinguirlos: cada uno
    /// suma su carácter sobre el radio, en tiempo de reloj y no acumulado.
    /// </summary>
    /// <summary>
    /// Cuánto ondula cada punto, según el modo del estado.
    /// </summary>
    /// <remarks>
    /// Es lo que faltaba para que los estados se distingan de verdad. Antes los seis usaban el mismo
    /// dibujo de movimiento y sólo cambiaban amplitud y velocidad; a los tres segundos el ojo deja de
    /// medir velocidad y todos se parecen. Ahora cada estado deforma distinto, que es una diferencia
    /// que se lee sin comparar contra nada.
    /// </remarks>
    private static double Deform(
        DeformationMode mode,
        int index,
        double verticality,
        double phase,
        double clock) => mode switch
    {
        // Escuchando: la gota se estira hacia el sonido. Arriba 1,55× y abajo 0,5×.
        DeformationMode.Listening =>
            Math.Sin((2 * Math.PI * phase / Periods[index]) + Phases[index]) *
            (0.5 + (1.05 * ((verticality + 1) / 2))),

        // Pensando: una onda que recorre el contorno. Período común y fase corrida por punto, así
        // que circula sin rotar — los reflejos, que están fuera de la masa, siguen quietos.
        DeformationMode.Thinking =>
            Math.Sin((2 * Math.PI * phase / 4.2) + (index * 1.34)),

        // Hablando: pares afuera, impares adentro, alternando cada 340 ms — pero mezclado con la
        // ondulación de base, no en lugar de ella.
        //
        // Con la alternancia sola y a excursión completa, ocho puntos que van uno sí y uno no dan un
        // cuadrilátero redondeado: se ve en el render, y una gota que se vuelve cuadrada rompe el
        // vocabulario de formas que sostiene todo el sistema. Repartido 55/45 la sílaba se lee y la
        // silueta sigue siendo la misma.
        // La alternancia además va corrida por punto: si los ocho la reciben en el mismo instante, la
        // simetría resultante es de orden cuatro, y una simetría de orden cuatro es un cuadrado, sin
        // importar cuán chica sea la amplitud. Con el corrimiento la sílaba recorre el contorno.
        DeformationMode.Speaking =>
            (0.72 * Math.Sin((2 * Math.PI * phase / Periods[index]) + Phases[index])) +
            (0.28 * (index % 2 == 0 ? 1 : -1) * Math.Sin((2 * Math.PI * clock / 0.68) + (index * 0.45))),

        // Revisar: queda tensa, casi lisa. La atención la pide el latido del cuerpo entero.
        DeformationMode.Tense =>
            Math.Sin((2 * Math.PI * phase / Periods[index]) + Phases[index]) * 0.45,

        // Error: dos ruidos superpuestos que no comparten período, así que nunca se repiten igual.
        DeformationMode.Error =>
            (0.6 * Math.Sin(2 * Math.PI * clock / 0.130)) +
            (0.4 * Math.Sin((2 * Math.PI * clock / 0.077) + 1.9)),

        _ => Math.Sin((2 * Math.PI * phase / Periods[index]) + Phases[index])
    };

    /// <summary>Corrimiento de radio propio de cada punto: es lo que hace que la gota se descuelgue.</summary>
    private static double PointBias(DeformationMode mode, double verticality, double clock) => mode switch
    {
        // Se estira hacia arriba: el conjunto se desplaza, no sólo ondula.
        DeformationMode.Listening => -0.8 * verticality * -1,

        // Los puntos de abajo se descuelgan: una gota golpeada.
        DeformationMode.Error => verticality < 0 ? 0.5 : 0,

        // Sin capacidad la gota se cansa: cae abajo y se hunde arriba.
        DeformationMode.Weary => verticality < 0 ? 0.7 : -0.4,

        _ => 0
    };

    /// <summary>
    /// Escalas y desplazamiento del cuerpo entero. Van sobre la masa y nunca sobre los reflejos: la
    /// luz se queda donde estaba y el líquido se mueve por debajo.
    /// </summary>
    private static (double ScaleX, double ScaleY, double ShiftX) BodyTransform(
        DeformationMode mode,
        double clock) => mode switch
    {
        // Squash silábico: se achata al hablar, como una boca.
        DeformationMode.Speaking =>
            (1 - (0.05 * Pulse(clock, 0.34)), 1 + (0.055 * Pulse(clock, 0.34)), 0),

        // Late entera pidiendo atención, sin apurar.
        DeformationMode.Tense =>
            (1 + (0.05 * Math.Sin(2 * Math.PI * clock / 1.6)), 1 + (0.05 * Math.Sin(2 * Math.PI * clock / 1.6)), 0),

        // Temblor horizontal rápido: 11,7 Hz es lo bastante alto para leerse como nervio.
        DeformationMode.Error =>
            (1, 1, 0.85 * Math.Sin(2 * Math.PI * clock * 11.7)),

        _ => (1, 1, 0)
    };

    private static double Pulse(double clock, double period) =>
        0.5 + (0.5 * Math.Sin(2 * Math.PI * clock / period));

    private enum DeformationMode
    {
        Idle,
        Listening,
        Thinking,
        Speaking,
        Tense,
        Error,
        Weary
    }

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
        CharacterTerm Character,
        DeformationMode Mode)
    {
        public static StateProfile For(AssistantVisualState state) => state switch
        {
            AssistantVisualState.Listening => new(
                Rgb(0x72, 0xF0, 0xC0), Rgb(0x16, 0x6B, 0x54), Rgb(0x07, 0x36, 0x2A),
                2.6, 2.8, new CharacterTerm(CharacterKind.Tremor, 0.5, 0.42),
                DeformationMode.Listening),
            AssistantVisualState.Thinking => new(
                Rgb(0x9B, 0xB7, 0xFF), Rgb(0x30, 0x44, 0x86), Rgb(0x15, 0x1E, 0x45),
                3.0, 3.6, new CharacterTerm(CharacterKind.Breath, 1.0, 2.4),
                DeformationMode.Thinking),
            AssistantVisualState.Speaking => new(
                Rgb(0xFF, 0xCE, 0x82), Rgb(0x7D, 0x54, 0x1C), Rgb(0x3E, 0x28, 0x08),
                4.0, 4.3, new CharacterTerm(CharacterKind.Syllabic, 0, 0.34),
                DeformationMode.Speaking),
            AssistantVisualState.Attention => new(
                Rgb(0xFF, 0xB3, 0x47), Rgb(0x7D, 0x4D, 0x13), Rgb(0x3E, 0x23, 0x04),
                1.7, 2.0, new CharacterTerm(CharacterKind.Breath, 1.6, 1.6),
                DeformationMode.Tense),
            AssistantVisualState.Error => new(
                Rgb(0xFF, 0x73, 0x85), Rgb(0x78, 0x26, 0x32), Rgb(0x3B, 0x0F, 0x17),
                2.2, 2.5, new CharacterTerm(CharacterKind.Tremor, 0.65, 0.28),
                DeformationMode.Error),

            // Gris casi sin croma: no es «otro color de estado», es ausencia de color, que es
            // exactamente lo que hay que decir. El borde va más oscuro que en el resto de la paleta
            // porque sobre escritorio claro un cuerpo casi blanco necesita ese contorno para existir.
            AssistantVisualState.Unconfigured => new(
                Rgb(0xE9, 0xEF, 0xF2), Rgb(0x93, 0xA3, 0xAE), Rgb(0x3A, 0x46, 0x4E),
                1.0, 1.7, CharacterTerm.None, DeformationMode.Idle),

            // Mismo cuerpo, casi quieta. De un vistazo se distingue de la anterior sólo por el
            // movimiento, y alcanza: una no arrancó, la otra se quedó sin a dónde ir.
            AssistantVisualState.Offline => new(
                Rgb(0xE9, 0xEF, 0xF2), Rgb(0x93, 0xA3, 0xAE), Rgb(0x3A, 0x46, 0x4E),
                0.35, 1.7, CharacterTerm.None, DeformationMode.Weary),
            _ => new(
                Rgb(0x72, 0xD9, 0xFF), Rgb(0x17, 0x60, 0x7F), Rgb(0x08, 0x31, 0x4A),
                1.0, 1.7, CharacterTerm.None, DeformationMode.Idle)
        };

        public static StateProfile Lerp(StateProfile start, StateProfile target, double t) => new(
            LerpColor(start.Body, target.Body, t),
            LerpColor(start.Depth, target.Depth, t),
            LerpColor(start.Rim, target.Rim, t),
            start.Viscosity + ((target.Viscosity - start.Viscosity) * t),
            start.Excursion + ((target.Excursion - start.Excursion) * t),
            CharacterTerm.Lerp(start.Character, target.Character, t),
            // El modo no se mezcla: una onda que recorre el contorno no es media alternancia de
            // pares e impares. Cambia a mitad de camino, cuando el color ya avanzó lo suficiente
            // para que el salto de dibujo quede tapado por la transición cromática.
            t >= 0.5 ? target.Mode : start.Mode);

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Color LerpColor(Color start, Color target, double t) => Color.FromRgb(
            (byte)(start.R + ((target.R - start.R) * t)),
            (byte)(start.G + ((target.G - start.G) * t)),
            (byte)(start.B + ((target.B - start.B) * t)));
    }
}
