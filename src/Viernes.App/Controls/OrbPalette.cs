using System.Windows.Media;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: el alias evita la ambigüedad de nombres.
using Color = System.Windows.Media.Color;

namespace Viernes.App.Controls;

/// <summary>Un término que late: amplitud y período en segundos.</summary>
/// <remarks>
/// Se usa para la respiración, el temblor, el golpeteo y la onda de la voz. Son todos el mismo
/// objeto porque son la misma idea —algo que vuelve— y lo único que los distingue es dónde entran.
/// </remarks>
internal readonly record struct OrbPulse(double Amplitude, double Period);

/// <summary>Las tres velocidades de giro, una por cascarón.</summary>
/// <remarks>
/// Van con signos alternados a propósito: si los tres anillos giraran para el mismo lado el
/// conjunto se leería como un objeto sólido rotando, y lo que hay que leer es una nube.
/// </remarks>
internal readonly record struct OrbSpin(double First, double Second, double Third)
{
    /// <summary>Velocidad del cascarón <paramref name="index"/>, de 0 a 2.</summary>
    internal double this[int index] => index switch
    {
        0 => First,
        1 => Second,
        _ => Third
    };
}

/// <summary>
/// Todo lo que define un estado del orbe: color, forma, movimiento y nombre.
/// </summary>
/// <remarks>
/// Es una sola tabla para los dos cuerpos. La nube usa la dispersión, el giro, la inclinación y el
/// polvo; la gota usa la amplitud y la velocidad de deformación. Los dos usan el color, la
/// respiración, el temblor y los gestos. Que sea una tabla y no dos es lo que garantiza que
/// cambiar de cuerpo no cambie de vocabulario.
/// </remarks>
internal sealed record OrbStateProfile
{
    /// <summary>Color del cuerpo sobre escritorio oscuro.</summary>
    internal required Color Body { get; init; }

    /// <summary>Color de profundidad. Sobre escritorio claro es el que se dibuja.</summary>
    internal required Color Depth { get; init; }

    /// <summary>Nombre del estado, tal como se lo llama al hablar de él.</summary>
    internal required string Name { get; init; }

    /// <summary>Lo que dice la píldora. No siempre coincide con el nombre: «revisar» se muestra «necesita tu ok».</summary>
    internal required string PillLabel { get; init; }

    /// <summary>Radio general de la nube. Uno es el tamaño de reposo.</summary>
    internal double Dispersion { get; init; } = 1.0;

    /// <summary>Cuánto respira cada punto por su cuenta.</summary>
    internal double Wobble { get; init; }

    /// <summary>Giro del conjunto, en radianes por segundo.</summary>
    internal double Yaw { get; init; }

    /// <summary>Giro propio de cada anillo.</summary>
    internal OrbSpin Spin { get; init; }

    /// <summary>Apertura de los anillos. En cero los tres quedan en el mismo plano.</summary>
    internal double Tilt { get; init; }

    /// <summary>Radio de los anillos respecto del núcleo.</summary>
    internal double RingScale { get; init; } = 1.0;

    /// <summary>Cuán lejos llega el polvo.</summary>
    internal double DustRadius { get; init; } = 1.0;

    /// <summary>Cuán rápido circula el polvo.</summary>
    internal double DustSpeed { get; init; }

    /// <summary>Turbulencia del campo que desordena todo lo anterior.</summary>
    internal double Turbulence { get; init; }

    /// <summary>Opacidad general del estado. Es lo que separa «trabajando sin vos» de «pensando».</summary>
    internal double Alpha { get; init; } = 1.0;

    /// <summary>Amplitud de la deformación de la gota.</summary>
    internal double DropAmplitude { get; init; } = 0.030;

    /// <summary>Velocidad de la deformación de la gota.</summary>
    internal double DropSpeed { get; init; } = 0.55;

    /// <summary>Respiración: el cuerpo entero crece y vuelve.</summary>
    internal OrbPulse? Breath { get; init; }

