using System.ComponentModel;
using System.Windows;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: el alias evita la ambigüedad de nombres.
using Color = System.Windows.Media.Color;

namespace Viernes.App.Controls;

/// <summary>
/// Movimiento reducido: los quince estados colapsan a siete siluetas quietas.
/// </summary>
/// <remarks>
/// Sin movimiento los quince no se distinguen. La forma sola puede cargar siete cosas —en reposo,
/// atendiendo, trabajando, hablando, te espera, degradada, falló— y ni una más, porque lo que
/// separaba a <em>pensando</em> de <em>trabajando sin vos</em> era la cadencia, y acá no hay
/// cadencia.
/// <para>
/// De ahí sale la consecuencia que no es negociable: <b>en modo quieto la píldora deja de ser
/// opcional</b>. La forma carga siete y el texto carga quince; si se apaga el texto se pierden ocho
/// estados. Por eso <see cref="StatePill"/> ignora el pedido de silencio cuando esto está prendido:
/// no es una preferencia que se pueda desoír, es la mitad de la información.
/// </para>
/// </remarks>
internal static class OrbQuiet
{
    /// <summary>Las siete siluetas, en el orden de <c>QZC</c>.</summary>
    private static readonly OrbQuietGroup[] Groups =
    [
        new(Rgb(0x72, 0xD9, 0xFF), Rgb(0x17, 0x60, 0x7F), 1.00, 0.85, 1.00, 1.10, 1.00, "en reposo"),
        new(Rgb(0x72, 0xF0, 0xC0), Rgb(0x16, 0x6B, 0x54), 1.05, 2.15, 1.14, 1.34, 1.00, "atendiendo"),
        new(Rgb(0x9B, 0xB7, 0xFF), Rgb(0x30, 0x44, 0x86), 1.02, 1.15, 0.97, 1.06, 0.92, "trabajando") { TwoShells = true },
        new(Rgb(0xFF, 0xCE, 0x82), Rgb(0x7D, 0x54, 0x1C), 1.04, 0.10, 1.06, 1.22, 1.00, "hablando"),
        new(Rgb(0xFF, 0x9E, 0x5E), Rgb(0x7A, 0x3F, 0x16), 0.89, 0.28, 0.84, 0.90, 0.94, "te espera"),
        new(Rgb(0xB9, 0xC2, 0xC8), Rgb(0x4A, 0x55, 0x5C), 0.95, 0.20, 0.92, 0.86, 0.80, "degradada") { SingleShell = true },
        new(Rgb(0xFF, 0x73, 0x85), Rgb(0x78, 0x26, 0x32), 1.10, 1.40, 1.18, 1.50, 1.00, "falló") { StaticGap = true }
    ];

    private static bool? _force;

    /// <summary>Cambió la preferencia. La píldora la escucha porque se apaga sola cuando no pasa nada.</summary>
    internal static event EventHandler? Changed;

