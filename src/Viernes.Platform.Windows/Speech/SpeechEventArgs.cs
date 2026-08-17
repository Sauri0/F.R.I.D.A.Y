namespace Viernes.Platform.Windows.Speech;

public sealed class SpeechStateChangedEventArgs(
    SpeechServiceState previousState,
    SpeechServiceState currentState) : EventArgs
{
    public SpeechServiceState PreviousState { get; } = previousState;

    public SpeechServiceState CurrentState { get; } = currentState;
}

/// <summary>
/// Nivel instantáneo del micrófono, de 0 a 1. Se emite muchas veces por segundo para que la
/// interfaz pueda reaccionar a la voz del usuario mientras habla.
/// </summary>
/// <remarks>
/// Es la señal que hace inequívoco el «te estoy escuchando»: un color fijo no distingue atender de
/// estar colgado, pero una forma que se mueve con tu voz sí. No lleva audio, sólo una magnitud.
/// </remarks>
public sealed class AudioLevelEventArgs(double level, bool isVoice) : EventArgs
{
    public double Level { get; } = Math.Clamp(level, 0, 1);

    /// <summary>Si el detector considera que esto es voz sostenida y no ruido suelto.</summary>
    public bool IsVoice { get; } = isVoice;
}

public sealed class MicrophoneActivityChangedEventArgs(bool isActive) : EventArgs
{
    public bool IsActive { get; } = isActive;
}

public sealed class MicrophoneMuteChangedEventArgs(bool isMuted) : EventArgs
{
    public bool IsMuted { get; } = isMuted;
}

public sealed class SpeechTranscriptionEventArgs(
    string text,
    float confidence,
    bool isFinal) : EventArgs
{
    public string Text { get; } = text;

    public float Confidence { get; } = confidence;

    public bool IsFinal { get; } = isFinal;
}

public sealed class SpeechServiceErrorEventArgs(
    SpeechErrorCode errorCode,
    string message) : EventArgs
{
    public SpeechErrorCode ErrorCode { get; } = errorCode;

    public string Message { get; } = message;
}
