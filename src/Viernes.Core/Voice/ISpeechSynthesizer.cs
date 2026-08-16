namespace Viernes.Core.Voice;

public interface ISpeechSynthesizer
{
    bool IsAvailable { get; }

    bool IsSpeaking { get; }

    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
