using System.Text;

namespace Viernes.Core.Live;

/// <summary>
/// Junta en un solo pedido los tramos en que quedó partido lo que dijo la persona.
/// </summary>
/// <remarks>
/// El servidor cierra el turno solo, con su propio detector de voz, apenas junta el silencio
/// configurado (<see cref="GeminiLiveOptions.SilenceDurationMs"/>). Eso es lo correcto para bajar la
/// latencia y es lo que hace posible interrumpirla — y es también lo que parte una sola cosa dicha en
/// dos pedidos sueltos: la persona respira en el medio de la frase, el servidor da la frase por
/// terminada, y lo que sigue nace como un turno nuevo. Del lado de acá no hay forma de deshacer ese
/// corte: cuando llega, ya está hecho del otro lado del socket.
/// <para>
/// Lo que sí se puede es no repetirlo acá adentro. Sin esto, <em>una</em> oración partida por una
/// pausa quedaba anotada como dos turnos distintos en la charla, y la burbuja borraba la primera
/// mitad para escribir la segunda: la persona veía cortarse lo que estaba diciendo. Con esto, los
/// tramos se suman y lo que se anota y se dibuja es la frase entera.
/// </para>
/// <para>
/// Lo que cierra el pedido no es una pausa: es que ella haya llegado a contestar entero. Ver
/// <see cref="ClosesUtterance"/>.
/// </para>
/// </remarks>
public sealed class LiveUtterance
{
    /// <summary>
    /// Cuánto puede quedar un pedido abierto sin que la persona agregue nada.
    /// </summary>
    /// <remarks>
    /// <b>Sin esto, un pedido abierto no se cierra nunca.</b> Lo único que lo cierra es que ella
    /// llegue a contestar entera, y si la cortaron y la persona se fue, eso no pasa nunca: lo que se
    /// dijera media hora más tarde se pegaba a lo de antes como si fuera la misma frase.
    /// <para>
    /// Cuarenta y cinco segundos porque el hueco que hay que tolerar no es el de una pausa para
    /// respirar, es el de una interrupción: la persona la corta, se queda pensando, y sigue. Eso
    /// puede llevar medio minuto largo y sigue siendo la misma frase. Volver dos minutos después,
    /// no.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromSeconds(45);

    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Los eventos llegan del hilo que lee del servidor y del hilo del micrófono, indistintamente.
    /// </summary>
    /// <remarks>
    /// El momento del orbe lo puede mover cualquiera de los dos —el servidor con un
    /// <c>turnComplete</c>, el micrófono con un borde del detector de voz— así que dos hilos pueden
    /// estar sumando tramos a la vez. Es una lista chica y el candado se toma una vez por frase, no
    /// por bloque de audio.
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly List<string> _parts = [];

    private readonly TimeSpan _maxGap;

    private readonly TimeProvider _time;

    private long _lastAdded;

