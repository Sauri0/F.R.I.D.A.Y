namespace Viernes.Core.Live;

/// <summary>
/// El momento de la charla en vivo, dicho en el idioma del orbe.
/// </summary>
/// <remarks>
/// Es un enum propio de Core y no el de la interfaz por una razón de dependencias: los estados del
/// orbe viven en el proyecto de WPF, y Core no lo referencia ni lo va a referenciar. El anfitrión
/// traduce estos cuatro a los suyos con un <c>switch</c> de cuatro líneas; lo que se puede probar
/// sin abrir una ventana está de este lado.
/// <para>
/// Son cuatro y no quince porque son los cuatro que la sesión en vivo <em>sabe</em>. Lo demás que
/// dibuja el orbe —guardia, sin clave, sorda, un proyecto esperando— no es un momento de esta
/// conversación sino una condición del asistente, y lo sigue decidiendo quien lo decidía.
/// </para>
/// </remarks>
public enum LiveOrbMoment
{
    /// <summary>El micrófono está abierto y no hay respuesta en curso.</summary>
    Listening,

    /// <summary>Dejaste de hablar y todavía no volvió nada.</summary>
    Thinking,

    /// <summary>Está saliendo voz por los parlantes.</summary>
    Speaking,

    /// <summary>La cortaste hablándole encima.</summary>
    Interrupted
}

/// <summary>
/// Traduce el turno en vivo a un momento del orbe.
/// </summary>
/// <remarks>
/// <b>Tres de los cuatro salen del turno y el cuarto no.</b> El protocolo avisa cuando empieza a
/// llegar audio, cuando la interrumpieron y cuando el turno cerró; <em>no</em> manda nada entre que
/// el servidor da por terminado lo que dijiste y el primer bloque de voz de la respuesta. Ese hueco
/// es justo «pensando», y del lado del servidor no existe ningún mensaje que lo anuncie.
/// <para>
/// Por eso <c>Thinking</c> entra por el segundo argumento de <see cref="For"/>, que lo pone el anfitrión
/// con su propio detector de voz —el mismo que ya está medido contra este micrófono—: cuando la
/// persona dejó de hablar y el turno sigue en reposo, está pensando. Un anfitrión que no tenga
/// detector pasa <c>false</c> siempre y se queda en «te escucho» hasta que llega la voz: se pierde
/// un momento, no se dibuja uno falso.
/// </para>
/// </remarks>
public static class LiveOrbMoments
{
    /// <summary>Qué momento corresponde a este turno.</summary>
    /// <param name="turn">En qué anda el turno.</param>
    /// <param name="waitingForReply">
    /// Si la persona terminó de hablar y todavía no volvió nada. Lo sabe el anfitrión, no el
    /// servidor; ver el comentario de la clase.
    /// </param>
    /// <remarks>
    /// <see cref="LiveTurnState.Draining"/> también es «hablando», y no es un detalle: es el tramo
    /// en el que el servidor terminó de generar y los parlantes siguen sonando. Dibujar «terminó»
    /// ahí apaga el orbe mientras ella sigue hablando.
    /// </remarks>
    public static LiveOrbMoment For(LiveTurnState turn, bool waitingForReply) => turn switch
    {
        LiveTurnState.Interrupted => LiveOrbMoment.Interrupted,
        LiveTurnState.Responding or LiveTurnState.Draining => LiveOrbMoment.Speaking,
        _ => waitingForReply ? LiveOrbMoment.Thinking : LiveOrbMoment.Listening
    };
}

/// <summary>Cambió el momento de la charla en vivo.</summary>
public sealed class LiveMomentChangedEventArgs(LiveOrbMoment previous, LiveOrbMoment current) : EventArgs
{
    /// <summary>Cómo estaba.</summary>
    public LiveOrbMoment Previous { get; } = previous;

    /// <summary>Cómo quedó.</summary>
    public LiveOrbMoment Current { get; } = current;
}
