namespace Viernes.Platform.Windows.Speech.WakeWord;

public interface IWakeWordService : IAsyncDisposable
{
    WakeWordServiceState State { get; }

    bool IsMicrophoneActive { get; }

    bool IsMuted { get; }

    bool IsDemoOnly { get; }

    string ReliabilityNotice { get; }

    IReadOnlyList<string> Phrases { get; }

    event EventHandler<WakeWordStateChangedEventArgs>? StateChanged;

    event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    Task<SpeechOperationResult> StartAsync(CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> StopAsync(CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> SetMutedAsync(bool isMuted, CancellationToken cancellationToken = default);
}
