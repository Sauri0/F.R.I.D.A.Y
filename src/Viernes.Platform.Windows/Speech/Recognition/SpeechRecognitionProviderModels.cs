namespace Viernes.Platform.Windows.Speech.Recognition;

public sealed record SpeechRecognitionProviderInfo(
    SpeechRecognitionProviderKind Kind,
    string DisplayName,
    bool RunsLocally,
    string PrivacyDescription);

public sealed record SpeechRecognitionProviderAvailability(
    bool IsAvailable,
    string? UnavailableReason = null);

public sealed record SpeechRecognitionProviderSelection(
    ISpeechRecognitionProvider Provider,
    SpeechRecognitionProviderAvailability Availability,
    bool UsedFallback,
    string? FallbackReason = null);

public sealed class SpeechRecognitionProviderStateChangedEventArgs(
    SpeechRecognitionProviderState previousState,
    SpeechRecognitionProviderState currentState) : EventArgs
{
    public SpeechRecognitionProviderState PreviousState { get; } = previousState;

    public SpeechRecognitionProviderState CurrentState { get; } = currentState;
}
