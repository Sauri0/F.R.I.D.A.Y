using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// DEMO experimental de palabra de activación. SAPI y una gramática exacta no constituyen
/// un detector robusto: pueden existir falsos positivos, falsos negativos y variación por equipo.
/// </summary>
public sealed class SapiWakeWordService : IWakeWordService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WakeWordServiceOptions _options;
    private readonly string[] _phrases;
    private readonly object _sync = new();
    private SpeechRecognitionEngine? _engine;
    private WakeWordServiceState _state;
    private bool _isMicrophoneActive;
    private bool _isMuted;
    private bool _desiredRunning;
    private bool _disposed;

    public SapiWakeWordService(WakeWordServiceOptions? options = null)
    {
        _options = options ?? new WakeWordServiceOptions();
        _phrases = _options.ValidateAndNormalizePhrases();
    }

    public WakeWordServiceState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsMicrophoneActive
    {
        get
        {
            lock (_sync)
            {
                return _isMicrophoneActive;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _isMuted;
            }
        }
    }

    public bool IsDemoOnly => true;

    public string ReliabilityNotice =>
        "DEMO no confiable: la gramática exacta de SAPI puede fallar o activarse por error; " +
        "mientras escucha mantiene el micrófono abierto y visible en el indicador.";

    public IReadOnlyList<string> Phrases => _phrases;

    public event EventHandler<WakeWordStateChangedEventArgs>? StateChanged;

    public event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    public event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    public async Task<SpeechOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return DisposedOperation();
                }

                _desiredRunning = true;
                if (_isMuted)
                {
                    SetStateUnsafe(WakeWordServiceState.Muted, out var mutedChange);
                    RaiseStateChange(mutedChange);
                    return SpeechOperationResult.Failure(
                        SpeechErrorCode.MicrophoneMuted,
                        "La palabra de activación está silenciada.");
                }

                if (_engine is not null)
                {
                    return SpeechOperationResult.Success();
                }
            }

            return StartEngineCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return DisposedOperation();
                }

                _desiredRunning = false;
            }

            StopEngineCore(WakeWordServiceState.Stopped);
            return SpeechOperationResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechOperationResult> SetMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            bool shouldResume;
            lock (_sync)
            {
                if (_disposed)
                {
                    return DisposedOperation();
                }

                _isMuted = isMuted;
                shouldResume = !isMuted && _desiredRunning;
            }

            if (isMuted)
            {
                // Cancelar y desechar el engine libera el dispositivo antes de completar mute.
                StopEngineCore(WakeWordServiceState.Muted);
                return SpeechOperationResult.Success();
            }

            if (shouldResume)
            {
                return StartEngineCore();
            }

            SetState(WakeWordServiceState.Stopped);
            return SpeechOperationResult.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _desiredRunning = false;
            }

            StopEngineCore(WakeWordServiceState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SpeechOperationResult StartEngineCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetState(WakeWordServiceState.Unavailable);
            return SpeechOperationResult.Failure(
                SpeechErrorCode.Unavailable,
                "La demo SAPI solo está disponible en Windows.");
        }

        SpeechRecognitionEngine? engine = null;
        try
        {
            var requestedCulture = CultureInfo.GetCultureInfo(_options.RecognitionCulture);
            var recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .OrderByDescending(candidate => candidate.Culture.Equals(requestedCulture))
                .FirstOrDefault(candidate =>
                    candidate.Culture.Equals(requestedCulture) ||
                    string.Equals(
                        candidate.Culture.TwoLetterISOLanguageName,
                        requestedCulture.TwoLetterISOLanguageName,
                        StringComparison.OrdinalIgnoreCase));
            if (recognizer is null)
            {
                SetState(WakeWordServiceState.Unavailable);
                return SpeechOperationResult.Failure(
                    SpeechErrorCode.Unavailable,
                    $"Windows no tiene un reconocedor para {_options.RecognitionCulture}.");
            }

            engine = new SpeechRecognitionEngine(recognizer.Id)
            {
                InitialSilenceTimeout = TimeSpan.FromHours(1),
                BabbleTimeout = TimeSpan.FromHours(1)
            };
            var grammarBuilder = new GrammarBuilder
            {
                Culture = recognizer.Culture
            };
            grammarBuilder.Append(new Choices(_phrases));
            engine.LoadGrammar(new Grammar(grammarBuilder) { Name = "Viernes.SapiWakeWordDemo" });
            engine.SpeechRecognized += OnSpeechRecognized;
            engine.RecognizeCompleted += OnRecognizeCompleted;

            lock (_sync)
            {
                _engine = engine;
            }

            SetState(WakeWordServiceState.Listening);
            SetMicrophoneActivity(isActive: true);
            engine.SetInputToDefaultAudioDevice();
            engine.RecognizeAsync(RecognizeMode.Multiple);
            return SpeechOperationResult.Success();
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
            if (engine is not null)
            {
                DetachAndDispose(engine);
            }

            lock (_sync)
            {
                if (ReferenceEquals(_engine, engine))
                {
                    _engine = null;
                }
            }

            SetMicrophoneActivity(isActive: false);
            SetState(WakeWordServiceState.Faulted);
            var message = $"La demo de palabra de activación no pudo iniciar: {exception.Message}";
            RaiseError(SpeechErrorCode.DeviceError, message);
            return SpeechOperationResult.Failure(SpeechErrorCode.DeviceError, message);
        }
    }

    private void StopEngineCore(WakeWordServiceState finalState)
    {
        SpeechRecognitionEngine? engine;
        lock (_sync)
        {
            engine = _engine;
            _engine = null;
        }

        if (engine is not null)
        {
            try
            {
                engine.RecognizeAsyncCancel();
            }
            catch (Exception exception) when (IsExpectedSpeechFailure(exception))
            {
            }

            DetachAndDispose(engine);
        }

        SetMicrophoneActivity(isActive: false);
        SetState(finalState);
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (eventArgs.Result is null || eventArgs.Result.Confidence < _options.MinimumConfidence)
        {
            return;
        }

        var phrase = _phrases.FirstOrDefault(candidate => string.Equals(
            candidate,
            eventArgs.Result.Text.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (phrase is null)
        {
            return;
        }

        RaiseSafely(
            WakeWordDetected,
            new WakeWordDetectedEventArgs(phrase, eventArgs.Result.Confidence, DateTimeOffset.UtcNow));
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs eventArgs)
    {
        if (sender is not SpeechRecognitionEngine engine)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => _ = HandleUnexpectedCompletionAsync(engine, eventArgs.Error));
    }

    private async Task HandleUnexpectedCompletionAsync(SpeechRecognitionEngine engine, Exception? error)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool shouldRestart;
            lock (_sync)
            {
                if (!ReferenceEquals(_engine, engine))
                {
                    return;
                }

                _engine = null;
                shouldRestart = !_disposed && _desiredRunning && !_isMuted && error is null;
            }

            DetachAndDispose(engine);
            SetMicrophoneActivity(isActive: false);
            if (error is not null)
            {
                SetState(WakeWordServiceState.Faulted);
                RaiseError(SpeechErrorCode.DeviceError, $"La demo SAPI se detuvo: {error.Message}");
            }
            else if (shouldRestart)
            {
                _ = StartEngineCore();
            }
            else
            {
                SetState(_isMuted ? WakeWordServiceState.Muted : WakeWordServiceState.Stopped);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void DetachAndDispose(SpeechRecognitionEngine engine)
    {
        // Los handlers pertenecen a esta instancia y se eliminan al desechar el engine.
        engine.Dispose();
    }

    private void SetState(WakeWordServiceState state)
    {
        StateChange? change;
        lock (_sync)
        {
            SetStateUnsafe(state, out change);
        }

        RaiseStateChange(change);
    }

    private void SetStateUnsafe(WakeWordServiceState state, out StateChange? change)
    {
        if (_state == state)
        {
            change = null;
            return;
        }

        change = new StateChange(_state, state);
        _state = state;
    }

    private void RaiseStateChange(StateChange? change)
    {
        if (change is not null)
        {
            RaiseSafely(
                StateChanged,
                new WakeWordStateChangedEventArgs(change.Previous, change.Current));
        }
    }

    private void SetMicrophoneActivity(bool isActive)
    {
        lock (_sync)
        {
            if (_isMicrophoneActive == isActive)
            {
                return;
            }

            _isMicrophoneActive = isActive;
        }

        RaiseSafely(MicrophoneActivityChanged, new MicrophoneActivityChangedEventArgs(isActive));
    }

    private void RaiseError(SpeechErrorCode code, string message) =>
        RaiseSafely(ServiceError, new SpeechServiceErrorEventArgs(code, message));

    private async Task<bool> TryEnterGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsExpectedSpeechFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or COMException;

    private static SpeechOperationResult CancelledOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");

    private static SpeechOperationResult DisposedOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Disposed, "El detector ya fue cerrado.");

    private void RaiseSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
            }
        }
    }

    private sealed record StateChange(WakeWordServiceState Previous, WakeWordServiceState Current);
}
