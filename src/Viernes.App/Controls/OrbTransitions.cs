using Viernes.App.ViewModels;

namespace Viernes.App.Controls;

/// <summary>Las cinco curvas con nombre del boceto, más la coseno de las transiciones anónimas.</summary>
/// <remarks>
/// Salen de <c>static shape(</c>. No son variantes de la misma ecuación con otro exponente: dos de
/// ellas se salen del rango 0..1 a propósito —<see cref="Anti"/> por abajo antes de arrancar y por
/// arriba antes de asentarse, <see cref="Step"/> con una meseta en el medio— y eso es justamente lo
/// que un fundido genérico no puede decir.
/// </remarks>
internal enum OrbTransitionCurve
{
    /// <summary>Coseno. Arranca y termina quieta. Es la de las transiciones que no tienen nombre.</summary>
    Cos,

    /// <summary>Anticipa hacia atrás un 13 %, arranca, y se pasa un 17 % antes de asentarse.</summary>
    Anti,

    /// <summary>Dos escalones: cae al 62 %, aguanta, y cae el resto. Es degradación, no error.</summary>
    Step,

    /// <summary>Arranca lento y acelera.</summary>
    In,

    /// <summary>Arranca de golpe y frena.</summary>
    Out,

    /// <summary>Monótona: ni anticipación ni sobrepaso. La de las cosas que tardan.</summary>
    Slow
}

/// <summary>
/// La forma propia de un cambio de estado: cuánto dura, con qué curva, y qué le hace al cuerpo
/// mientras dura.
/// </summary>
/// <remarks>
/// Los campos que no son duración ni curva se aplican multiplicados por la patada
/// (<see cref="OrbTransitionClock.Kick"/>), que decae al cuadrado sobre la duración: son cosas que
/// pasan <em>al entrar</em> y se apagan solas, no propiedades del estado nuevo.
/// </remarks>
internal sealed record OrbTransition
{
    /// <summary>Cuánto dura, en segundos.</summary>
    internal required double Seconds { get; init; }

    /// <summary>Con qué curva recorre el camino.</summary>
    internal required OrbTransitionCurve Curve { get; init; }

    /// <summary>Cuántos píxeles sube al entrar, sobre las 70 unidades del lienzo.</summary>
    internal double Lift { get; init; }

    /// <summary>Cuántos píxeles se hunde al entrar. Es el contrario de <see cref="Lift"/>.</summary>
    internal double Sink { get; init; }

    /// <summary>Destello de opacidad al entrar. Negativo apaga.</summary>
    internal double Flash { get; init; }

    /// <summary>Patada al giro de los anillos. Se apaga sola en una décima.</summary>
    internal double SpinKick { get; init; }

    /// <summary>Amplitud de la onda que sale del centro al entrar. Negativa la manda hacia adentro.</summary>
    internal double Ripple { get; init; } = 1.0;

    /// <summary>
    /// Corte: la transición no acompaña, corta. Deja cola —la última onda sigue viajando y muere
    /// afuera del cuerpo.
    /// </summary>
    internal bool IsCut { get; init; }

    /// <summary>
    /// Encadenada: no reinicia nada. El giro sigue donde estaba y la primera onda del estado nuevo
    /// <em>es</em> la transición.
    /// </summary>
    internal bool IsChained { get; init; }

    /// <summary>El polvo corre hacia atrás un instante: es el gesto de darse vuelta a mirar.</summary>
    internal bool LooksBack { get; init; }
}

/// <summary>
/// La tabla de transiciones, portada de <c>static TR = {</c>.
/// </summary>
/// <remarks>
/// Esto es la mitad de la personalidad del orbe y el boceto lo dice con todas las letras: quince
/// estados con un fundido genérico entre ellos pierden más que catorce estados con transiciones
/// propias. Un cambio de estado no es un color que se mezcla con otro; es un cuerpo que hace algo.
/// <para>
/// La búsqueda es en tres pasos: primero el par exacto, después el comodín del destino, y recién
/// después la genérica de 520 ms. Es decir: <em>de dónde venís</em> importa sólo en los cinco pares
/// donde importa, y en el resto alcanza con a dónde vas.
/// </para>
/// </remarks>
internal static class OrbTransitions
{
    /// <summary>Cuánto corre el polvo hacia atrás cuando la transición mira para atrás.</summary>
    internal const double LookBackSeconds = 0.340;

    /// <summary>Y a qué velocidad relativa. Negativo porque va al revés, y más rápido que de ida.</summary>
    internal const double LookBackFactor = -1.7;

    /// <summary>La transición de las que no tienen nombre.</summary>
    internal static readonly OrbTransition Default = new() { Seconds = 0.520, Curve = OrbTransitionCurve.Cos };