    /// <summary>Golpeteo: dos toques seguidos y silencio. Es «tengo algo para vos» sin palabras.</summary>
    internal OrbPulse? Knock { get; init; }

    /// <summary>Estirón: se alarga buscando algo y vuelve. Cero si el estado no se estira.</summary>
    /// <remarks>
    /// El número es el período que trae la tabla del boceto, pero <b>no manda la cadencia</b>: la
    /// cadencia del estirón es la escalera de reintentos de <see cref="OrbDeafRetry"/>, que no es
    /// periódica —3, 8, 20 y 45 segundos— y va perdiendo fuerza. Acá sólo hace de bandera.
    /// </remarks>
    internal double ReachPeriod { get; init; }

    /// <summary>Temblor rápido, a 11,8 Hz. Es nervio, no respiración.</summary>
    internal double Tremor { get; init; }

    /// <summary>Cada cuántos segundos sale una onda desde el centro. Cero si no hay.</summary>
    internal double ThinkPeriod { get; init; }

    /// <summary>Onda de la voz al hablar.</summary>
    internal OrbPulse? Wave { get; init; }

    /// <summary>Apertura de la «boca» al hablar.</summary>
    internal double Mouth { get; init; }

    /// <summary>Apertura del «oído» al escuchar. Es la contracara de <see cref="Mouth"/>: entra en vez de salir.</summary>
    internal double Ear { get; init; }

    /// <summary>Inclinación hacia el usuario, en unidades de las 70 del lienzo.</summary>
    internal double Lean { get; init; }

    /// <summary>Los anillos se rompen: un tramo de cada vuelta no se dibuja, y se mueve.</summary>
    internal bool IsBroken { get; init; }

    /// <summary>
    /// Un tramo fijo del anillo que no se dibuja. Es el anillo roto sin el temblor: lo que queda del
    /// error cuando se le saca el movimiento.
    /// </summary>
    internal bool HasStaticGap { get; init; }

    /// <summary>Dos cascarones en vez de tres.</summary>
    internal bool IsTwoShells { get; init; }

    /// <summary>Un solo cascarón, sin apertura ni giro propio. Es el cuerpo de la capacidad reducida.</summary>
    internal bool IsSingleShell { get; init; }
}

/// <summary>
/// La tabla de los quince estados, medida del boceto de referencia.
/// </summary>
/// <remarks>
/// Ningún número de acá es una interpretación: salen de <c>PAL</c> y <c>GOTA</c> del fuente del
/// rework. Si algo se ve distinto al boceto, el problema está en cómo se dibuja, no acá.
/// </remarks>
internal static class OrbPalette
{
    // Las duraciones de los cambios de estado ya no viven acá: están en OrbTransitions, que las
    // tiene par por par. Un solo número para los quince estados era lo que había hasta que se portó
    // la tabla, y era justamente lo que el boceto pedía no hacer.

    private static readonly OrbStateProfile IdleProfile = new()
    {
        Body = Rgb(0x72, 0xD9, 0xFF),
        Depth = Rgb(0x17, 0x60, 0x7F),
        Name = "reposo",
        PillLabel = "acá estoy",
        Dispersion = 1.00,
        Wobble = 0.015,
        Yaw = 0.055,
        Spin = new OrbSpin(0.130, -0.118, 0.110),
        Tilt = 0.92,
        RingScale = 1.00,
        DustRadius = 1.16,
        DustSpeed = 0.11,
        Turbulence = 0.6,
        Breath = new OrbPulse(0.022, 4.2),
        DropAmplitude = 0.030,
        DropSpeed = 0.55
    };

    private static readonly OrbStateProfile WatchingProfile = new()
    {
        Body = Rgb(0x5F, 0xB3, 0x9A),
        Depth = Rgb(0x14, 0x57, 0x4A),
        Name = "guardia",
        PillLabel = "atenta al nombre",
        Dispersion = 0.94,
        Wobble = 0.010,
        Yaw = 0.045,
        Spin = new OrbSpin(0.090, -0.082, 0.076),
        Tilt = 0.55,
        RingScale = 0.92,
        DustRadius = 1.02,
        DustSpeed = 0.08,
        Turbulence = 0.40,
        Breath = new OrbPulse(0.016, 3.4),
        Alpha = 0.86,
        DropAmplitude = 0.026,
        DropSpeed = 0.48
    };

