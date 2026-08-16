namespace Viernes.Platform.Windows.Speech;

public sealed record SpeechOperationResult(
    bool Succeeded,
    SpeechErrorCode ErrorCode = SpeechErrorCode.None,
    string? ErrorMessage = null)
{
    public static SpeechOperationResult Success() => new(true);

    public static SpeechOperationResult Failure(SpeechErrorCode errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}
