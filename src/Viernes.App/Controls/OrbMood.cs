using System.Windows.Media;
using Viernes.Core.Voice;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Color = System.Windows.Media.Color;

namespace Viernes.App.Controls;

/// <summary>Los seis registros de ánimo. Son transitorios: se disparan, duran, y se van.</summary>
/// <remarks>
/// Un ánimo no reemplaza al estado: se le monta encima y al terminar el orbe vuelve exactamente a
/// donde estaba. Por eso son un canal aparte y no siete estados más.
/// <para>
/// Son los mismos seis que ya tiene la voz en <see cref="VoiceMoment"/>, y eso no es coincidencia:
/// que la voz diga «listo» con una cara y el orbe con otra es peor que no tener ninguna de las dos.
/// </para>
/// </remarks>
internal enum OrbMood
{
    /// <summary>¡Listo! Terminó algo. Morfea a un tilde verde.</summary>
    Logro,

    /// <summary>No salió. Se cae, aterriza y se recompone. Sin signo: no hay uno honesto.</summary>
    Tropiezo,

    /// <summary>Urgente. Tres pulsos y un signo de admiración amarillo.</summary>
    Urgente,

    /// <summary>Una pregunta. Se eleva, se ladea y morfea a un interrogante celeste.</summary>
    Pregunta,

    /// <summary>Hola. Un saltito y un saludo con la mano.</summary>
    Saludo,

    /// <summary>Hasta luego. Se apaga y se hunde.</summary>
    Despedida
}

/// <summary>Cuál de los dos cuerpos está pidiendo la coreografía.</summary>
/// <remarks>
/// Los dos hacen el mismo gesto pero no con los mismos números: la nube tiene anillos y polvo que
/// la gota no tiene, y la gota tiene un contorno continuo que la nube no tiene. El gesto es el
/// mismo; el reparto entre canales, no.
/// </remarks>
internal enum OrbBody
{
    /// <summary>La nube de polvo.</summary>
    Nube,

    /// <summary>La gota líquida.</summary>
    Gota
}

/// <summary>El empujón del primer cuadro de un ánimo. Se aplica una sola vez, al dispararlo.</summary>
/// <param name="Ripple">Amplitud de la onda que sale del centro.</param>
/// <param name="Scale">Golpe a la escala del cuerpo entero. Negativo hunde.</param>
/// <param name="KickX">Empujón horizontal al polvo.</param>
/// <param name="KickY">Empujón vertical al polvo.</param>
/// <param name="KickRadial">Empujón radial al polvo: positivo lo expande, negativo lo junta.</param>
internal readonly record struct OrbMoodImpulse(
    double Ripple,
    double Scale,
    double KickX,
    double KickY,
    double KickRadial);

/// <summary>
/// Lo que un ánimo le hace al cuerpo en un instante dado. Todos los canales, ya resueltos.
/// </summary>
/// <remarks>
/// Es deliberadamente ancho: la coreografía de cada ánimo toca ocho o diez canales a la vez, y
/// resumirla en «color y escala» es exactamente lo que hace que un gesto no se lea. El costo de
/// tener veintitantos campos se paga una vez acá y no en cada cuerpo.
/// <para>Los valores neutros son los de «no está pasando nada»: uno donde multiplica, cero donde suma.</para>
/// </remarks>
internal sealed record OrbMoodShape
{
    /// <summary>Ánimo apagado. Es el que se usa cuando no hay ninguno vivo.</summary>
    internal static readonly OrbMoodShape Neutral = new();

    /// <summary>Suma al radio de todo el cuerpo.</summary>
    internal double Extra { get; init; }

    /// <summary>Multiplica la opacidad general.</summary>
    internal double AlphaFactor { get; init; } = 1.0;

    /// <summary>Corrimiento horizontal del cuerpo, en unidades de las 70 del lienzo.</summary>
    internal double OffsetX { get; init; }

    /// <summary>Corrimiento vertical. Positivo baja.</summary>
    internal double OffsetY { get; init; }

    /// <summary>Achatamiento vertical. Uno es sin achatar.</summary>
    internal double SquashY { get; init; } = 1.0;

    /// <summary>Cizalla horizontal: es lo que hace que el aterrizaje se lea como impacto.</summary>
    internal double Shear { get; init; }

