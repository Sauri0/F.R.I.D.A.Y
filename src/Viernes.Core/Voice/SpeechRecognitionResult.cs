namespace Viernes.Core.Voice;

public sealed record SpeechRecognitionResult(
    string Text,
    float Confidence,
    bool WasCancelled = false)
{
    public static SpeechRecognitionResult Cancelled() => new(string.Empty, 0, WasCancelled: true);
}
