namespace Viernes.Core.Live;

/// <summary>Quién dijo el texto que llegó transcripto.</summary>
public enum LiveSpeaker
{
    /// <summary>La persona.</summary>
    User,

    /// <summary>Ella.</summary>
    Assistant
}

/// <summary>Un pedazo de transcripción.</summary>
/// <remarks>
/// Llega de a fragmentos y no de a frases: sirve para ir mostrando en pantalla mientras se habla,
/// no para guardar. Quien quiera la frase entera tiene que acumular hasta el fin del turno.
/// </remarks>
public sealed class LiveTranscriptEventArgs(LiveSpeaker speaker, string text) : EventArgs
{
    /// <summary>Quién lo dijo.</summary>
    public LiveSpeaker Speaker { get; } = speaker;

    /// <summary>El fragmento.</summary>
    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));
}

/// <summary>Cambió el estado del turno.</summary>
public sealed class LiveTurnStateChangedEventArgs(LiveTurnState previous, LiveTurnState current) : EventArgs
{
    /// <summary>Cómo estaba.</summary>
    public LiveTurnState Previous { get; } = previous;

    /// <summary>Cómo quedó.</summary>
    public LiveTurnState Current { get; } = current;
}

/// <summary>
/// Algo salió mal en la sesión.
/// </summary>
/// <remarks>
/// Nunca trae la excepción cruda ni la dirección del servidor: la credencial viaja en la URL de esta
/// API, así que cualquier cosa que la contenga es una filtración esperando un archivo de registro.
/// </remarks>
public sealed class LiveFailureEventArgs(string message, bool fatal) : EventArgs
{
    /// <summary>Qué pasó, dicho para una persona.</summary>
    public string Message { get; } = message ?? throw new ArgumentNullException(nameof(message));

    /// <summary>Si la sesión quedó cerrada y hay que volver al camino de siempre.</summary>
    public bool Fatal { get; } = fatal;
}