    /// <summary>Rotación. En la gota gira el contorno; en la nube gira el enrollado de los anillos.</summary>
    internal double Roll { get; init; }

    /// <summary>Suma a la apertura de los anillos. Sólo la nube.</summary>
    internal double TiltBias { get; init; }

    /// <summary>Multiplica la amplitud de la deformación de la gota.</summary>
    internal double AmplitudeFactor { get; init; } = 1.0;

    /// <summary>Multiplica el radio del núcleo. Bajarlo es lo que deja lugar para el signo.</summary>
    internal double CoreRadius { get; init; } = 1.0;

    /// <summary>Multiplica el radio de los anillos.</summary>
    internal double RingRadius { get; init; } = 1.0;

    /// <summary>Multiplica la opacidad de los anillos.</summary>
    internal double RingAlphaFactor { get; init; } = 1.0;

    /// <summary>Cuánto se tiñe el núcleo con el color del ánimo, de 0 a 1.</summary>
    internal double WashCore { get; init; }

    /// <summary>Cuánto se tiñe el canto.</summary>
    internal double WashRim { get; init; }

    /// <summary>Cuánto se tiñe el polvo.</summary>
    internal double WashDust { get; init; }

    /// <summary>El color del ánimo. Nulo cuando no hay ninguno vivo.</summary>
    internal Color? Tint { get; init; }

    /// <summary>Arrastre vertical del polvo. Negativo lo levanta.</summary>
    internal double DriftY { get; init; }

    /// <summary>Cuánto se descuelga el polvo de atrás. Es lo que dibuja la caída del tropiezo.</summary>
    internal double DropY { get; init; }

    /// <summary>Desfasaje del anillo del medio: el conjunto pierde el paso.</summary>
    internal double Desync { get; init; }

    /// <summary>Multiplica la turbulencia del campo.</summary>
    internal double FlowFactor { get; init; } = 1.0;

    /// <summary>Balanceo lateral del polvo. Es la mano del saludo.</summary>
    internal double Sway { get; init; }

    /// <summary>Amplitud del destello que recorre el cuerpo al saludar.</summary>
    internal double ShimmerAmount { get; init; }

    /// <summary>Fase de ese destello.</summary>
    internal double ShimmerPhase { get; init; }

    /// <summary>Multiplica el brillo del nudo que destella en los anillos.</summary>
    internal double FlareBoost { get; init; } = 1.0;

    /// <summary>Asimetría vertical del polvo: se estira arriba y se junta abajo.</summary>
    internal double AsymmetryY { get; init; }

    /// <summary>Cuánto sube el anillo externo. Acompaña la elevación de la pregunta.</summary>
    internal double RingLift { get; init; }

    /// <summary>El signo al que está morfeando, si hay uno.</summary>
    internal OrbGlyphKind Glyph { get; init; } = OrbGlyphKind.None;

    /// <summary>Cuánto del signo ya está formado, de 0 a 1.</summary>
    internal double GlyphReveal { get; init; }

    /// <summary>Escala del signo.</summary>
    internal double GlyphScale { get; init; } = 1.0;

    /// <summary>
    /// El mismo ánimo con la amplitud escalada por la energía que le presta el estado de fondo.
    /// </summary>
    /// <remarks>
    /// Es lo que hace que «no salió» sea un golpe sobre <em>pensando</em> —energía 1,0— y un suspiro
    /// sobre <em>esperándote</em> —energía 0,45—. Es el mismo ánimo con dos intensidades, no dos
    /// ánimos: la coreografía es una sola y lo único que cambia es cuánto se mueve.
    /// <para>
    /// Se escala <b>sólo lo que es movimiento</b>. El color del ánimo, el signo y la opacidad quedan
    /// enteros a propósito: un interrogante al 45 % sigue teniendo que ser un interrogante, y si el
    /// tinte se diluyera con la energía, sobre los estados apagados —que son justo donde más falta
    /// hace saber qué pasó— el ánimo se volvería invisible.
    /// </para>
    /// </remarks>
    internal OrbMoodShape Scale(double energy)
    {
        if (Math.Abs(energy - 1) < 0.001)
        {
            return this;
        }

        return this with
        {
            OffsetX = OffsetX * energy,
            OffsetY = OffsetY * energy,
            Extra = Extra * energy,
            Shear = Shear * energy,
            Roll = Roll * energy,
            TiltBias = TiltBias * energy,
            AmplitudeFactor = 1 + ((AmplitudeFactor - 1) * energy),
            SquashY = 1 + ((SquashY - 1) * energy)
        };
    }
}

