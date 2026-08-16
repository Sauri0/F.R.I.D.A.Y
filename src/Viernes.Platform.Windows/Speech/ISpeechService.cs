namespace Viernes.Platform.Windows.Speech;

public interface ISpeechService : IAsyncDisposable
{
    SpeechServiceState State { get; }

    bool IsMicrophoneActive { get; }

    bool IsMicrophoneMuted { get; }

    event EventHandler<SpeechStateChangedEventArgs>? StateChanged;

    event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    event EventHandler<MicrophoneMuteChangedEventArgs>? MicrophoneMuteChanged;

    event EventHandler<SpeechTranscriptionEventArgs>? TranscriptionUpdated;

    event EventHandler<SpeechServiceErrorEventArgs>? Error;

    SpeechCapabilities GetCapabilities();

    Task<SpeechOperationResult> StartPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechRecognitionResult> StopPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> CancelPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> SetMicrophoneMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> SpeakAsync(string text, CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> StopSpeakingAsync(CancellationToken cancellationToken = default);
}