    /// <param name="maxGap">
    /// Cuánto silencio de la persona da por perdido el pedido abierto. Ver
    /// <see cref="DefaultMaxGap"/>.
    /// </param>
    /// <param name="timeProvider">El reloj. En las pruebas se le pasa uno de mentira.</param>
    public LiveUtterance(TimeSpan? maxGap = null, TimeProvider? timeProvider = null)
    {
        _maxGap = maxGap ?? DefaultMaxGap;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Lo que dejó sumar un tramo: el pedido entero, si continúa uno abierto, y qué se dio por
    /// perdido.
    /// </summary>
    /// <param name="Text">Todo lo que la persona lleva dicho en este pedido.</param>
    /// <param name="Continued">
    /// Si esto continúa un pedido que ya venía abierto. El llamador anota un turno nuevo o corrige
    /// el último según esto, y por eso la decisión se toma acá adentro y no allá: allá habría que
    /// reconstruirla mirando <see cref="IsOpen"/> justo antes de sumar, y una regla reconstruida
    /// afuera es una regla que se puede reconstruir mal —y que las pruebas de acá no cubrirían—.
    /// </param>
    /// <param name="Expired">
    /// Lo que se dio por perdido por silencio, o <c>null</c> si no se perdió nada. Que no sea nulo
    /// significa que lo que quedó escrito en pantalla es de otra frase y hay que borrarlo.
    /// </param>
    public readonly record struct Added(string Text, bool Continued, string? Expired);

    /// <summary>Si hay un pedido abierto esperando que lo terminen de decir.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _parts.Count > 0;
            }
        }
    }

    /// <summary>En cuántos tramos vino partido el pedido abierto.</summary>
    /// <remarks>
    /// Más de uno significa que el servidor cortó de más. Sirve para poder decirlo en la bitácora sin
    /// escribir ahí lo que la persona dijo.
    /// </remarks>
    public int Parts
    {
        get
        {
            lock (_gate)
            {
                return _parts.Count;
            }
        }
    }

    /// <summary>El pedido entero como quedó hasta ahora, o cadena vacía si no hay ninguno abierto.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return Join(_parts);
            }
        }
    }

    /// <summary>
    /// Suma un tramo y devuelve el pedido entero hasta ahora.
    /// </summary>
    /// <remarks>
    /// Los tramos se pegan con un espacio y nada más. La tentación es limpiar el punto final que el
    /// servidor le puso al tramo anterior —es un punto que la persona no dijo, lo puso el corte—,
    /// pero distinguir ese punto de uno de verdad pide adivinar, y adivinar mal borra puntuación que
    /// sí estaba. Un punto de más se lee; media frase perdida, no.
    /// </remarks>
    /// <param name="fragment">El tramo que acaba de cerrar el servidor.</param>
    public Added Add(string? fragment)
    {
        lock (_gate)
        {
            var ahora = _time.GetUtcNow().UtcTicks;
            string? vencido = null;

            if (_parts.Count > 0 && new TimeSpan(ahora - _lastAdded) >= _maxGap)
            {
                vencido = Join(_parts);
                _parts.Clear();
            }

            var continua = _parts.Count > 0;
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                _parts.Add(fragment.Trim());
                _lastAdded = ahora;
            }

            return new Added(Join(_parts), continua, vencido);
        }
    }

    /// <summary>
    /// El pedido quedó contestado: lo que venga después empieza uno nuevo.
    /// </summary>
    /// <returns>Lo que había abierto, o <c>null</c> si no había nada.</returns>
    public string? Close()
    {
        lock (_gate)
        {
            if (_parts.Count == 0)
            {
                return null;
            }

            var whole = Join(_parts);
            _parts.Clear();
            return whole;
        }
    }

    /// <summary>Tira lo que haya abierto. Para cuando se cierra o se reabre la conversación.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _parts.Clear();
        }
    }

    /// <summary>
    /// Si una respuesta que terminó así da por cerrado lo que la persona estaba pidiendo.
    /// </summary>
    /// <remarks>
    /// <b>La regla en una línea:</b> lo único que cierra el pedido de la persona es que ella haya
    /// llegado a contestar entero, o sea volver a «te escucho» viniendo de «hablando».
    /// <para>
    /// Los otros dos caminos de vuelta a «te escucho» no cierran nada, y son justamente los dos que
    /// el usuario describió como «intenta responder ambas separado»:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     Desde <see cref="LiveOrbMoment.Interrupted"/>: la cortaron hablándole encima. Lo que le
    ///     están diciendo ahora es la continuación de lo de antes — «que lo sume a lo que escuchó
    ///     antes de ser interrumpida», con las palabras del usuario.
    ///   </item>
    ///   <item>
    ///     Desde <see cref="LiveOrbMoment.Thinking"/>: el turno nació y murió sin que saliera una
    ///     sola palabra. Casi siempre es la pausa para respirar que el servidor tomó por punto
    ///     final; en la bitácora del usuario hay uno de estos que duró 340 ms.
    ///   </item>
    /// </list>
    /// <para>
    /// <b>Ese último caso no es sólo la pausa.</b> Un turno que se contesta usando una herramienta y
    /// se cierra sin decir nada en voz alta vuelve por el mismo camino, y acá no hay forma de
    /// distinguirlo: los dos son «pensando → te escucho». Se lo trata como pausa a propósito —si
    /// contestó sin hablar, la persona no escuchó ninguna respuesta y lo que dice a continuación
    /// sigue siendo lo mismo que venía pidiendo—. Lo que impide que eso se estire para siempre no
    /// es esta regla sino <see cref="DefaultMaxGap"/>.
    /// </para>
    /// </remarks>
    /// <param name="previous">Cómo estaba el orbe.</param>
    /// <param name="current">Cómo quedó.</param>
    public static bool ClosesUtterance(LiveOrbMoment previous, LiveOrbMoment current) =>
        current == LiveOrbMoment.Listening && previous == LiveOrbMoment.Speaking;

    private static string Join(List<string> parts)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        var texto = new StringBuilder();
        foreach (var part in parts)
        {
            foreach (var word in part.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (texto.Length > 0)
                {
                    texto.Append(' ');
                }

                texto.Append(word);
            }
        }

        return texto.ToString();
    }
}