/// <summary>
/// La coreografía de los seis ánimos, canal por canal.
/// </summary>
/// <remarks>
/// Todos los números salen de los bloques <c>mo.k === '...'</c> del boceto, que hay uno por cuerpo.
/// No están unificados porque no son el mismo gesto repartido igual: en la nube el tropiezo se le
/// va en polvo que se descuelga, y en la gota en amplitud de contorno. El gesto se lee igual; el
/// reparto no puede.
/// </remarks>
internal static class OrbMoods
{
    private static readonly Color LogroTint = Color.FromRgb(143, 243, 203);
    private static readonly Color TropiezoTint = Color.FromRgb(138, 143, 153);
    private static readonly Color UrgenteTint = Color.FromRgb(255, 213, 74);
    private static readonly Color PreguntaTint = Color.FromRgb(143, 212, 255);
    private static readonly Color SaludoTint = Color.FromRgb(255, 217, 160);
    private static readonly Color DespedidaTint = Color.FromRgb(126, 144, 196);

    /// <summary>Cuánto dura cada ánimo. Salen de <c>MOOD</c>.</summary>
    /// <remarks>
    /// No duran todos lo mismo y eso importa: «no salió» es corto porque insistir en un tropiezo es
    /// regodearse, y «una pregunta» es el más largo porque tiene que quedar en pantalla el tiempo de
    /// que la mires y entiendas que espera algo.
    /// </remarks>
    internal static TimeSpan Duration(OrbMood mood) => TimeSpan.FromMilliseconds(mood switch
    {
        OrbMood.Logro => 1750,
        OrbMood.Tropiezo => 1250,
        OrbMood.Urgente => 2000,
        OrbMood.Pregunta => 2100,
        OrbMood.Saludo => 1400,
        _ => 1550
    });

    /// <summary>Lo que muestra la píldora mientras dura el ánimo.</summary>
    internal static string Label(OrbMood mood) => mood switch
    {
        OrbMood.Logro => "¡listo!",
        OrbMood.Tropiezo => "no salió",
        OrbMood.Urgente => "urgente",
        OrbMood.Pregunta => "una pregunta",
        OrbMood.Saludo => "hola",
        _ => "hasta luego"
    };

    /// <summary>
    /// El ánimo que le corresponde a un registro de voz.
    /// </summary>
    /// <remarks>
    /// Este es el puente, y es la mitad del valor de todo esto: la voz ya sabe con qué cara está
    /// diciendo lo que dice —<see cref="VoiceRegister"/> lo decide para el sintetizador— y hasta
    /// ahora el orbe no se enteraba. Cuando la voz habla con un registro, el orbe lo acompaña.
    /// <para>
    /// <see cref="VoiceMoment.Normal"/> no tiene ánimo a propósito: una respuesta cualquiera no
    /// merece un gesto, y un asistente que gesticula en cada frase cansa a la tercera.
    /// </para>
    /// </remarks>
    internal static OrbMood? FromVoice(VoiceMoment moment) => moment switch
    {
        VoiceMoment.Logro => OrbMood.Logro,
        VoiceMoment.Tropiezo => OrbMood.Tropiezo,
        VoiceMoment.Urgente => OrbMood.Urgente,
        VoiceMoment.Pregunta => OrbMood.Pregunta,
        VoiceMoment.Saludo => OrbMood.Saludo,
        VoiceMoment.Despedida => OrbMood.Despedida,
        _ => null
    };

