namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>Fallback local basado en el reconocedor instalado de Windows.</summary>
public sealed class SapiSpeechRecognitionProvider : ISpeechRecognitionProvider
{
    private readonly SpeechService _speechService;
    private readonly object _sync = new();
    private SpeechRecognitionProviderState _state;
    private bool _disposed;

    public SapiSpeechRecognitionProvider(SpeechServiceOptions? options = null)
    {
        _speechService = new SpeechService(options);
        _speechService.StateChanged += OnSpeechStateChanged;
        _speechService.MicrophoneActivityChanged += OnMicrophoneActivityChanged;
        _speechService.TranscriptionUpdated += OnTranscriptionUpdated;
        _speechService.Error += OnServiceError;
    }

    public SpeechRecognitionProviderInfo Info { get; } = new(
        SpeechRecognitionProviderKind.WindowsSapi,
        "Reconocimiento de voz de Windows",
        RunsLocally: true,
        "Usa únicamente el motor SAPI instalado en Windows; no envía audio a servicios remotos.");

    public SpeechRecognitionProviderState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsMicrophoneActive => _speechService.IsMicrophoneActive;

    public bool IsMicrophoneMuted => _speechService.IsMicrophoneMuted;

    public event EventHandler<SpeechRecognitionProviderStateChangedEventArgs>? StateChanged;

    public event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    public event EventHandler<SpeechTranscriptionEventArgs>? TranscriptionUpdated;

    public event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    public SpeechRecognitionProviderAvailability GetAvailability()
    {
        var capabilities = _speechService.GetCapabilities();
        return capabilities.IsRecognitionAvailable
            ? new SpeechRecognitionProviderAvailability(true)
            : new SpeechRecognitionProviderAvailability(
                false,
                capabilities.ErrorMessage ?? "Windows no tiene un reconocedor de voz instalado.");
    }

    public Task<SpeechOperationResult> StartPushToTalkAsync(CancellationToken cancellationToken = default) =>
        _speechService.StartPushToTalkAsync(cancellationToken);

    public Task<SpeechRecognitionResult> StopPushToTalkAsync(CancellationToken cancellationToken = default) =>
        _speechService.StopPushToTalkAsync(cancellationToken);

    public async Task<SpeechRecognitionResult> RecognizeSingleUtteranceAsync(
        SingleUtteranceRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new SingleUtteranceRecognitionOptions();
        effectiveOptions.Validate();
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTranscript(object? sender, SpeechTranscriptionEventArgs eventArgs)
        {
            if (eventArgs.IsFinal)
            {
                completed.TrySetResult(true);
            }
        }

        void OnState(object? sender, SpeechStateChangedEventArgs eventArgs)
        {
            if (eventArgs.PreviousState == SpeechServiceState.Listening &&
                eventArgs.CurrentState == SpeechServiceState.Idle)
            {
                completed.TrySetResult(false);
            }
        }

        _speechService.TranscriptionUpdated += OnTranscript;
        _speechService.StateChanged += OnState;
        try
        {
            var started = await _speechService.StartPushToTalkAsync(cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded)
            {
                return SpeechRecognitionResult.Failure(started.ErrorCode, started.ErrorMessage!);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(effectiveOptions.MaximumDuration);
            try
            {
                _ = await completed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return await _speechService.StopPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await _speechService.CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
                return SpeechRecognitionResult.Failure(
                    SpeechErrorCode.TimedOut,
                    "SAPI no detectó una frase completa dentro del tiempo permitido.");
            }
            catch (OperationCanceledException)
            {
                await _speechService.CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
                return SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Cancelled,
                    "El reconocimiento de una frase fue cancelado.");
            }
        }
        finally
        {
            _speechService.TranscriptionUpdated -= OnTranscript;
            _speechService.StateChanged -= OnState;
        }
    }

    public Task<SpeechOperationResult> CancelPushToTalkAsync(CancellationToken cancellationToken = default) =>
        _speechService.CancelPushToTalkAsync(cancellationToken);

    public Task<SpeechOperationResult> SetMicrophoneMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default) =>
        _speechService.SetMicrophoneMutedAsync(isMuted, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _speechService.StateChanged -= OnSpeechStateChanged;
        _speechService.MicrophoneActivityChanged -= OnMicrophoneActivityChanged;
        _speechService.TranscriptionUpdated -= OnTranscriptionUpdated;
        _speechService.Error -= OnServiceError;
        await _speechService.DisposeAsync().ConfigureAwait(false);
    }

    private void OnSpeechStateChanged(object? sender, SpeechStateChangedEventArgs eventArgs)
    {
        var state = eventArgs.CurrentState == SpeechServiceState.Listening
            ? SpeechRecognitionProviderState.Capturing
            : SpeechRecognitionProviderState.Idle;
        SetState(state);
    }

    private void OnMicrophoneActivityChanged(object? sender, MicrophoneActivityChangedEventArgs eventArgs) =>
        MicrophoneActivityChanged?.Invoke(this, eventArgs);

    private void OnTranscriptionUpdated(object? sender, SpeechTranscriptionEventArgs eventArgs) =>
        TranscriptionUpdated?.Invoke(this, eventArgs);

    private void OnServiceError(object? sender, SpeechServiceErrorEventArgs eventArgs) =>
        ServiceError?.Invoke(this, eventArgs);

    private void SetState(SpeechRecognitionProviderState state)
    {
        SpeechRecognitionProviderState previous;
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            previous = _state;
            _state = state;
        }

        StateChanged?.Invoke(this, new SpeechRecognitionProviderStateChangedEventArgs(previous, state));
    }
}