    /// <summary>
    /// Los cinco pares con nombre. Son los que el usuario ve todos los días y los únicos donde de
    /// dónde venís cambia lo que se dibuja.
    /// </summary>
    private static readonly Dictionary<(AssistantVisualState From, AssistantVisualState To), OrbTransition> Pairs = new()
    {
        // Te llamaron por el nombre. Se sobresalta: junta cuerpo hacia atrás, salta 3,6 px, destella
        // y pega la patada de giro. Es el único gesto del repertorio que anticipa antes de moverse.
        [(AssistantVisualState.Watching, AssistantVisualState.Listening)] = new()
        {
            Seconds = 0.470,
            Curve = OrbTransitionCurve.Anti,
            Lift = 3.6,
            Flash = 0.58,
            SpinKick = 1.6,
            Ripple = 1.15,
            LooksBack = true
        },

        // Lo mismo desde reposo, pero más apagado: no estaba atenta, así que no se sobresalta igual.
        [(AssistantVisualState.Idle, AssistantVisualState.Listening)] = new()
        {
            Seconds = 0.470,
            Curve = OrbTransitionCurve.Anti,
            Lift = 3.1,
            Flash = 0.48,
            SpinKick = 1.4,
            Ripple = 1.0,
            LooksBack = true
        },

        // Dejó de escuchar y se puso a pensar: arranca lento y acelera. Es la inversa exacta de la
        // anterior, y por eso se lee como el otro lado del mismo movimiento.
        [(AssistantVisualState.Listening, AssistantVisualState.Thinking)] = new()
        {
            Seconds = 0.410,
            Curve = OrbTransitionCurve.In,
            Flash = 0.10,
            SpinKick = 2.5,
            Ripple = 0.45,
            Sink = 2.2
        },

        // Encadenada. No hay patada de giro: el giro que traía de pensar sigue igual, y la onda que
        // sale al entrar es literalmente la primera sílaba. Si acá hubiera un fundido, hablar se
        // leería como un estado nuevo en vez de como la continuación de haber pensado.
        [(AssistantVisualState.Thinking, AssistantVisualState.Speaking)] = new()
        {
            Seconds = 0.300,
            Curve = OrbTransitionCurve.Out,
            Flash = 0.34,
            Ripple = 0.9,
            IsChained = true
        },

        // 80 ms. Le pediste que pare y para. La onda negativa que deja sigue viajando hacia afuera
        // durante un segundo y medio y muere fuera del cuerpo: eso es la frase que quedó cortada.
        [(AssistantVisualState.Speaking, AssistantVisualState.Interrupted)] = new()
        {
            Seconds = 0.080,
            Curve = OrbTransitionCurve.Out,
            IsCut = true,
            Ripple = -1.45,
            Flash = -0.34
        }
    };

    /// <summary>Por destino. Vale cuando el par exacto no está en la tabla.</summary>
    private static readonly Dictionary<AssistantVisualState, OrbTransition> ByTarget = new()
    {
        [AssistantVisualState.Interrupted] = new()
        {
            Seconds = 0.095,
            Curve = OrbTransitionCurve.Out,
            IsCut = true,
            Ripple = -1.2,
            Flash = -0.28
        },

        // La más lenta de todas y a propósito: pasar a esperarte no es un evento, es resignarse.
        [AssistantVisualState.WaitingForYou] = new()
        {
            Seconds = 0.960,
            Curve = OrbTransitionCurve.Slow,
            Ripple = 0.22,
            Sink = 1.4
        },

        // Dos escalones. Quedarse sorda es perder capacidad de a poco, no fallar de golpe.
        [AssistantVisualState.Deaf] = new()
        {
            Seconds = 0.660,
            Curve = OrbTransitionCurve.Step,
            Ripple = 0.28,
            Flash = -0.16
        },

        [AssistantVisualState.Error] = new()
        {
            Seconds = 0.230,
            Curve = OrbTransitionCurve.Out,
            Flash = 0.72,
            SpinKick = 2.3,
            Ripple = 1.45
        },

        [AssistantVisualState.Watching] = new()
        {
            Seconds = 0.720,
            Curve = OrbTransitionCurve.Slow,
            Ripple = 0.3
        },

        [AssistantVisualState.Idle] = new()
        {
            Seconds = 0.740,
            Curve = OrbTransitionCurve.Slow,
            Ripple = 0.3
        },

        [AssistantVisualState.ProjectWaiting] = new()
        {
            Seconds = 0.430,
            Curve = OrbTransitionCurve.Anti,
            Lift = 1.8,
            Flash = 0.3,
            Ripple = 0.8
        },

        [AssistantVisualState.Speaking] = new()
        {
            Seconds = 0.340,
            Curve = OrbTransitionCurve.Out,
            Flash = 0.3,
            Ripple = 0.85
        }
    };

    /// <summary>La transición que corresponde a este cambio.</summary>
    /// <param name="from">De dónde viene. Nulo en el primer cuadro, cuando no viene de ningún lado.</param>
    /// <param name="to">A dónde va.</param>
    internal static OrbTransition For(AssistantVisualState? from, AssistantVisualState to)
    {
        if (from is { } origin && Pairs.TryGetValue((origin, to), out var pair))
        {
            return pair;
        }

        return ByTarget.TryGetValue(to, out var target) ? target : Default;
    }

