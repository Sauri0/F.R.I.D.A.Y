using System.Windows;
using System.Windows.Media;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Viernes.App.Controls;

/// <summary>
/// La gota. Un contorno de 72 muestras cuya distancia al centro es la suma de cinco armónicos con
/// períodos que no comparten divisor, reconstruido cuadro a cuadro.
/// </summary>
/// <remarks>
/// Dos reglas que se pagaron caro y no conviene volver a probar: <b>deformar más no la hace más
/// líquida</b> —una gota en reposo es casi esférica y lo que la vuelve agua es la luz—, y <b>nada
/// rota</b>, porque girar un cuerpo ovoide barre la silueta y despega los reflejos. La única
/// excepción a la segunda es la pregunta, que ladea el cuerpo a propósito y por poco tiempo.
/// <para>
/// Los cinco armónicos son 2, 3, 4, 5 y 7. El siete y no el seis: seis es múltiplo de dos y de tres
/// y el conjunto se cerraría en un patrón visible cada pocas vueltas.
/// </para>
/// </remarks>
internal partial class LiquidOrb : UserControl, IOrbBody
{
    /// <summary>Muestras del contorno. A 72 el ojo ya no encuentra el polígono.</summary>
    private const int Samples = 72;

    private const double BaseRadius = 24.5;
    private const double CenterX = 35.0;
    private const double CenterY = 36.5;

    /// <summary>Los cinco armónicos. Ninguno es múltiplo de otro, así que el conjunto nunca se repite.</summary>
    private static readonly double[] Harmonics = [2, 3, 4, 5, 7];

    /// <summary>Velocidad relativa de cada armónico. Los signos cruzados desarman cualquier simetría.</summary>
    private static readonly double[] HarmonicRates = [1, -0.74, 0.52, -0.38, 0.24];

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

    private readonly Point[] _points = new Point[Samples];
    private readonly double[] _harmonicPhase = [0.4, 1.9, 3.3, 5.1, 2.2];
    private readonly List<Ripple> _ripples = [];
    private readonly OrbMoodClock _moods = new();

    /// <summary>Quién decide qué se muestra. Ver <see cref="Channel"/>.</summary>
    private OrbStateChannel _channel = new();

    private readonly OrbTransitionClock _transition = new();

    /// <summary>Sólo para dispersar la cola del corte. No toca la silueta.</summary>
    private readonly Random _spray = new(20260818);

    /// <summary>El perfil desde el que sale la transición en vuelo. Es lo que se veía al arrancarla.</summary>
    private OrbStateProfile _from = OrbPalette.For(AssistantVisualState.Idle);

    private AssistantVisualState _shown = AssistantVisualState.Idle;
    private int _moodToken;

    /// <summary>La última transición del canal que este cuerpo llegó a adoptar.</summary>
    private int _transitionToken;

    /// <summary>Cuánto entró la antigüedad de la pregunta sin contestar, de 0 a 1.</summary>
    private double _age;

    /// <summary>
    /// El cero del reloj de la voz. Sólo lo mueve una transición encadenada, y es lo que hace que la
    /// primera onda de la voz caiga exactamente en el cambio de estado.
    /// </summary>
    private double _voiceEpoch;

    /// <summary>
    /// La gota que se desprendió en el corte, si hay una viva. Una sola, y a propósito.
    /// </summary>
    /// <remarks>
    /// El fuente desprende gotas en ocho lugares y acá está implementado <b>uno</b>: el del corte de
    /// «hablando → interrumpida», que es el único donde el satélite <em>significa</em> algo —es la
    /// parte de la frase que ya había salido del cuerpo cuando le pediste que pare, y por eso sigue
    /// viajando y muere afuera—. Los otros siete son adorno de movimiento: cuatro de los ánimos
    /// (¡listo! desprende seis de una, urgente dos, no salió y hola una cada uno), uno del estado
    /// error mientras el cuerpo está roto, y dos de la física —al golpear contra un borde y al
    /// arrastrar rápido—.
    /// <para>
    /// Portarlos pide convertir esto en una lista acotada, porque <c>spawn(6, …)</c> no entra en un
    /// campo que guarda uno. Se anota entero para que la próxima persona sepa qué falta de verdad:
    /// una lista de omisiones incompleta es peor que no tenerla, porque se lee como si estuviera
    /// todo declarado.
    /// </para>
    /// </remarks>
    private Satellite? _tail;

