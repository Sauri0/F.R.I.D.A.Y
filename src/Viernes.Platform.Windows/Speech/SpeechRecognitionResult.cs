namespace Viernes.Platform.Windows.Speech;

public sealed record SpeechRecognitionResult(
    bool Succeeded,
    string Text,
    float? Confidence = null,
    SpeechErrorCode ErrorCode = SpeechErrorCode.None,
    string? ErrorMessage = null)
{
    public static SpeechRecognitionResult Success(string text = "", float? confidence = null) =>
        new(true, text, confidence);

    public static SpeechRecognitionResult Failure(SpeechErrorCode errorCode, string errorMessage) =>
        new(false, string.Empty, null, errorCode, errorMessage);
}