    /// <summary>El empujón del primer cuadro.</summary>
    internal static OrbMoodImpulse Impulse(OrbMood mood) => mood switch
    {
        OrbMood.Logro => new OrbMoodImpulse(1.25, 1.4, 0, -6, 40),
        OrbMood.Tropiezo => new OrbMoodImpulse(-0.9, -1.1, 0, 30, -14),
        OrbMood.Urgente => new OrbMoodImpulse(0.85, 0.9, 0, 0, 22),
        OrbMood.Pregunta => new OrbMoodImpulse(0.85, 0.9, 6, -16, 0),
        OrbMood.Saludo => new OrbMoodImpulse(0.85, 0.9, 26, 0, 0),
        _ => new OrbMoodImpulse(0.85, -0.85, 0, 12, -18)
    };

    /// <summary>
    /// La coreografía en el instante <paramref name="u"/>, de 0 a 1 sobre la duración del ánimo.
    /// </summary>
    internal static OrbMoodShape Evaluate(OrbMood mood, double u, OrbBody body)
    {
        if (u < 0 || u > 1)
        {
            return OrbMoodShape.Neutral;
        }

        return mood switch
        {
            OrbMood.Logro => Logro(u, body),
            OrbMood.Tropiezo => Tropiezo(u, body),
            OrbMood.Urgente => Urgente(u, body),
            OrbMood.Pregunta => Pregunta(u, body),
            OrbMood.Saludo => Saludo(u, body),
            _ => Despedida(u, body)
        };
    }

