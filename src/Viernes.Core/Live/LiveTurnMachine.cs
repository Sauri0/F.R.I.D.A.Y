namespace Viernes.Core.Live;

/// <summary>En qué anda el turno en vivo.</summary>
public enum LiveTurnState
{
    /// <summary>No hay respuesta en curso. La persona puede hablar cuando quiera.</summary>
    Idle,

    /// <summary>Está generando y llegando audio.</summary>
    Responding,

    /// <summary>Terminó de generar pero todavía queda audio por reproducir.</summary>
    /// <remarks>
    /// Es un estado propio y no un detalle: si acá se muestra «terminó», la pantalla se apaga
    /// mientras ella sigue hablando. Y todavía se la puede interrumpir, porque lo que queda sonando
    /// es tan interrumpible como lo que se estaba generando.
    /// </remarks>
    Draining,

    /// <summary>La cortaron. Se vació la cola y se espera el cierre del turno.</summary>
    Interrupted
}

/// <summary>El paso que dio la máquina de estados al recibir un mensaje.</summary>
/// <remarks>
/// <see cref="FlushPlayback"/> es la única salida que obliga a hacer algo <em>ya</em>: significa
/// tirar lo que está sonando y lo que está encolado, en ese orden y sin esperar nada más.
/// </remarks>
public readonly record struct LiveTurnTransition(
    LiveTurnState Previous,
    LiveTurnState Current,
    bool FlushPlayback,
    bool TurnEnded)
{
    /// <summary>Si el estado cambió y vale la pena avisarle a la pantalla.</summary>
    public bool Changed => Previous != Current;
}

/// <summary>
/// La máquina de estados de un turno hablado.
/// </summary>
/// <remarks>
/// Existe separada del cliente porque es la parte que hay que poder probar sin red, y porque el
/// orden de los mensajes tiene una trampa que no se ve leyendo la documentación:
/// <para>
/// Un turno normal llega como <c>audio…</c> → <c>generationComplete</c> → <c>turnComplete</c>. Uno
/// interrumpido llega como <c>audio…</c> → <c>interrupted</c> → <c>turnComplete</c>, <b>sin</b>
/// <c>generationComplete</c>. Cualquier cosa que use el generado como señal de «ya casi» se cuelga
/// exactamente en el caso que más importa, que es cuando la persona la cortó.
/// </para>
/// <para>
/// Por eso el único final que se toma en serio es <c>turnComplete</c>.
/// </para>
/// </remarks>
public sealed class LiveTurnMachine
{
    /// <summary>Estado actual del turno.</summary>
    public LiveTurnState State { get; private set; } = LiveTurnState.Idle;

    /// <summary>Cuántas veces la cortaron desde que arrancó la sesión.</summary>
    public int InterruptionCount { get; private set; }

    /// <summary>Cuántos turnos se cerraron.</summary>
    public int CompletedTurns { get; private set; }

    /// <summary>Vuelve todo a cero. Para cuando se reconecta.</summary>
    /// <remarks>
    /// Los contadores no se tocan: son de la sesión que el usuario percibe como una sola charla, y
    /// reconectar por un <c>goAway</c> es un detalle del transporte que no debería borrarlos.
    /// </remarks>
    public void Reset() => State = LiveTurnState.Idle;

    /// <summary>
    /// Hace avanzar el turno con lo que llegó.
    /// </summary>
    /// <remarks>
    /// El orden de las decisiones acá no es cosmético. La interrupción se mira primero que nada,
    /// incluso antes que el audio del mismo mensaje: si en un mensaje viniera audio e
    /// <c>interrupted</c> juntos, encolar ese audio y después vaciar la cola deja la cola vacía —
    /// bien—, pero encolarlo <em>después</em> de vaciar deja sonando justo lo que había que tirar.
    /// </remarks>
    public LiveTurnTransition Apply(LiveServerEvent serverEvent)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);

        var previous = State;
        var flush = false;

        if (serverEvent.Interrupted)
        {
            InterruptionCount++;
            flush = true;
            State = LiveTurnState.Interrupted;
        }
        else if (serverEvent.Audio.Count > 0 || serverEvent.OutputTranscript is not null)
        {
            // Un turno interrumpido puede seguir escupiendo audio ya despachado por el servidor. No
            // se vuelve a Responding desde Interrupted: eso reabriría la reproducción de lo que la
            // persona acaba de mandar a callar.
            if (State is LiveTurnState.Idle or LiveTurnState.Responding)
            {
                State = LiveTurnState.Responding;
            }
        }

        if (serverEvent.GenerationComplete && State == LiveTurnState.Responding)
        {
            State = LiveTurnState.Draining;
        }

        var turnEnded = false;
        if (serverEvent.TurnComplete)
        {
            turnEnded = true;
            CompletedTurns++;
            State = LiveTurnState.Idle;
        }

        return new LiveTurnTransition(previous, State, flush, turnEnded);
    }
}
