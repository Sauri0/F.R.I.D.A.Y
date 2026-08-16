namespace Viernes.Platform.Windows.Speech;

public sealed class SpeechStateChangedEventArgs(
    SpeechServiceState previousState,
    SpeechServiceState currentState) : EventArgs
{
    public SpeechServiceState PreviousState { get; } = previousState;

    public SpeechServiceState CurrentState { get; } = currentState;
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
