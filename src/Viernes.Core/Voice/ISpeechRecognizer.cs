namespace Viernes.Core.Voice;

/// <summary>
/// Platform abstraction for one explicit capture. Implementations must surface microphone state in
/// the host UI and must not begin capture while muted.
/// </summary>
public interface ISpeechRecognizer
{
    bool IsAvailable { get; }

    bool IsCapturing { get; }

    Task<SpeechRecognitionResult> RecognizeOnceAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