    static OrbQuiet()
    {
        // El usuario puede prender «mostrar animaciones» con Viernes abierto, y el orbe tiene que
        // enterarse sin que haya que reiniciar nada.
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    /// <summary>
    /// Fuerza el modo quieto por código, o lo suelta con <see langword="null"/> para volver a la
    /// preferencia del sistema.
    /// </summary>
    /// <remarks>
    /// Existe por dos razones concretas: para que la maqueta de comparación pueda mirar las siete
    /// siluetas sin tocar la configuración de Windows, y porque «reducir movimiento» es una decisión
    /// que un usuario puede querer tomar para Viernes sin tomarla para todo el escritorio.
    /// </remarks>
    internal static bool? Force
    {
        get => _force;
        set
        {
            if (_force == value)
            {
                return;
            }

            _force = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Está en movimiento reducido.</summary>
    /// <remarks>
    /// La preferencia del sistema es <c>SPI_GETCLIENTAREAANIMATION</c>, que en WPF se lee como
    /// <see cref="SystemParameters.ClientAreaAnimation"/>: es la misma casilla que apaga las
    /// animaciones de las ventanas de Windows, así que respetarla es respetar lo que el usuario ya
    /// dijo una vez y no quiere volver a decir.
    /// </remarks>
    internal static bool IsReduced => _force ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>A cuál de las siete siluetas cae este estado.</summary>
    /// <remarks>
    /// Portado de <c>QZ</c>. Los pares que colapsan dicen bastante sobre qué es cada estado:
    /// <em>reposo</em> con <em>guardia</em> (los dos son estar disponible), <em>pensando</em> con
    /// <em>trabajando sin vos</em> (los dos son estar ocupada) y <em>error</em> con
    /// <em>interrumpida</em> (los dos son que algo se cortó).
    /// </remarks>
    internal static OrbQuietGroup GroupFor(AssistantVisualState state) => Groups[IndexFor(state)];

    /// <summary>El índice de la silueta, de 0 a 6.</summary>
    internal static int IndexFor(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Idle => 0,
        AssistantVisualState.Watching => 0,
        AssistantVisualState.Listening => 1,
        AssistantVisualState.Thinking => 2,
        AssistantVisualState.Background => 2,
        AssistantVisualState.Speaking => 3,
        AssistantVisualState.WaitingForYou => 4,
        AssistantVisualState.ProjectWaiting => 4,
        AssistantVisualState.Attention => 4,
        AssistantVisualState.AskingPermission => 4,
        AssistantVisualState.Deaf => 5,
        AssistantVisualState.Offline => 5,
        AssistantVisualState.Unconfigured => 5,
        AssistantVisualState.Error => 6,
        _ => 6
    };

    /// <summary>
    /// El perfil de un estado ya aplanado a su silueta quieta.
    /// </summary>
    /// <remarks>
    /// Se apaga todo lo que es tiempo —giro, respiración, temblor, golpeteo, estirón, onda de voz,
    /// oído, inclinación— y se queda sólo lo que es forma. El <see cref="OrbStateProfile.Wobble"/>
    /// no baja a cero sino a 0,002: un borde perfectamente liso se lee como un ícono pegado, y lo
    /// que hay que decir es «está viva pero no se mueve», no «es una calcomanía».
    /// </remarks>
    internal static OrbStateProfile Apply(OrbStateProfile profile, AssistantVisualState state)
    {
        if (!IsReduced)
        {
            return profile;
        }

        var group = GroupFor(state);
        return profile with
        {
            Body = group.Body,
            Depth = group.Depth,
            Dispersion = group.Dispersion,
            Tilt = group.Tilt,
            RingScale = group.RingScale,
            DustRadius = group.DustRadius,
            Alpha = group.Alpha,
            Wobble = 0.002,
            Yaw = 0,
            Spin = default,
            DustSpeed = 0,
            Turbulence = 0,
            ThinkPeriod = 0,
            Breath = null,
            Knock = null,
            ReachPeriod = 0,
            Tremor = 0,
            Wave = null,
            Ear = 0,
            Mouth = 0,
            Lean = 0,
            IsBroken = false,
            IsTwoShells = group.TwoShells,
            IsSingleShell = group.SingleShell,
            HasStaticGap = group.StaticGap,

            // La gota no tiene anillos ni polvo: lo suyo es la deformación del contorno, y ahí el
            // modo quieto se lee bajando la excursión al 30 % y la velocidad al 10 %. No a cero:
            // una gota inmóvil deja de ser líquida y pasa a ser un óvalo.
            DropAmplitude = profile.DropAmplitude * 0.30,
            DropSpeed = profile.DropSpeed * 0.10
        };
    }

    private static void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemParameters.ClientAreaAnimation))
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>Una de las siete siluetas quietas.</summary>
/// <param name="Body">Color del cuerpo sobre escritorio oscuro.</param>
/// <param name="Depth">Color de profundidad, el que se dibuja sobre escritorio claro.</param>
/// <param name="Dispersion">Radio general.</param>
/// <param name="Tilt">Apertura de los anillos.</param>
/// <param name="RingScale">Radio de los anillos respecto del núcleo.</param>
/// <param name="DustRadius">Hasta dónde llega el polvo.</param>
/// <param name="Alpha">Opacidad general.</param>
/// <param name="Name">Cómo se llama la silueta. Es lo que dice la píldora antes del nombre del estado.</param>
internal sealed record OrbQuietGroup(
    Color Body,
    Color Depth,
    double Dispersion,
    double Tilt,
    double RingScale,
    double DustRadius,
    double Alpha,
    string Name)
{
    /// <summary>Dos cascarones en vez de tres. Es lo que distingue «trabajando» de «en reposo» sin girar.</summary>
    internal bool TwoShells { get; init; }

    /// <summary>Un solo cascarón, sin apertura. Es el cuerpo de la capacidad reducida.</summary>
    internal bool SingleShell { get; init; }

    /// <summary>Un tramo fijo del anillo que no se dibuja. Es el error sin el temblor.</summary>
    internal bool StaticGap { get; init; }
}