    private double _phase;
    private double _clock;
    private double _levelSmoothed;

    /// <summary>Escala del cuerpo entero y su velocidad: un resorte, no una animación.</summary>
    private double _scale = 1.0;
    private double _scaleVelocity;

    private double _nextThinkRipple;
    private OrbMoodShape _mood = OrbMoodShape.Neutral;

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

    /// <summary>El estado de fondo.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(LiquidOrb),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

    /// <inheritdoc />
    public AssistantVisualState State
    {
        get => (AssistantVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Sobre escritorio claro la gota se dibuja con el color de profundidad, no con el cuerpo.</summary>
    public static readonly DependencyProperty IsLightDesktopProperty = DependencyProperty.Register(
        nameof(IsLightDesktop),
        typeof(bool),
        typeof(LiquidOrb),
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
        typeof(LiquidOrb),
        new PropertyMetadata(0.0));

    /// <inheritdoc />
    public double NightMode
    {
        get => (double)GetValue(NightModeProperty);
        set => SetValue(NightModeProperty, value);
    }

    /// <summary>
    /// El canal que arbitra estados y ánimos. Se puede reemplazar por uno compartido.
    /// </summary>
    /// <remarks>
    /// Por omisión cada cuerpo tiene el suyo, así que funciona sin cablear nada. Si la píldora y el
    /// orbe tienen que decir exactamente lo mismo en el mismo cuadro —y tienen que decirlo—, hay que
    /// pasarles el mismo canal desde el shell. Es también por donde entra la antigüedad de la
    /// pregunta (<see cref="OrbStateChannel.WaitingAge"/>) y el aviso de reintento del runtime
    /// (<see cref="OrbStateChannel.ReportRetry"/>).
    /// </remarks>
    internal OrbStateChannel Channel
    {
        get => _channel;
        set
        {
            _channel = value;
            _moodToken = value.MoodToken;
            _transitionToken = value.TransitionToken;
        }
    }

    /// <inheritdoc />
    public void ShowMood(OrbMood mood) => _channel.RequestMood(mood);

    /// <summary>Hay autorización de gasto viva. Tiñe el borde de cálido mientras dure.</summary>
    public static readonly DependencyProperty HasSpendAuthorizationProperty = DependencyProperty.Register(
        nameof(HasSpendAuthorization),
        typeof(bool),
        typeof(LiquidOrb),
        new PropertyMetadata(false));

    /// <summary>Hay autorización de gasto viva.</summary>
    internal bool HasSpendAuthorization
    {
        get => (bool)GetValue(HasSpendAuthorizationProperty);
        set => SetValue(HasSpendAuthorizationProperty, value);
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

    /// <summary>Nivel del micrófono, de 0 a 1.</summary>
    internal double AudioLevel
    {
        get => (double)GetValue(AudioLevelProperty);
        set => SetValue(AudioLevelProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // La propiedad es el pedido, no la verdad: quién entra y cuándo lo decide el canal. Un error
        // corta lo que haya en vuelo; un «volvé a reposo» espera su turno y entra igual.
        ((LiquidOrb)d)._channel.Request((AssistantVisualState)e.NewValue);
    }

    /// <summary>El perfil del estado que se está mostrando, ya aplanado si hay movimiento reducido.</summary>
    private OrbStateProfile Target() => OrbQuiet.Apply(OrbPalette.For(_shown), _shown);

    /// <summary>
    /// Lo que la transición hace en su primer cuadro, y que después se apaga solo.
    /// </summary>
    /// <remarks>
    /// La onda de entrada puede ser negativa: en <em>hablando → interrumpida</em> vale −1,45 y sale
    /// hacia adentro. Y esa transición además desprende una gota, que es la parte de la frase que ya
    /// había salido del cuerpo cuando le pediste que pare: sigue viajando, cae, y muere afuera. La
    /// transición dura ochenta milésimas; la cola, casi un segundo.
    /// </remarks>
    private void EnterTransition()
    {
        var spec = _transition.Spec;
        _ripples.Add(new Ripple(_clock, spec.Ripple));
        _scaleVelocity += spec.Ripple * 1.15;

        if (spec.IsCut)
        {
            var angle = _spray.NextDouble() * 2 * Math.PI;
            _tail = new Satellite(
                Math.Cos(angle) * 6,
                Math.Sin(angle) * 6,
                (26 * (0.5 + _spray.NextDouble())) + ((_spray.NextDouble() - 0.5) * 14),
                (-6 * (0.5 + _spray.NextDouble())) + ((_spray.NextDouble() - 0.5) * 14),
                2.2 * (0.6 + (_spray.NextDouble() * 0.7)),
                1.0,
                0.9);
        }

        if (spec.IsChained)
        {
            // Encadenada: nada se reinicia y el reloj de la voz arranca acá. La primera onda de la
            // voz no acompaña a la transición: es la transición.
            _voiceEpoch = _clock;
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
        delta = Math.Clamp(delta, 0, 0.1);
        _clock += delta;

        // El estado pedido se reafirma en cada cuadro. Ver OrbStateChannel.Poll(AssistantVisualState):
        // la propiedad es el pedido, el canal es lo que se ve, y sin volver a preguntar una
        // divergencia entre los dos no tiene forma de arreglarse sola.
        _channel.Poll(State);

        // Quién cambió y cuándo lo decide el canal. El cuerpo no vuelve a resolver el par ni arranca
        // su propio reloj: si lo hiciera, dos cambios entre dos cuadros le darían otra entrada de la
        // tabla que la que el canal midió, y el reloj empezaría a contar recién cuando el cuerpo se
        // entera —que con el control descargado puede ser nunca.
        if (_channel.TransitionToken != _transitionToken || _channel.State != _shown)
        {
            _transitionToken = _channel.TransitionToken;

            // Se sale desde lo que se está viendo y no desde el estado anterior nominal: si el
            // cambio llega a mitad de una transición, no hay salto.
            _from = OrbPalette.Lerp(_from, Target(), _transition.Blend);
            _shown = _channel.State;
            _transition.Begin(_channel.Transition, _channel.TransitionElapsed);
            EnterTransition();
        }
        else
        {
            _transition.Sync(_channel.TransitionElapsed);
        }

        // El recortado, y sólo el recortado, para los sobres: aliento, temblor y voz no anticipan.
        // La anticipación de la curva entra por el otro factor de Blend, que es el que mueve la forma.
        var eased = _transition.EasedClamped;
        var profile = OrbPalette.Lerp(_from, Target(), _transition.Blend);
        _age = _shown == AssistantVisualState.WaitingForYou ? OrbAge.Strength(_channel.WaitingAge) : 0;

        if (_channel.MoodToken != _moodToken)
        {
            _moodToken = _channel.MoodToken;
            if (_channel.Mood is { } pending)
            {
                _moods.Trigger(pending);
            }
        }

        // El mismo ánimo pesa distinto según sobre qué estado cae: la energía del estado escala la
        // amplitud del gesto.
        _mood = _moods.Advance(delta, OrbBody.Gota).Scale(OrbPriority.Energy(_shown));
        if (_moods.TryTakeImpulse(out var impulse))
        {
            _ripples.Add(new Ripple(_clock, impulse.Ripple));
            _scaleVelocity += impulse.Scale;
        }

        profile = ApplyTaskPacing(profile, delta);
        AdvanceBeat(delta);
        AdvanceTail(delta);

        // La velocidad del estado acelera el reloj de la ondulación, no su amplitud.
        var speed = OrbNight.Speed(NightMode) * profile.DropSpeed;

        // La patada de giro de la transición, igual que en la nube: entra de golpe y se apaga a la
        // décima. En la gota no gira ningún anillo, así que empuja las cinco fases armónicas —cada
        // una a su ritmo y con su signo—, y el contorno entero pega el respingo.
        var burst = _transition.ConsumeSpin(delta);

        _phase += delta * speed;
        for (var i = 0; i < Harmonics.Length; i++)
        {
            _harmonicPhase[i] += delta * speed * HarmonicRates[i] * 0.9;
            if (burst > 0)
            {
                _harmonicPhase[i] += delta * burst * 3.2 * HarmonicRates[i];
            }
        }

        // La escala es un resorte crítico: los golpes entran como velocidad y vuelven solos.
        _scaleVelocity += ((170 * (1 - _scale)) - (15 * _scaleVelocity)) * delta;
        _scale += _scaleVelocity * delta;

        if (profile.ThinkPeriod > 0 && _clock > _nextThinkRipple)
        {
            _ripples.Add(new Ripple(_clock, 0.42));
            _nextThinkRipple = _clock + profile.ThinkPeriod;
        }

        _ripples.RemoveAll(r => _clock - r.Start > 1.5);

        ApplyGeometry(profile, eased);
        ApplyPalette(profile);
    }

    /// <summary>Las ondas que salen del centro, sumadas para un radio dado.</summary>
    private double RippleAt(double radius)
    {
        var value = 0.0;
        foreach (var ripple in _ripples)
        {
            var tau = _clock - ripple.Start;
            if (tau > 1.5)
            {
                continue;
            }

            value += ripple.Amplitude * Math.Exp(-tau * 2.0) * Math.Sin(2 * Math.PI * ((radius * 1.25) - (tau * 2.15)));
        }

        return value * 0.072;
    }

    /// <summary>
    /// Ajusta el ritmo según cuánto lleva la tarea, y devuelve el perfil ya corregido.
    /// </summary>
    private OrbStateProfile ApplyTaskPacing(OrbStateProfile profile, double delta)
    {
        if (_shown != AssistantVisualState.Thinking && _shown != AssistantVisualState.Background)
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

        // Tramo 1: mantiene el ritmo del estado. Tramo 2: desacelera y empieza a contar con sedimento.
        if (_taskElapsed <= LongTaskSeconds)
        {
            return profile;
        }

        var into = Math.Min(1, (_taskElapsed - LongTaskSeconds) / 3.0);
        var level = Math.Min(SedimentCeiling, (_taskElapsed - LongTaskSeconds) * SedimentRisePerSecond);
        Sediment.Height = level * 70;
        System.Windows.Controls.Canvas.SetTop(Sediment, 70 - (level * 70));

        return profile with { DropSpeed = profile.DropSpeed * (1 - (0.35 * OrbPalette.SineInOut(into))) };
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

    /// <summary>
    /// La gota que se desprendió en el corte: se va, frena, cae y se apaga.
    /// </summary>
    /// <remarks>
    /// El roce y la gravedad son por cuadro en el boceto, que corre a 60; acá se elevan al tiempo
    /// real para que la cola se vea igual en una máquina que baja a 30. Es el único lugar donde un
    /// número del fuente se reinterpreta, y es porque el fuente ahí depende de su propio fps.
    /// </remarks>
    private void AdvanceTail(double delta)
    {
        if (_tail is not { } tail)
        {
            return;
        }

        var life = tail.Life - (delta / tail.Span);
        if (life <= 0)
        {
            _tail = null;
            return;
        }

        var drag = Math.Pow(0.94, delta * 60);
        _tail = tail with
        {
            Life = life,
            X = tail.X + (tail.VelocityX * delta),
            Y = tail.Y + (tail.VelocityY * delta),
            VelocityX = tail.VelocityX * drag,
            VelocityY = (tail.VelocityY * drag) + (34 * delta * 0.6)
        };
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

    private void ApplyGeometry(OrbStateProfile profile, double eased)
    {
        // Los gestos propios del estado, todos sobre el radio del cuerpo entero.
        var extra = _mood.Extra;
        if (profile.Breath is { } breath)
        {
            // La antigüedad alarga el período: a los tres días la respiración dura ×2,15. No es que
            // respire menos, es que respira más despacio, que es lo que hace algo que lleva mucho
            // tiempo esperando.
            var period = breath.Period * (1 + (OrbAge.BreathStretch * _age));
            extra += breath.Amplitude * Math.Sin(2 * Math.PI * _clock / period) * eased;
        }

        if (profile.Tremor > 0)
        {
            extra += profile.Tremor * Math.Sin(2 * Math.PI * _clock / 0.085) * eased;
        }

        if (profile.Knock is { } knock)
        {
            var u = (_clock % knock.Period) / knock.Period;
            if (u < 0.16)
            {
                extra += knock.Amplitude * Math.Sin(Math.PI * u / 0.16);
            }
            else if (u is > 0.22 and < 0.38)
            {
                extra += knock.Amplitude * 0.68 * Math.Sin(Math.PI * (u - 0.22) / 0.16);
            }
        }

        // El estirón de sorda no tiene cadencia propia: la marca la escalera de reintentos, y cada
        // intento sale más débil que el anterior. Ver OrbDeafRetry.
        if (profile.ReachPeriod > 0)
        {
            extra += OrbDeafRetry.DropStretch(_channel.Retry);
        }

        // El reloj de la voz sólo se separa del de pared cuando la transición fue encadenada.
        var voice = _clock - _voiceEpoch;

        // Oído y boca: escuchar hunde, hablar saca. Los dos suman actividad.
        var earIn = profile.Ear > 0
            ? profile.Ear * Math.Pow(Math.Abs(Math.Sin(_clock * 2.35) * Math.Sin((_clock * 0.73) + 0.4)), 1.25)
            : 0;
        var mouthOut = profile.Wave is not null
            ? (profile.Mouth > 0 ? profile.Mouth : 0.13) *
                Math.Pow(Math.Abs(Math.Sin(voice * 3.05) * Math.Sin((voice * 1.13) + 0.6)), 1.45)
            : 0;
        var sound = (mouthOut - earIn) * eased;
        var soundEnergy = (mouthOut + earIn) * eased;

        // La voz del usuario entra como crecimiento del radio, no como agitación del contorno:
        // hablarle más fuerte la hincha, y eso se lee de inmediato como «me está oyendo».
        // El seguimiento es asimétrico —sube rápido, baja lento— porque una caída instantánea
        // parpadea con cada sílaba en vez de acompañar la frase.
        var target = _shown == AssistantVisualState.Listening ? Math.Clamp(AudioLevel, 0, 1) : 0;
        _levelSmoothed += (target - _levelSmoothed) * (target > _levelSmoothed ? 0.45 : 0.08);
        extra += _levelSmoothed * 0.128;

        // El latido crece la masa entera bajo la misma luz —los reflejos están fuera de esta cuenta—,
        // y eso es exactamente lo que se lee como líquido en vez de como un objeto que se infla.
        extra += _beat * 0.048;

        var radius = BaseRadius * _scale * _mood.CoreRadius *
            (1 + extra + RippleAt(1) + (sound * 0.8));

        // El signo se arma con gotas que salen del cuerpo, así que el cuerpo se vacía en la misma
        // proporción. Es lo que hace que el tilde parezca hecho <em>de</em> ella y no dibujado encima.
        var beads = BuildGlyph(radius, out var drain);
        radius *= 1 - (0.82 * drain);

        var amplitude = profile.DropAmplitude * _mood.AmplitudeFactor *
            (1 + (0.7 * soundEnergy)) * (1 - (0.88 * drain));

        var spec = _transition.Spec;
        var kick = _transition.Kick;

        var centerX = CenterX + (profile.Lean * 22) + _mood.OffsetX;

        // El salto y el hundimiento de la transición, y el hundimiento de la pregunta vieja: los dos
        // son lo mismo con dos escalas de tiempo. Uno dura lo que dura la transición; el otro, días.
        var centerY = CenterY + (0.5 * Math.Sin(_clock / 3.7)) + _mood.OffsetY -
            (spec.Lift * kick) + (spec.Sink * kick) + (OrbAge.Sink * _age);
        var rollCos = Math.Cos(_mood.Roll);
        var rollSin = Math.Sin(_mood.Roll);

        for (var i = 0; i < Samples; i++)
        {
            var theta = (double)i / Samples * 2 * Math.PI;

            var scale = 1.0;
            for (var j = 0; j < Harmonics.Length; j++)
            {
                scale += amplitude *
                    Math.Cos((Harmonics[j] * theta) + _harmonicPhase[j] + (_phase * HarmonicRates[j])) /
                    (1 + (j * 0.55));
            }

            // El achatamiento de abajo es la gravedad, y es lo único que le da un arriba y un abajo
            // a una forma que si no sería una mancha.
            scale *= 1 - (0.055 * Math.Max(0, Math.Sin(theta)));

            if (_mood.Sway != 0)
            {
                scale *= 1 + (0.02 * _mood.Sway * (0.5 - (0.5 * Math.Sin(theta))));
            }

            var dx = Math.Cos(theta) * scale * radius;
            var dy = Math.Sin(theta) * scale * radius * 0.985;

            if (_mood.Sway != 0)
            {
                dx += _mood.Sway * 0.16 * (0.5 - (0.5 * Math.Sin(theta)));
            }

            if (_mood.Roll != 0)
            {
                var rotated = (dx * rollCos) - (dy * rollSin);
                dy = (dx * rollSin) + (dy * rollCos);
                dx = rotated;
            }

            if (_mood.SquashY != 1)
            {
                dy *= _mood.SquashY;
                dx *= 1 + ((1 - _mood.SquashY) * 0.5);
            }

            if (_mood.Shear != 0)
            {
                dx += dy * _mood.Shear;
            }

            // Sin la guarda del lienzo del boceto, el error se saldría del control. El techo es
            // blando: ningún estado que no sea el error llega a tocarlo.
            (dx, dy) = OrbBounds.SoftLimit(dx, dy);

            _points[i] = new Point(centerX + dx, centerY + dy);
        }

        var body = new StreamGeometry();
        using (var context = body.Open())
        {
            // Curvas cuadráticas por los puntos medios: el contorno pasa suave por 72 muestras sin
            // calcular tangentes, y con 72 el error contra la curva verdadera es invisible.
            context.BeginFigure(Midpoint(_points[Samples - 1], _points[0]), isFilled: true, isClosed: true);
            for (var i = 0; i < Samples; i++)
            {
                var next = _points[(i + 1) % Samples];
                context.QuadraticBezierTo(_points[i], Midpoint(_points[i], next), isStroked: true, isSmoothJoin: true);
            }
        }

        body.Freeze();

        if (beads.Count == 0)
        {
            Mass.Data = body;
        }
        else
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(body);
            foreach (var bead in beads)
            {
                group.Children.Add(new EllipseGeometry(new Point(bead.X, bead.Y), bead.Radius, bead.Radius));
            }

            group.Freeze();
            Mass.Data = group;
        }

        if (_tail is { } tail)
        {
            var size = tail.Radius * (0.45 + (0.55 * tail.Life));
            var shape = new EllipseGeometry(new Point(centerX + tail.X, centerY + tail.Y), size, size * 1.08);
            shape.Freeze();
            Tail.Data = shape;
            Tail.Opacity = Math.Min(1, tail.Life * 1.4);
            Tail.Visibility = Visibility.Visible;
        }
        else if (Tail.Visibility != Visibility.Collapsed)
        {
            Tail.Visibility = Visibility.Collapsed;
        }

        // Recortar los reflejos contra la silueta es lo que hace que la luz resbale por el borde.
        // Se recortan contra el cuerpo y no contra el signo: la luz es del ambiente y el signo es
        // una escritura, no un objeto con volumen.
        Reflections.Clip = body;

        // El sedimento usa el mismo recorte: es líquido dentro de la gota, no una banda encima.
        SedimentLayer.Clip = body;
    }

    /// <summary>
    /// Las gotas del signo, y cuánto cuerpo se llevaron.
    /// </summary>
    /// <remarks>
    /// Cada gota sale de cerca del centro y viaja por una curva cuadrática hasta su lugar en el
    /// trazo, y las de más adelante en el signo salen más tarde: por eso el signo se escribe de una
    /// punta a la otra en vez de aparecer entero. La deuda es que acá son círculos sueltos que se
    /// superponen, y en el boceto son un contorno envolvente calculado sobre la cadena. A 108 px la
    /// superposición ya cierra el trazo; en un orbe grande se notarían las juntas.
    /// </remarks>
    private List<Bead> BuildGlyph(double radius, out double drain)
    {
        drain = 0;
        var beads = new List<Bead>();
        if (_mood.GlyphReveal <= 0.004 || _mood.Glyph == OrbGlyphKind.None)
        {
            return beads;
        }

        var points = OrbGlyph.Points(_mood.Glyph);
        var count = points.Length;
        if (count == 0)
        {
            return beads;
        }

        var span = radius * 1.42 * _mood.GlyphScale;
        var smooth = _mood.GlyphReveal * _mood.GlyphReveal * (3 - (2 * _mood.GlyphReveal));
        var centerX = CenterX + (_mood.OffsetX);
        var centerY = CenterY + (0.5 * Math.Sin(_clock / 3.7)) + _mood.OffsetY;

        for (var i = 0; i < count; i++)
        {
            var raw = Math.Clamp(((smooth * 1.36) - ((double)i / count * 0.44)) / 0.50, 0, 1);
            var reveal = raw * raw * (3 - (2 * raw));
            drain += reveal / count;
            if (reveal < 0.004)
            {
                continue;
            }

            var e = 1 - Math.Pow(1 - reveal, 2.0);
            var om = 1 - e;
            var overshoot = 1 + (0.055 * Math.Sin(Math.PI * reveal));

            var tx = points[i].X * span * overshoot;
            var ty = points[i].Y * span * overshoot;
            var bx = tx * 0.10;
            var by = ty * 0.10;
            var mx = ((bx + tx) / 2) - ((ty - by) * 0.17);
            var my = ((by + ty) / 2) + ((tx - bx) * 0.17);

            var ex = (om * om * bx) + (2 * om * e * mx) + (e * e * tx);
            var ey = (om * om * by) + (2 * om * e * my) + (e * e * ty);

            var size = points[i].Radius * span * (0.44 + (0.56 * reveal)) *
                (1 + (0.075 * Math.Sin((_phase * 2.4) + (i * 0.6))));

            beads.Add(new Bead(centerX + ex, centerY + ey, size));
        }

        return beads;
    }

    private void ApplyPalette(OrbStateProfile profile)
    {
        var night = Math.Clamp(NightMode, 0, 1);
        var light = IsLightDesktop;

        var body = OrbNight.Tint(light ? profile.Depth : profile.Body, night);
        body = OrbPalette.Wash(body, _mood.Tint, _mood.WashCore);

        // La profundidad y el borde salen del color de profundidad y no son tres colores sueltos por
        // estado: derivarlos es lo que permite que quince estados tengan luz coherente sin quince
        // decisiones cromáticas que envejecen mal.
        var deep = OrbNight.Tint(profile.Depth, night);
        deep = light ? OrbPalette.Lighten(deep, 0.30) : OrbPalette.LerpColor(deep, Colors.Black, 0.42);
        deep = OrbPalette.Wash(deep, _mood.Tint, _mood.WashCore * 0.6);

        var rim = OrbPalette.LerpColor(OrbNight.Tint(profile.Depth, night), Colors.Black, 0.55);
        rim = OrbPalette.Wash(rim, _mood.Tint, _mood.WashRim * 0.5);

        StopLight.Color = OrbPalette.Lighten(body, 0.26);
        StopBody.Color = body;
        StopDeep.Color = deep;
        StopRim.Color = rim;
        MassGlow.Color = body;

        // El sedimento es el color de profundidad al 55 %: pertenece al mismo líquido, más denso.
        Sediment.Fill = new SolidColorBrush(Color.FromArgb(0x8C, deep.R, deep.G, deep.B));

        // Mientras hay autorización de gasto viva, el borde queda más cálido todo el día.
        //
        // No es un distintivo encima de la gota: es el propio stop de borde mezclado con ámbar. Se
        // nota sin gritar, y desaparece al revocar o al reiniciar —que es justamente la garantía:
        // la autorización vive en memoria y muere con el proceso, así que el aviso también.
        if (HasSpendAuthorization)
        {
            StopRim.Color = OrbPalette.LerpColor(StopRim.Color, Color.FromRgb(0x3E, 0x23, 0x04), 0.40);
        }

        // Durante el latido el borde se aclara: el golpe de masa se acompaña con luz en el contorno.
        if (_beat > 0)
        {
            StopRim.Color = OrbPalette.Lighten(StopRim.Color, 0.18 * _beat);
        }

        // El rebote toma el color del cuerpo y nada más.
        //
        // Acá había un aro verde propio para «micrófono armado», de cuando el armado era una bandera
        // encima de reposo y no un estado. Ahora es «guardia», con su fila entera en la tabla —color
        // 5F B3 9A, dispersión, giro, la píldora—, y la lleva la nube igual que la gota. Dejar el aro
        // era teñir de verde un orbe que ya estaba verde, y con la condición vieja
        // —armado && reposo— ni siquiera se encendía: el armado ya no publica reposo.
        BounceCore.Color = Color.FromArgb(0x57, body.R, body.G, body.B);
        BounceEdge.Color = Color.FromArgb(0x00, body.R, body.G, body.B);

        // La gota desprendida es del mismo líquido: mismo color, con el borde un poco más hundido
        // porque ya no le llega la luz del cuerpo.
        TailCore.Color = OrbPalette.Lighten(body, 0.42);
        TailEdge.Color = deep;

        // La opacidad general es la que separa «trabajando sin vos» de «pensando» sin cambiar el
        // color, y la que hace que la madrugada baje el volumen de todo de una sola vez. El destello
        // de la transición y la pérdida de brillo de la pregunta vieja entran acá por la misma razón:
        // no son colores nuevos, son el mismo color con otro volumen.
        Stage.Opacity = Math.Clamp(
            profile.Alpha * OrbNight.Alpha(night) * _mood.AlphaFactor *
                (1 + (_transition.Spec.Flash * _transition.Kick)) *
                (1 - (OrbAge.BrightnessDrop * _age)),
            0,
            1);
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private readonly record struct Ripple(double Start, double Amplitude);

    /// <summary>Una gota del signo: dónde quedó y de qué tamaño.</summary>
    private readonly record struct Bead(double X, double Y, double Radius);

    /// <summary>
    /// Una gota que se soltó del cuerpo y se va sola.
    /// </summary>
    /// <param name="X">Posición respecto del centro del cuerpo.</param>
    /// <param name="Y">Ídem, positivo hacia abajo.</param>
    /// <param name="VelocityX">Velocidad horizontal, en unidades del lienzo por segundo.</param>
    /// <param name="VelocityY">Velocidad vertical. La gravedad la va empujando hacia abajo.</param>
    /// <param name="Radius">Radio con la que salió. Se achica al apagarse.</param>
    /// <param name="Life">Lo que le queda, de 1 a 0.</param>
    /// <param name="Span">Cuántos segundos vive en total.</param>
    private readonly record struct Satellite(
        double X,
        double Y,
        double VelocityX,
        double VelocityY,
        double Radius,
        double Life,
        double Span);
}