    /// <summary>
    /// ¡Listo!: sube, se abre y escribe un tilde.
    /// </summary>
    /// <remarks>
    /// La envolvente es <c>sin(πu)</c> elevado a 0,42 y no lineal: el gesto tiene que estar arriba
    /// casi todo el tiempo y entrar y salir rápido. Un logro que sube despacio se lee como esfuerzo.
    /// </remarks>
    private static OrbMoodShape Logro(double u, OrbBody body)
    {
        var attack = Math.Pow(Math.Sin(Math.PI * u), 0.42);
        var reveal = Curve(u, 0.06, 0.30, 0.76, 0.98);

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = LogroTint,
                Extra = 0.135 * attack,
                AlphaFactor = 1 + (0.62 * attack),
                FlareBoost = 1 + (3.2 * attack),
                WashCore = 0.62 * attack,
                WashRim = 0.34 * attack,
                WashDust = 0.78 * attack,
                OffsetY = -3.2 * attack,
                TiltBias = 0.15 * attack,
                RingRadius = 1 + (0.10 * attack),
                Glyph = OrbGlyphKind.Check,
                GlyphReveal = reveal
            }
            : new OrbMoodShape
            {
                Tint = LogroTint,
                Extra = 0.115 * attack,
                AlphaFactor = 1 + (0.55 * attack),
                WashCore = 0.60 * attack,
                WashRim = 0.34 * attack,
                OffsetY = -3.2 * attack,
                AmplitudeFactor = 1 + (0.5 * attack),
                CoreRadius = 1 + (0.06 * attack),
                Glyph = OrbGlyphKind.Check,
                GlyphReveal = reveal
            };
    }

    /// <summary>
    /// No salió: cae, aterriza achatada y se recompone.
    /// </summary>
    /// <remarks>
    /// Es el único ánimo sin signo. Los otros dos que no morfean —hola y hasta luego— tampoco, pero
    /// acá la ausencia es deliberada por otra razón: un signo de error sería un reproche, y quien
    /// falló fue ella. El gesto alcanza y no acusa a nadie.
    /// </remarks>
    private static OrbMoodShape Tropiezo(double u, OrbBody body)
    {
        var fall = u < 0.22 ? Math.Pow(u / 0.22, 1.7) : 1;
        var recover = u < 0.55 ? 0 : (u - 0.55) / 0.45;
        var land = Math.Max(0, 1 - (Math.Abs(u - 0.24) / 0.15));
        var wobble = Math.Sin(2 * Math.PI * (u - 0.24) * 2.3) * Math.Max(0, 1 - (Math.Abs(u - 0.32) / 0.34));
        var offsetY = (7.4 * fall * (1 - (recover * 0.93))) + (1.5 * wobble);
        var offsetX = 1.5 * (1 - recover) * Math.Sin(u * 68);

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = TropiezoTint,
                OffsetY = offsetY,
                OffsetX = offsetX,
                SquashY = 1 - (0.36 * land) - (0.09 * fall * (1 - recover)),
                Shear = (0.24 * land) - (0.07 * fall * (1 - recover)),
                WashDust = 0.84 * (1 - (recover * 0.72)),
                WashRim = 0.60 * (1 - (recover * 0.6)),
                WashCore = 0.14,
                AlphaFactor = 1 - (0.36 * (1 - (recover * 0.8))),
                RingAlphaFactor = 1 - (0.28 * (1 - recover)),
                DriftY = 34 * (1 - recover),
                DropY = 26 * fall * (1 - (recover * 0.45)),
                Desync = 0.58 * fall * (1 - recover),
                FlowFactor = 1.5,
                Extra = -0.055 * fall * (1 - recover),
                TiltBias = -0.15 * fall * (1 - recover)
            }
            : new OrbMoodShape
            {
                Tint = TropiezoTint,
                OffsetY = offsetY,
                OffsetX = offsetX,
                SquashY = 1 - (0.40 * land) - (0.10 * fall * (1 - recover)),
                Shear = (0.26 * land) - (0.07 * fall * (1 - recover)),
                WashCore = 0.42 * (1 - (recover * 0.7)),
                WashRim = 0.72 * (1 - (recover * 0.6)),
                AlphaFactor = 1 - (0.30 * (1 - (recover * 0.8))),
                AmplitudeFactor = 1 + (1.5 * land * (1 - recover)),
                DriftY = 34 * (1 - recover)
            };
    }

    /// <summary>
    /// Urgente: tres pulsos y un signo de admiración.
    /// </summary>
    /// <remarks>
    /// Tres y no cinco: son los que caben antes de que el ojo deje de contarlos y empiece a leer
    /// «parpadea». La urgencia está en el ritmo, igual que en la voz.
    /// </remarks>
    private static OrbMoodShape Urgente(double u, OrbBody body)
    {
        var decay = 1 - u;
        var beat = Math.Pow(Math.Abs(Math.Sin(3 * Math.PI * u)), 0.55);
        var reveal = Curve(u, 0.04, 0.17, 0.84, 0.98);

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = UrgenteTint,
                Extra = 0.095 * beat * decay,
                AlphaFactor = 1 + (0.44 * beat * decay),
                SquashY = 1 - (0.09 * beat),
                WashRim = Math.Max(0.82 * beat, 0.94 * reveal),
                WashCore = Math.Max(0.28 * beat, 0.90 * reveal),
                WashDust = Math.Max(0.40 * beat, 0.92 * reveal),
                RingRadius = 1 - (0.07 * (1 - beat)),
                FlareBoost = 1 + (1.4 * beat),
                Glyph = OrbGlyphKind.Bang,
                GlyphReveal = reveal,
                GlyphScale = 1 + (0.11 * beat)
            }
            : new OrbMoodShape
            {
                Tint = UrgenteTint,
                Extra = 0.09 * beat * decay,
                AlphaFactor = 1 + (0.42 * beat * decay),
                SquashY = 1 - (0.10 * beat),
                WashRim = Math.Max(0.85 * beat, 0.92 * reveal),
                WashCore = Math.Max(0.30 * beat, 0.88 * reveal),
                AmplitudeFactor = 1 + (0.7 * beat),
                Glyph = OrbGlyphKind.Bang,
                GlyphReveal = reveal,
                GlyphScale = 1 + (0.11 * beat)
            };
    }

    /// <summary>
    /// Una pregunta: se eleva, se ladea y escribe un interrogante.
    /// </summary>
    /// <remarks>
    /// El <em>lilt</em> del final —el balanceo que aparece pasado el 62 %— es la entonación
    /// ascendente dibujada. Es el mismo recurso que usa la voz, y por eso los dos leen igual.
    /// </remarks>
    private static OrbMoodShape Pregunta(double u, OrbBody body)
    {
        var rise = Math.Min(1, u * 6.5);
        var hold = u < 0.66 ? 1 : Math.Max(0, 1 - ((u - 0.66) / 0.34));
        var raised = rise * hold;
        var lilt = u > 0.62
            ? Math.Sin(2 * Math.PI * (u - 0.62) * 2.4) * Math.Max(0, 1 - ((u - 0.62) / 0.38))
            : 0;
        var reveal = Curve(u, 0.05, 0.26, 0.74, 0.96);

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = PreguntaTint,
                OffsetY = -((5.6 * raised) + (1.9 * lilt)),
                OffsetX = 3.5 * raised,
                TiltBias = 0.48 * raised,
                Roll = 0.21 * raised,
                AsymmetryY = -0.42 * raised,
                CoreRadius = 1 - (0.28 * raised),
                RingLift = 5.6 * raised,
                RingRadius = 1 + (0.06 * raised),
                WashRim = Math.Max(0.56 * raised, 0.94 * reveal),
                WashCore = Math.Max(0.20 * raised, 0.90 * reveal),
                WashDust = Math.Max(0.34 * raised, 0.92 * reveal),
                FlowFactor = 1 - (0.62 * raised),
                Extra = 0.02 * raised,
                Glyph = OrbGlyphKind.Ask,
                GlyphReveal = reveal,
                GlyphScale = 1 + (0.05 * lilt)
            }
            : new OrbMoodShape
            {
                Tint = PreguntaTint,
                OffsetY = -((5.0 * raised) + (1.9 * lilt)),
                OffsetX = 3.2 * raised,
                Roll = 0.20 * raised,
                CoreRadius = 1 - (0.24 * raised),
                AmplitudeFactor = 1 - (0.45 * raised),
                WashRim = Math.Max(0.55 * raised, 0.94 * reveal),
                WashCore = Math.Max(0.22 * raised, 0.90 * reveal),
                Glyph = OrbGlyphKind.Ask,
                GlyphReveal = reveal,
                GlyphScale = 1 + (0.05 * lilt)
            };
    }

    /// <summary>
    /// Hola: un saltito y un saludo con la mano.
    /// </summary>
    /// <remarks>
    /// El saludo va con un desfasaje de 0,14 entre el balanceo del cuerpo y el de la inclinación:
    /// es lo que convierte dos movimientos simultáneos en uno que arrastra al otro, que es como se
    /// mueve una mano de verdad.
    /// </remarks>
    private static OrbMoodShape Saludo(double u, OrbBody body)
    {
        var hop = Math.Max(0, 1 - (u / 0.18));
        var decay = 1 - (u * 0.72);
        var swing = Math.Sin(2 * Math.PI * u * 1.6) * decay;
        var lag = Math.Sin(2 * Math.PI * (u - 0.14) * 1.6) * decay;

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = SaludoTint,
                OffsetY = -3.6 * hop * hop,
                OffsetX = 4.4 * swing,
                Sway = 5.6 * swing,
                ShimmerAmount = 0.60 * decay,
                ShimmerPhase = 2 * Math.PI * u * 1.6,
                TiltBias = 0.21 * lag,
                Roll = 0.17 * lag,
                SquashY = 1 - (0.05 * Math.Abs(swing)),
                WashDust = 0.60 * Math.Abs(swing),
                WashCore = (0.38 * hop) + (0.22 * Math.Abs(swing)),
                WashRim = 0.20 * Math.Abs(lag),
                AlphaFactor = 1 + (0.26 * Math.Abs(swing)) + (0.30 * hop),
                FlareBoost = 1 + (1.2 * hop)
            }
            : new OrbMoodShape
            {
                Tint = SaludoTint,
                OffsetY = -3.6 * hop * hop,
                OffsetX = 3.4 * swing,
                Sway = 6.4 * swing,
                ShimmerAmount = 0.5 * decay,
                ShimmerPhase = 2 * Math.PI * u * 1.6,
                Roll = 0.16 * lag,
                SquashY = 1 - (0.05 * Math.Abs(swing)),
                WashCore = (0.36 * hop) + (0.20 * Math.Abs(swing)),
                WashRim = 0.22 * Math.Abs(lag),
                AlphaFactor = 1 + (0.24 * Math.Abs(swing)) + (0.28 * hop)
            };
    }

    /// <summary>Hasta luego: se apaga, se hunde y se va.</summary>
    private static OrbMoodShape Despedida(double u, OrbBody body)
    {
        var arc = Math.Sin(Math.PI * u);
        var swell = (0.075 * Math.Sin(Math.PI * Math.Min(1, u * 1.25) * 0.8)) - (0.10 * Math.Max(0, u - 0.55));

        return body == OrbBody.Nube
            ? new OrbMoodShape
            {
                Tint = DespedidaTint,
                Extra = swell,
                AlphaFactor = 1 - (0.58 * arc),
                RingAlphaFactor = 1 - (0.55 * Math.Min(1, u * 1.6)),
                DriftY = -20 * u,
                OffsetY = 4.2 * u * u,
                SquashY = 1 - (0.13 * arc),
                WashCore = 0.62 * u,
                WashRim = 0.48 * u,
                WashDust = 0.40 * u,
                RingRadius = 1 - (0.08 * u)
            }
            : new OrbMoodShape
            {
                Tint = DespedidaTint,
                Extra = (0.07 * Math.Sin(Math.PI * Math.Min(1, u * 1.25) * 0.8)) - (0.10 * Math.Max(0, u - 0.55)),
                AlphaFactor = 1 - (0.55 * arc),
                DriftY = -20 * u,
                OffsetY = 4.2 * u * u,
                SquashY = 1 - (0.13 * arc),
                WashCore = 0.60 * u,
                WashRim = 0.48 * u,
                AmplitudeFactor = 1 - (0.3 * u)
            };
    }

    /// <summary>
    /// Una meseta con rampas: sube entre <paramref name="a"/> y <paramref name="b"/>, se queda, y
    /// baja entre <paramref name="c"/> y <paramref name="d"/>.
    /// </summary>
    /// <remarks>
    /// Es la envolvente de todos los signos. Que la subida y la bajada no midan lo mismo es lo que
    /// hace que el signo aparezca de golpe y se disuelva despacio, como tinta.
    /// </remarks>
    private static double Curve(double u, double a, double b, double c, double d) =>
        u < a ? 0
        : u < b ? (u - a) / (b - a)
        : u < c ? 1
        : u < d ? 1 - ((u - c) / (d - c))
        : 0;
}

