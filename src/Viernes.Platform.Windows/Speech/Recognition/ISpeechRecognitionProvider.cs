namespace Viernes.Platform.Windows.Speech.Recognition;

public interface ISpeechRecognitionProvider : IAsyncDisposable
{
    SpeechRecognitionProviderInfo Info { get; }

    SpeechRecognitionProviderState State { get; }

    bool IsMicrophoneActive { get; }

    bool IsMicrophoneMuted { get; }

    event EventHandler<SpeechRecognitionProviderStateChangedEventArgs>? StateChanged;

    event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    /// <summary>Nivel del micrófono mientras captura, para que la interfaz reaccione a la voz.</summary>
    event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    event EventHandler<SpeechTranscriptionEventArgs>? TranscriptionUpdated;

    event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    SpeechRecognitionProviderAvailability GetAvailability();

    Task<SpeechOperationResult> StartPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechRecognitionResult> StopPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechRecognitionResult> RecognizeSingleUtteranceAsync(
        SingleUtteranceRecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> CancelPushToTalkAsync(CancellationToken cancellationToken = default);

    Task<SpeechOperationResult> SetMicrophoneMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default);
}