    private static readonly OrbStateProfile ListeningProfile = new()
    {
        Body = Rgb(0x72, 0xF0, 0xC0),
        Depth = Rgb(0x16, 0x6B, 0x54),
        Name = "escuchando",
        PillLabel = "te escucho",
        Dispersion = 1.06,
        Wobble = 0.018,
        Yaw = 0.095,
        Spin = new OrbSpin(0.170, -0.154, 0.143),
        Tilt = 2.15,
        RingScale = 1.11,
        DustRadius = 1.38,
        DustSpeed = 0.20,
        Turbulence = 1.0,
        Breath = new OrbPulse(0.030, 2.4),
        Tremor = 0.006,
        Ear = 0.055,
        DropAmplitude = 0.078,
        DropSpeed = 1.05
    };

    private static readonly OrbStateProfile ThinkingProfile = new()
    {
        Body = Rgb(0x9B, 0xB7, 0xFF),
        Depth = Rgb(0x30, 0x44, 0x86),
        Name = "pensando",
        PillLabel = "pensando",
        Dispersion = 1.02,
        Wobble = 0.026,
        Yaw = 0.30,
        Spin = new OrbSpin(0.540, -0.492, 0.456),
        Tilt = 1.18,
        RingScale = 0.97,
        DustRadius = 1.12,
        DustSpeed = 0.58,
        Turbulence = 2.1,
        ThinkPeriod = 0.95,
        DropAmplitude = 0.135,
        DropSpeed = 2.10
    };

    // ---------------------------------------------------------------------------------------------
    // Los tres que se parecían: trabajando sin vos, un proyecto te espera y esperándote.
    //
    // El boceto los dejó explícitamente sin resolver —de reojo se confunden— y ofrecía separarlos
    // por color o por cadencia. Van separados por CADENCIA Y BRILLO, no por color: cada uno se queda
    // con el color que tiene en PAL. Cambiar el color sería inventar un cuarto significado; lo que
    // hay que decir ya está dicho y es otra cosa.
    //
    // Se separan por PRESENCIA, o sea por qué espera cada uno del usuario:
    //
    //   trabajando sin vos      no espera nada de nadie. Es el más apagado (0,70) y es el único de
    //                           los tres que no respira: su pulso —una onda cada 1,7 s— viene del
    //                           trabajo, no de la espera. No te está mirando.
    //
    //   un proyecto te espera   espera a OTRO, no a vos. Brillo intermedio (0,82) y el golpeteo cada
    //                           2,6 s, más suave que antes (0,044): son dos golpes en la puerta de
    //                           al lado. Se oyen, pero no son para vos.
    //
    //   esperándote             es el único que te debe algo. El más presente de los tres (0,94) y
    //                           el único con una respiración profunda (0,048 cada 4,6 s). Está vivo
    //                           y está esperando, y eso tiene que verse antes que los otros dos.
    //
    // Los tres números de brillo ya estaban en la tabla: 0,70 es el de trabajando sin vos, 0,82 el
    // de sorda y sin clave, 0,94 el que tenía un proyecto te espera. Están repartidos de nuevo, no
    // inventados. Lo mismo con las amplitudes: 0,044 es la de pidiendo permiso y 0,048 la de revisar.
    //
    // El próximo que mire estos tres va a querer "arreglarlos" para que se parezcan, porque los tres
    // son formas de esperar. No: los tres son formas de esperar COSAS DISTINTAS, y el usuario tiene
    // que poder saber cuál de las tres es sin acercarse a leer la píldora.
    // ---------------------------------------------------------------------------------------------