    /// <summary>
    /// El avance de la transición en el instante <paramref name="u"/>, de 0 a 1.
    /// </summary>
    /// <remarks>
    /// Devuelve valores fuera de 0..1 en dos curvas y eso no es un error: la anticipación va a
    /// −0,13 antes de arrancar y el sobrepaso pasa de 1 antes de asentarse. Quien la use para
    /// mezclar colores tiene que recortar; quien la use para mover el cuerpo, no.
    /// </remarks>
    internal static double Shape(OrbTransitionCurve curve, double u)
    {
        switch (curve)
        {
            case OrbTransitionCurve.Anti:
            {
                // El primer quinto va para el otro lado. Es lo que hace que el salto se lea como
                // sobresalto y no como que alguien la empujó.
                if (u < 0.20)
                {
                    return -0.13 * Math.Sin(Math.PI * u / 0.20);
                }

                var v = (u - 0.20) / 0.80;
                return 1 - Math.Pow(1 - v, 2.2) + (0.17 * Math.Sin(Math.PI * v) * (1 - v));
            }

            case OrbTransitionCurve.Step:
            {
                if (u < 0.34)
                {
                    return u / 0.34 * 0.62;
                }

                // La meseta. Sin ella los dos escalones se leerían como uno solo mal amortiguado.
                if (u < 0.52)
                {
                    return 0.62;
                }

                var v = (u - 0.52) / 0.48;
                return 0.62 + (0.38 * (1 - Math.Pow(1 - v, 2)));
            }

            case OrbTransitionCurve.In:
                return u * u * u * (4 - (3 * u));

            case OrbTransitionCurve.Out:
                return 1 - Math.Pow(1 - u, 2.7);

            case OrbTransitionCurve.Slow:
                return u * u * (3 - (2 * u));

            default:
                return 0.5 - (0.5 * Math.Cos(Math.PI * u));
        }
    }
}

/// <summary>
/// El reloj de una transición en vuelo: cuánto lleva, dónde va la curva y qué queda de la patada.
/// </summary>
/// <remarks>
/// Vive fuera de los cuerpos porque la nube y la gota tienen que estar en el mismo punto de la misma
/// transición aunque dibujen cosas distintas. Y arranca terminado a propósito: en el primer cuadro
/// no hay transición de nada, hay un estado.
/// </remarks>
internal sealed class OrbTransitionClock
{
    private double _elapsed = double.MaxValue;
    private double _spinBurst;

    /// <summary>La transición en vuelo.</summary>
    internal OrbTransition Spec { get; private set; } = OrbTransitions.Default;

    /// <summary>Segundos desde que arrancó.</summary>
    internal double Elapsed => _elapsed;

    /// <summary>Ya terminó.</summary>
    internal bool IsDone => _elapsed >= Spec.Seconds;

    /// <summary>Cuánto lleva recorrido en tiempo, de 0 a 1.</summary>
    internal double Progress => Spec.Seconds <= 0 ? 1 : Math.Min(1, _elapsed / Spec.Seconds);

    /// <summary>Cuánto lleva recorrido en forma. Puede salirse de 0..1: ver <see cref="OrbTransitions.Shape"/>.</summary>
    internal double Eased => OrbTransitions.Shape(Spec.Curve, Progress);

    /// <summary>Lo mismo, recortado. Es lo que se usa para mezclar colores.</summary>
    internal double EasedClamped => Math.Clamp(Eased, 0, 1);

    /// <summary>
    /// La patada de entrada: uno al arrancar, cero al 95 % de la duración, al cuadrado en el medio.
    /// </summary>
    /// <remarks>
    /// El 95 % y no el 100 % es lo que hace que la patada esté apagada <em>antes</em> de que la
    /// transición termine. Si las dos cosas terminaran juntas, el último cuadro tendría un salto.
    /// </remarks>
    internal double Kick => Math.Pow(Math.Max(0, 1 - (_elapsed / (Spec.Seconds * 0.95))), 2);

    /// <summary>El polvo todavía está corriendo hacia atrás.</summary>
    internal bool IsLookingBack => Spec.LooksBack && _elapsed < OrbTransitions.LookBackSeconds;

    /// <summary>Arranca la transición que corresponda a este cambio.</summary>
    internal void Begin(AssistantVisualState? from, AssistantVisualState to)
    {
        Spec = OrbTransitions.For(from, to);
        _elapsed = 0;

        // La patada de giro no la aplica el reloj: la consume el cuerpo, que es el que sabe cuántos
        // anillos tiene y para qué lado gira cada uno.
        _spinBurst = Spec.SpinKick;
    }

    /// <summary>Avanza el reloj.</summary>
    internal void Advance(double delta) => _elapsed += delta;

    /// <summary>
    /// Toma lo que queda de la patada de giro y la apaga un poco más.
    /// </summary>
    /// <remarks>
    /// Devuelve cero cuando ya no queda nada. El decaimiento es exponencial con base 0,05 por
    /// segundo, que la deja en nada a la décima: una patada, no una aceleración.
    /// </remarks>
    internal double ConsumeSpin(double delta)
    {
        if (_spinBurst <= 0.002)
        {
            return 0;
        }

        var burst = _spinBurst;
        _spinBurst *= Math.Pow(0.05, delta);
        return burst;
    }
}
