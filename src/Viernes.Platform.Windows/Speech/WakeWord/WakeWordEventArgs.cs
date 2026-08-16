namespace Viernes.Platform.Windows.Speech.WakeWord;

public sealed class WakeWordStateChangedEventArgs(
    WakeWordServiceState previousState,
    WakeWordServiceState currentState) : EventArgs
{
    public WakeWordServiceState PreviousState { get; } = previousState;

    public WakeWordServiceState CurrentState { get; } = currentState;
}

public sealed class WakeWordDetectedEventArgs(
    string phrase,
    float confidence,
    DateTimeOffset detectedAt) : EventArgs
{
    public string Phrase { get; } = phrase;

    public float Confidence { get; } = confidence;

    public DateTimeOffset DetectedAt { get; } = detectedAt;
}