    private static readonly OrbStateProfile BackgroundProfile = new()
    {
        Body = Rgb(0x7E, 0x90, 0xC4),
        Depth = Rgb(0x26, 0x32, 0x5E),
        Name = "trabajando sin vos",
        PillLabel = "trabajando sin vos",
        Dispersion = 0.98,
        Wobble = 0.018,
        Yaw = 0.16,
        Spin = new OrbSpin(0.300, -0.100, 0.088),
        Tilt = 0.85,
        RingScale = 0.95,
        DustRadius = 1.05,
        DustSpeed = 0.30,
        Turbulence = 1.20,
        ThinkPeriod = 1.7,
        Alpha = 0.70,
        DropAmplitude = 0.070,
        DropSpeed = 1.60
    };

    private static readonly OrbStateProfile SpeakingProfile = new()
    {
        Body = Rgb(0xFF, 0xCE, 0x82),
        Depth = Rgb(0x7D, 0x54, 0x1C),
        Name = "hablando",
        PillLabel = "hablando",
        Dispersion = 1.05,
        Wobble = 0.022,
        Yaw = 0.14,
        Spin = new OrbSpin(0.180, -0.164, 0.152),
        Tilt = 0.12,
        RingScale = 1.07,
        DustRadius = 1.26,
        DustSpeed = 0.28,
        Turbulence = 1.3,
        Wave = new OrbPulse(0.075, 0.30),
        Mouth = 0.14,
        DropAmplitude = 0.105,
        DropSpeed = 1.35
    };

    private static readonly OrbStateProfile InterruptedProfile = new()
    {
        Body = Rgb(0xC9, 0xB0, 0x8A),
        Depth = Rgb(0x5C, 0x4A, 0x2E),
        Name = "interrumpida",
        PillLabel = "me callo",
        Dispersion = 0.93,
        Wobble = 0.006,
        Yaw = 0.020,
        Spin = new OrbSpin(0.030, -0.027, 0.025),
        Tilt = 0.30,
        RingScale = 0.90,
        DustRadius = 0.86,
        DustSpeed = 0.03,
        Turbulence = 0.20,
        Alpha = 0.86,
        DropAmplitude = 0.040,
        DropSpeed = 0.60
    };

    private static readonly OrbStateProfile WaitingForYouProfile = new()
    {
        Body = Rgb(0xD9, 0xA9, 0x68),
        Depth = Rgb(0x6E, 0x4F, 0x1E),
        Name = "esperándote",
        PillLabel = "te espero",
        Dispersion = 0.90,
        Wobble = 0.008,
        Yaw = 0.012,
        Spin = new OrbSpin(0.020, -0.018, 0.017),
        Tilt = 0.45,
        RingScale = 0.88,
        DustRadius = 0.95,
        DustSpeed = 0.04,
        Turbulence = 0.30,

        // Respiración profunda y lenta: el único de los tres que espera algo de vos. Ver el bloque
        // de arriba de «trabajando sin vos».
        Breath = new OrbPulse(0.048, 4.6),
        Alpha = 0.94,
        DropAmplitude = 0.022,
        DropSpeed = 0.30
    };

    private static readonly OrbStateProfile ProjectWaitingProfile = new()
    {
        Body = Rgb(0xB7, 0x9B, 0xFF),
        Depth = Rgb(0x3A, 0x2C, 0x6E),
        Name = "un proyecto te espera",
        PillLabel = "un proyecto te espera",
        Dispersion = 1.00,
        Wobble = 0.012,
        Yaw = 0.050,
        Spin = new OrbSpin(0.070, -0.064, 0.060),
        Tilt = 0.70,
        RingScale = 1.00,
        DustRadius = 1.10,
        DustSpeed = 0.10,
        Turbulence = 0.50,

        // Golpea más suave que antes: los dos golpes son en la puerta de al lado. Ver el bloque de
        // arriba de «trabajando sin vos».
        Knock = new OrbPulse(0.044, 2.6),
        Alpha = 0.82,
        DropAmplitude = 0.052,
        DropSpeed = 0.70
    };