/// <summary>
/// Lleva la cuenta del ánimo en curso: cuánto lleva, si ya se disparó el empujón y cuándo terminó.
/// </summary>
/// <remarks>
/// Vive acá y no en cada cuerpo porque los dos hacen exactamente lo mismo con él, y porque el
/// contrato «un ánimo se dispara, dura lo que dice la tabla y se apaga solo» tiene que ser uno y
/// no dos. Quien lo dispara no tiene que acordarse de apagarlo.
/// </remarks>
internal sealed class OrbMoodClock
{
    private OrbMood? _mood;
    private double _elapsed;
    private bool _pendingImpulse;

    /// <summary>El ánimo vivo, o nulo si no hay ninguno.</summary>
    internal OrbMood? Current => _mood;

    /// <summary>Dispara un ánimo. Si había otro corriendo, lo reemplaza desde cero.</summary>
    internal void Trigger(OrbMood mood)
    {
        _mood = mood;
        _elapsed = 0;
        _pendingImpulse = true;
    }

    /// <summary>Corta el ánimo en curso sin esperar a que termine.</summary>
    internal void Clear()
    {
        _mood = null;
        _pendingImpulse = false;
    }

    /// <summary>
    /// Devuelve el empujón del primer cuadro una sola vez por disparo.
    /// </summary>
    internal bool TryTakeImpulse(out OrbMoodImpulse impulse)
    {
        if (_pendingImpulse && _mood is { } mood)
        {
            _pendingImpulse = false;
            impulse = OrbMoods.Impulse(mood);
            return true;
        }

        impulse = default;
        return false;
    }

    /// <summary>Avanza el reloj y devuelve la coreografía de este cuadro.</summary>
    internal OrbMoodShape Advance(double delta, OrbBody body)
    {
        if (_mood is not { } mood)
        {
            return OrbMoodShape.Neutral;
        }

        _elapsed += delta;
        var duration = OrbMoods.Duration(mood).TotalSeconds;
        var progress = duration <= 0 ? 1 : _elapsed / duration;
        if (progress >= 1)
        {
            // Se apaga solo. Que el orbe vuelva al estado de fondo sin que nadie lo mande es lo que
            // convierte al ánimo en un gesto y no en otro estado que hay que acordarse de sacar.
            _mood = null;
            return OrbMoodShape.Neutral;
        }

        return OrbMoods.Evaluate(mood, progress, body);
    }
}
