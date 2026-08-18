using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// La frase completa: lo que se dijo antes del nombre, el nombre y lo que se siguió diciendo.
/// </summary>
/// <remarks>
/// El WAV es del que recibe el evento; hay que cerrarlo. Se entrega audio y no texto porque quien
/// transcribe es el proveedor de reconocimiento —que ya sabe hacerlo sin tomar el micrófono— y así
/// el oído no depende de que haya un modelo de transcripción cargado para seguir escuchando.
/// </remarks>
public sealed class WakeUtteranceEventArgs(
    Stream wave,
    string phrase,
    float confidence,
    TimeSpan preRollDuration,
    TimeSpan tailDuration,
    UtteranceStopReason stopReason,
    DateTimeOffset detectedAt) : EventArgs
{
    /// <summary>El audio completo, listo para transcribir. Cerralo cuando termines.</summary>
    public Stream Wave { get; } = wave;

    /// <summary>La frase de activación que se oyó.</summary>
    public string Phrase { get; } = phrase;

    /// <summary>Con cuánta confianza la oyó el reconocedor de nombre.</summary>
    public float Confidence { get; } = confidence;

    /// <summary>Cuánto audio anterior al nombre se rescató de la ventana rodante.</summary>
    public TimeSpan PreRollDuration { get; } = preRollDuration;

    /// <summary>Cuánto se siguió grabando después.</summary>
    public TimeSpan TailDuration { get; } = tailDuration;

    /// <summary>Por qué se dejó de grabar.</summary>
    public UtteranceStopReason StopReason { get; } = stopReason;

    /// <summary>Cuándo sonó el nombre.</summary>
    public DateTimeOffset DetectedAt { get; } = detectedAt;

    /// <summary>Duración total del audio entregado.</summary>
    public TimeSpan Duration => PreRollDuration + TailDuration;
}