    private static readonly OrbStateProfile AttentionProfile = new()
    {
        Body = Rgb(0xFF, 0xB3, 0x47),
        Depth = Rgb(0x7D, 0x4D, 0x13),
        Name = "revisar",
        PillLabel = "necesita tu ok",
        Dispersion = 0.87,
        Wobble = 0.011,
        Yaw = 0.022,
        Spin = new OrbSpin(0.024, -0.022, 0.020),
        Tilt = 0.22,
        RingScale = 0.83,
        DustRadius = 0.90,
        DustSpeed = 0.06,
        Turbulence = 0.5,
        Breath = new OrbPulse(0.048, 1.7),
        DropAmplitude = 0.042,
        DropSpeed = 0.45
    };

    private static readonly OrbStateProfile AskingPermissionProfile = new()
    {
        Body = Rgb(0xFF, 0x9E, 0x5E),
        Depth = Rgb(0x7A, 0x3F, 0x16),
        Name = "pidiendo permiso",
        PillLabel = "pido permiso",
        Dispersion = 0.90,
        Wobble = 0.010,
        Yaw = 0.015,
        Spin = new OrbSpin(0.020, -0.018, 0.017),
        Tilt = 0.38,
        RingScale = 0.86,
        DustRadius = 0.92,
        DustSpeed = 0.05,
        Turbulence = 0.40,
        Breath = new OrbPulse(0.044, 2.6),
        Lean = 0.085,
        DropAmplitude = 0.048,
        DropSpeed = 0.50
    };

    private static readonly OrbStateProfile DeafProfile = new()
    {
        Body = Rgb(0x9F, 0xB3, 0xBE),
        Depth = Rgb(0x3E, 0x4E, 0x56),
        Name = "sorda",
        PillLabel = "no te oigo",
        Dispersion = 0.96,
        Wobble = 0.010,
        Yaw = 0.030,
        Spin = new OrbSpin(0.050, -0.045, 0.042),
        Tilt = 0.62,
        RingScale = 0.94,
        DustRadius = 0.74,
        DustSpeed = 0.05,
        Turbulence = 0.35,
        ReachPeriod = 1.9,
        Alpha = 0.82,
        DropAmplitude = 0.034,
        DropSpeed = 0.40
    };

    private static readonly OrbStateProfile ErrorProfile = new()
    {
        Body = Rgb(0xFF, 0x73, 0x85),
        Depth = Rgb(0x78, 0x26, 0x32),
        Name = "error",
        PillLabel = "algo falló",
        Dispersion = 1.16,
        Wobble = 0.048,
        Yaw = 0.19,
        Spin = new OrbSpin(0.290, -0.264, 0.245),
        Tilt = 1.52,
        RingScale = 1.22,
        DustRadius = 1.90,
        DustSpeed = 0.70,
        Turbulence = 2.8,
        IsBroken = true,
        Tremor = 0.022,
        DropAmplitude = 0.235,
        DropSpeed = 2.80
    };

    private static readonly OrbStateProfile OfflineProfile = new()
    {
        Body = Rgb(0xE9, 0xEF, 0xF2),
        Depth = Rgb(0x93, 0xA3, 0xAE),
        Name = "sin red",
        PillLabel = "sin red",
        Dispersion = 0.98,
        Wobble = 0.007,
        Yaw = 0.028,
        Spin = new OrbSpin(0.030, -0.027, 0.025),
        Tilt = 0.0,
        RingScale = 0.94,
        DustRadius = 1.02,
        DustSpeed = 0.03,
        Turbulence = 0.15,
        IsSingleShell = true,
        DropAmplitude = 0.014,
        DropSpeed = 0.16
    };

    private static readonly OrbStateProfile UnconfiguredProfile = new()
    {
        Body = Rgb(0xD8, 0xD2, 0xC6),
        Depth = Rgb(0x8A, 0x83, 0x75),
        Name = "sin clave",
        PillLabel = "sin clave",
        Dispersion = 0.95,
        Wobble = 0.004,
        Yaw = 0.006,
        Spin = new OrbSpin(0.006, -0.005, 0.005),
        Tilt = 0.20,
        RingScale = 0.90,
        DustRadius = 0.94,
        DustSpeed = 0.012,
        Turbulence = 0.10,
        IsSingleShell = true,
        Alpha = 0.82,
        DropAmplitude = 0.010,
        DropSpeed = 0.12
    };

    /// <summary>El perfil completo de un estado.</summary>
    internal static OrbStateProfile For(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Watching => WatchingProfile,
        AssistantVisualState.Listening => ListeningProfile,
        AssistantVisualState.Thinking => ThinkingProfile,
        AssistantVisualState.Background => BackgroundProfile,
        AssistantVisualState.Speaking => SpeakingProfile,
        AssistantVisualState.Interrupted => InterruptedProfile,
        AssistantVisualState.WaitingForYou => WaitingForYouProfile,
        AssistantVisualState.ProjectWaiting => ProjectWaitingProfile,
        AssistantVisualState.Attention => AttentionProfile,
        AssistantVisualState.AskingPermission => AskingPermissionProfile,
        AssistantVisualState.Deaf => DeafProfile,
        AssistantVisualState.Error => ErrorProfile,
        AssistantVisualState.Offline => OfflineProfile,
        AssistantVisualState.Unconfigured => UnconfiguredProfile,
        _ => IdleProfile
    };

    /// <summary>
    /// Mezcla dos perfiles. Sólo se interpola lo que es una cantidad; lo demás salta al destino.
    /// </summary>
    /// <remarks>
    /// Un anillo roto no es medio anillo roto, y un golpeteo no es media respiración. Los gestos y
    /// las banderas se toman del destino y punto: el salto queda tapado por la transición cromática,
    /// que es la que el ojo mira.
    /// </remarks>
    internal static OrbStateProfile Lerp(OrbStateProfile start, OrbStateProfile target, double t) => target with
    {
        Body = LerpColor(start.Body, target.Body, t),
        Depth = LerpColor(start.Depth, target.Depth, t),
        Dispersion = Mix(start.Dispersion, target.Dispersion, t),
        Wobble = Mix(start.Wobble, target.Wobble, t),
        Tilt = Mix(start.Tilt, target.Tilt, t),
        RingScale = Mix(start.RingScale, target.RingScale, t),
        DustRadius = Mix(start.DustRadius, target.DustRadius, t),
        DustSpeed = Mix(start.DustSpeed, target.DustSpeed, t),
        Turbulence = Mix(start.Turbulence, target.Turbulence, t),
        Alpha = Mix(start.Alpha, target.Alpha, t),
        DropAmplitude = Mix(start.DropAmplitude, target.DropAmplitude, t),
        DropSpeed = Mix(start.DropSpeed, target.DropSpeed, t)
    };

    /// <summary>Interpolación coseno: arranca y termina quieta.</summary>
    internal static double SineInOut(double t) => 0.5 - (Math.Cos(Math.PI * Math.Clamp(t, 0, 1)) / 2);

    /// <summary>Mezcla dos colores por componente.</summary>
    internal static Color LerpColor(Color start, Color target, double t) => Color.FromRgb(
        (byte)Math.Clamp(start.R + ((target.R - start.R) * t), 0, 255),
        (byte)Math.Clamp(start.G + ((target.G - start.G) * t), 0, 255),
        (byte)Math.Clamp(start.B + ((target.B - start.B) * t), 0, 255));

    /// <summary>Aclara un color hacia el blanco.</summary>
    internal static Color Lighten(Color color, double amount) => LerpColor(color, Colors.White, amount);

    /// <summary>El color de un estado, ya mezclado con el tinte de un ánimo si hay uno vivo.</summary>
    internal static Color Wash(Color color, Color? tint, double amount) =>
        tint is { } value && amount > 0.002 ? LerpColor(color, value, Math.Min(0.88, amount)) : color;

    private static double Mix(double start, double target, double t) => start + ((target - start) * t);

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
