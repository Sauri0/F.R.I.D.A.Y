using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace Viernes.Platform.Windows.Speech;

/// <summary>
/// Voz local basada en Windows SAPI. El reconocimiento toma el micrófono únicamente
/// entre StartPushToTalkAsync y Stop/CancelPushToTalkAsync.
/// </summary>
public sealed class SpeechService : ISpeechService
{
    private const int MaximumUtteranceLength = 10_000;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SpeechServiceOptions _options;
    private readonly object _sync = new();
    private RecognitionSession? _recognitionSession;
    private SpeechSession? _speechSession;
    private SpeechRecognitionResult? _lastRecognitionResult;
    private SpeechServiceState _state = SpeechServiceState.Idle;
    private bool _isMicrophoneActive;
    private bool _isMicrophoneMuted;
    private bool _isDisposed;

    public SpeechService(SpeechServiceOptions? options = null)
    {
        _options = options ?? new SpeechServiceOptions();
        _options.Validate();
    }

    public SpeechServiceState State
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

    public bool IsMicrophoneMuted
    {
        get
        {
            lock (_sync)
            {
                return _isMicrophoneMuted;
            }
        }
    }

    public event EventHandler<SpeechStateChangedEventArgs>? StateChanged;

    public event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    public event EventHandler<MicrophoneMuteChangedEventArgs>? MicrophoneMuteChanged;

    public event EventHandler<SpeechTranscriptionEventArgs>? TranscriptionUpdated;

    public event EventHandler<SpeechServiceErrorEventArgs>? Error;

    public SpeechCapabilities GetCapabilities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new SpeechCapabilities(false, false, [], [], "La voz local solo está disponible en Windows.");
        }

        var recognitionCultures = new List<string>();
        var voices = new List<InstalledSpeechVoice>();
        var errors = new List<string>();

        try
        {
            recognitionCultures.AddRange(
                SpeechRecognitionEngine.InstalledRecognizers()
                    .Select(recognizer => recognizer.Culture.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
            errors.Add($"Reconocimiento: {exception.Message}");
        }

        try
        {
            using var synthesizer = new SpeechSynthesizer();
            voices.AddRange(
                synthesizer.GetInstalledVoices()
                    .Where(voice => voice.Enabled)
                    .Select(voice => new InstalledSpeechVoice(
                        voice.VoiceInfo.Name,
                        voice.VoiceInfo.Culture.Name))
                    .OrderBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
            errors.Add($"Síntesis: {exception.Message}");
        }

        return new SpeechCapabilities(
            recognitionCultures.Count > 0,
            voices.Count > 0,
            recognitionCultures,
            voices,
            errors.Count == 0 ? null : string.Join(" ", errors));
    }

    public async Task<SpeechOperationResult> StartPushToTalkAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            if (IsDisposed())
            {
                return DisposedOperation();
            }

            if (IsMicrophoneMuted)
            {
                return SpeechOperationResult.Failure(
                    SpeechErrorCode.MicrophoneMuted,
                    "El micrófono está silenciado.");
            }

            lock (_sync)
            {
                if (_recognitionSession is not null)
                {
                    return SpeechOperationResult.Success();
                }
            }

            CancelSpeechCore();

            var creation = TryCreateRecognitionSession();
            if (creation.Session is null)
            {
                RaiseError(creation.ErrorCode, creation.ErrorMessage!);
                return SpeechOperationResult.Failure(creation.ErrorCode, creation.ErrorMessage!);
            }

            var session = creation.Session;
            lock (_sync)
            {
                _recognitionSession = session;
                _lastRecognitionResult = null;
            }

            SetState(SpeechServiceState.Listening);
            SetMicrophoneActivity(isActive: true);

            try
            {
                // El indicador se enciende antes de abrir el dispositivo de entrada.
                session.Engine.SetInputToDefaultAudioDevice();
                session.Engine.RecognizeAsync(RecognizeMode.Multiple);
                return SpeechOperationResult.Success();
            }
            catch (Exception exception) when (IsExpectedSpeechFailure(exception))
            {
                var result = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.DeviceError,
                    $"No se pudo abrir el micrófono: {exception.Message}");
                CompleteRecognition(session, result);
                return SpeechOperationResult.Failure(result.ErrorCode, result.ErrorMessage!);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SpeechRecognitionResult> StopPushToTalkAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return SpeechRecognitionResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");
        }

        try
        {
            if (IsDisposed())
            {
                return SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Disposed,
                    "El servicio de voz ya fue cerrado.");
            }

            RecognitionSession? session;
            SpeechRecognitionResult? lastResult;
            lock (_sync)
            {
                session = _recognitionSession;
                lastResult = _lastRecognitionResult;
            }

            if (session is null)
            {
                return lastResult ?? SpeechRecognitionResult.Success();
            }

            try
            {
                session.Engine.RecognizeAsyncStop();
            }
            catch (Exception exception) when (IsExpectedSpeechFailure(exception))
            {
                var failure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.DeviceError,
                    $"No se pudo detener el reconocimiento: {exception.Message}");
                CompleteRecognition(session, failure);
                return failure;
            }

            try
            {
                return await session.Completion.Task
                    .WaitAsync(_options.StopTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TryCancelRecognition(session);
                var failure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.TimedOut,
                    "El reconocimiento no liberó el micrófono a tiempo y fue cancelado.");
                CompleteRecognition(session, failure);
                return failure;
            }
            catch (OperationCanceledException)
            {
                TryCancelRecognition(session);
                var cancelled = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Cancelled,
                    "La operación fue cancelada.");
                CompleteRecognition(session, cancelled);
                return cancelled;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SpeechOperationResult> CancelPushToTalkAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            if (IsDisposed())
            {
                return DisposedOperation();
            }

            RecognitionSession? session;
            lock (_sync)
            {
                session = _recognitionSession;
            }

            if (session is not null)
            {
                TryCancelRecognition(session);
                CompleteRecognition(
                    session,
                    SpeechRecognitionResult.Failure(
                        SpeechErrorCode.Cancelled,
                        "El reconocimiento fue cancelado."));
            }

            return SpeechOperationResult.Success();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SpeechOperationResult> SetMicrophoneMutedAsync(
        bool isMuted,
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            if (IsDisposed())
            {
                return DisposedOperation();
            }

            bool changed;
            lock (_sync)
            {
                changed = _isMicrophoneMuted != isMuted;
                _isMicrophoneMuted = isMuted;
            }

            if (isMuted)
            {
                RecognitionSession? session;
                lock (_sync)
                {
                    session = _recognitionSession;
                }

                if (session is not null)
                {
                    TryCancelRecognition(session);
                    CompleteRecognition(
                        session,
                        SpeechRecognitionResult.Failure(
                            SpeechErrorCode.MicrophoneMuted,
                            "El micrófono fue silenciado."));
                }
            }

            if (changed)
            {
                RaiseSafely(this, MicrophoneMuteChanged, new MicrophoneMuteChangedEventArgs(isMuted));
            }

            return SpeechOperationResult.Success();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SpeechOperationResult> SpeakAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumUtteranceLength)
        {
            return SpeechOperationResult.Failure(
                SpeechErrorCode.InvalidInput,
                $"El texto debe contener entre 1 y {MaximumUtteranceLength:N0} caracteres.");
        }

        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        SpeechSession? session = null;
        try
        {
            if (IsDisposed())
            {
                return DisposedOperation();
            }

            CancelRecognitionCore();
            CancelSpeechCore();

            try
            {
                session = CreateSpeechSession();
                lock (_sync)
                {
                    _speechSession = session;
                }

                SetState(SpeechServiceState.Speaking);
                session.Synthesizer.SpeakAsync(text.Trim());
            }
            catch (Exception exception) when (IsExpectedSpeechFailure(exception))
            {
                var failure = SpeechOperationResult.Failure(
                    SpeechErrorCode.Unavailable,
                    $"La síntesis de voz no está disponible: {exception.Message}");

                if (session is not null)
                {
                    CompleteSpeech(session, failure);
                }
                else
                {
                    RaiseError(failure.ErrorCode, failure.ErrorMessage!);
                }

                return failure;
            }
        }
        finally
        {
            _operationGate.Release();
        }

        try
        {
            return await session.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopSpeakingAsync(CancellationToken.None).ConfigureAwait(false);
            return CancelledOperation();
        }
    }

    public async Task<SpeechOperationResult> StopSpeakingAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            if (IsDisposed())
            {
                return DisposedOperation();
            }

            CancelSpeechCore();
            return SpeechOperationResult.Success();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
            }

            CancelRecognitionCore();
            CancelSpeechCore();
            SetMicrophoneActivity(isActive: false);
            SetState(SpeechServiceState.Idle);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private (RecognitionSession? Session, SpeechErrorCode ErrorCode, string? ErrorMessage)
        TryCreateRecognitionSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, SpeechErrorCode.Unavailable, "El reconocimiento local solo está disponible en Windows.");
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
                return (
                    null,
                    SpeechErrorCode.Unavailable,
                    $"Windows no tiene instalado un reconocedor para {_options.RecognitionCulture}.");
            }

            engine = new SpeechRecognitionEngine(recognizer.Id)
            {
                InitialSilenceTimeout = _options.InitialSilenceTimeout,
                EndSilenceTimeout = _options.EndSilenceTimeout,
                EndSilenceTimeoutAmbiguous = _options.EndSilenceTimeout
            };
            engine.LoadGrammar(new DictationGrammar());

            var session = new RecognitionSession(engine);
            session.RecognizedHandler = (_, eventArgs) => OnSpeechRecognized(session, eventArgs);
            session.HypothesizedHandler = (_, eventArgs) => OnSpeechHypothesized(session, eventArgs);
            session.CompletedHandler = (_, eventArgs) => OnRecognitionCompleted(session, eventArgs);
            engine.SpeechRecognized += session.RecognizedHandler;
            engine.SpeechHypothesized += session.HypothesizedHandler;
            engine.RecognizeCompleted += session.CompletedHandler;

            return (session, SpeechErrorCode.None, null);
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
            engine?.Dispose();
            return (
                null,
                SpeechErrorCode.Unavailable,
                $"No se pudo iniciar el reconocimiento local: {exception.Message}");
        }
    }

    private SpeechSession CreateSpeechSession()
    {
        var synthesizer = new SpeechSynthesizer();
        try
        {
            SelectBestVoice(synthesizer);
            var session = new SpeechSession(synthesizer);
            session.CompletedHandler = (_, eventArgs) => OnSpeakCompleted(session, eventArgs);
            synthesizer.SpeakCompleted += session.CompletedHandler;
            return session;
        }
        catch
        {
            synthesizer.Dispose();
            throw;
        }
    }

    private void SelectBestVoice(SpeechSynthesizer synthesizer)
    {
        var voices = synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).ToArray();
        if (voices.Length == 0)
        {
            throw new InvalidOperationException("Windows no tiene voces instaladas.");
        }

        var preferredName = _options.PreferredVoiceName?.Trim();
        var selected = preferredName is null
            ? null
            : voices.FirstOrDefault(voice => string.Equals(
                voice.VoiceInfo.Name,
                preferredName,
                StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            var requestedCulture = CultureInfo.GetCultureInfo(_options.SynthesisCulture);
            selected = voices
                .OrderByDescending(voice => voice.VoiceInfo.Culture.Equals(requestedCulture))
                .FirstOrDefault(voice =>
                    voice.VoiceInfo.Culture.Equals(requestedCulture) ||
                    string.Equals(
                        voice.VoiceInfo.Culture.TwoLetterISOLanguageName,
                        requestedCulture.TwoLetterISOLanguageName,
                        StringComparison.OrdinalIgnoreCase));
        }

        // Si no hay voz española, SAPI conserva su voz predeterminada en vez de impedir la salida.
        if (selected is not null)
        {
            synthesizer.SelectVoice(selected.VoiceInfo.Name);
        }
    }

    private void OnSpeechRecognized(RecognitionSession session, SpeechRecognizedEventArgs eventArgs)
    {
        if (!session.IsActive || eventArgs.Result is null ||
            string.IsNullOrWhiteSpace(eventArgs.Result.Text) ||
            eventArgs.Result.Confidence < _options.MinimumConfidence)
        {
            return;
        }

        session.AddSegment(eventArgs.Result.Text.Trim(), eventArgs.Result.Confidence);
        RaiseSafely(
            this,
            TranscriptionUpdated,
            new SpeechTranscriptionEventArgs(
                eventArgs.Result.Text.Trim(),
                eventArgs.Result.Confidence,
                isFinal: true));
    }

    private void OnSpeechHypothesized(RecognitionSession session, SpeechHypothesizedEventArgs eventArgs)
    {
        if (!_options.EmitPartialTranscriptions || !session.IsActive || eventArgs.Result is null ||
            string.IsNullOrWhiteSpace(eventArgs.Result.Text))
        {
            return;
        }

        RaiseSafely(
            this,
            TranscriptionUpdated,
            new SpeechTranscriptionEventArgs(
                eventArgs.Result.Text.Trim(),
                eventArgs.Result.Confidence,
                isFinal: false));
    }

    private void OnRecognitionCompleted(RecognitionSession session, RecognizeCompletedEventArgs eventArgs)
    {
        SpeechRecognitionResult result;
        if (eventArgs.Error is not null)
        {
            result = SpeechRecognitionResult.Failure(
                SpeechErrorCode.DeviceError,
                $"El reconocimiento local falló: {eventArgs.Error.Message}");
        }
        else if (eventArgs.Cancelled)
        {
            result = SpeechRecognitionResult.Failure(
                SpeechErrorCode.Cancelled,
                "El reconocimiento fue cancelado.");
        }
        else
        {
            result = session.BuildResult();
        }

        CompleteRecognition(session, result);
    }

    private void OnSpeakCompleted(SpeechSession session, SpeakCompletedEventArgs eventArgs)
    {
        SpeechOperationResult result;
        if (eventArgs.Error is not null)
        {
            result = SpeechOperationResult.Failure(
                SpeechErrorCode.DeviceError,
                $"La salida de voz falló: {eventArgs.Error.Message}");
        }
        else if (eventArgs.Cancelled)
        {
            result = SpeechOperationResult.Failure(
                SpeechErrorCode.Cancelled,
                "La salida de voz fue cancelada.");
        }
        else
        {
            result = SpeechOperationResult.Success();
        }

        CompleteSpeech(session, result);
    }

    private void CompleteRecognition(RecognitionSession session, SpeechRecognitionResult result)
    {
        if (!session.TryBeginCompletion())
        {
            return;
        }

        var wasCurrent = false;
        lock (_sync)
        {
            if (ReferenceEquals(_recognitionSession, session))
            {
                _recognitionSession = null;
                _lastRecognitionResult = result;
                wasCurrent = true;
            }
        }

        if (wasCurrent)
        {
            SetMicrophoneActivity(isActive: false);
            if (State == SpeechServiceState.Listening)
            {
                SetState(SpeechServiceState.Idle);
            }
        }

        session.Completion.TrySetResult(result);
        if (!result.Succeeded && result.ErrorCode is not (SpeechErrorCode.Cancelled or SpeechErrorCode.MicrophoneMuted))
        {
            RaiseError(result.ErrorCode, result.ErrorMessage!);
        }

        ThreadPool.QueueUserWorkItem(static state => ((RecognitionSession)state!).DisposeResources(), session);
    }

    private void CompleteSpeech(SpeechSession session, SpeechOperationResult result)
    {
        if (!session.TryBeginCompletion())
        {
            return;
        }

        var wasCurrent = false;
        lock (_sync)
        {
            if (ReferenceEquals(_speechSession, session))
            {
                _speechSession = null;
                wasCurrent = true;
            }
        }

        if (wasCurrent && State == SpeechServiceState.Speaking)
        {
            SetState(SpeechServiceState.Idle);
        }

        session.Completion.TrySetResult(result);
        if (!result.Succeeded && result.ErrorCode != SpeechErrorCode.Cancelled)
        {
            RaiseError(result.ErrorCode, result.ErrorMessage!);
        }

        ThreadPool.QueueUserWorkItem(static state => ((SpeechSession)state!).DisposeResources(), session);
    }

    private void CancelRecognitionCore()
    {
        RecognitionSession? session;
        lock (_sync)
        {
            session = _recognitionSession;
        }

        if (session is null)
        {
            return;
        }

        TryCancelRecognition(session);
        CompleteRecognition(
            session,
            SpeechRecognitionResult.Failure(SpeechErrorCode.Cancelled, "El reconocimiento fue cancelado."));
    }

    private void CancelSpeechCore()
    {
        SpeechSession? session;
        lock (_sync)
        {
            session = _speechSession;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            session.Synthesizer.SpeakAsyncCancelAll();
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
        }

        CompleteSpeech(
            session,
            SpeechOperationResult.Failure(SpeechErrorCode.Cancelled, "La salida de voz fue cancelada."));
    }

    private static void TryCancelRecognition(RecognitionSession session)
    {
        try
        {
            session.Engine.RecognizeAsyncCancel();
        }
        catch (Exception exception) when (IsExpectedSpeechFailure(exception))
        {
        }
    }

    private async Task<bool> TryEnterGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetState(SpeechServiceState state)
    {
        SpeechServiceState previousState;
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            previousState = _state;
            _state = state;
        }

        RaiseSafely(this, StateChanged, new SpeechStateChangedEventArgs(previousState, state));
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

        RaiseSafely(
            this,
            MicrophoneActivityChanged,
            new MicrophoneActivityChangedEventArgs(isActive));
    }

    private bool IsDisposed()
    {
        lock (_sync)
        {
            return _isDisposed;
        }
    }

    private void RaiseError(SpeechErrorCode errorCode, string message) =>
        RaiseSafely(this, Error, new SpeechServiceErrorEventArgs(errorCode, message));

    private static SpeechOperationResult CancelledOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");

    private static SpeechOperationResult DisposedOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Disposed, "El servicio de voz ya fue cerrado.");

    private static bool IsExpectedSpeechFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or PlatformNotSupportedException
            or ObjectDisposedException
            or COMException;

    private static void RaiseSafely<TEventArgs>(
        object sender,
        EventHandler<TEventArgs>? handlers,
        TEventArgs eventArgs)
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
                handler(sender, eventArgs);
            }
            catch
            {
                // Los consumidores de eventos no deben dejar capturado el micrófono.
            }
        }
    }

    private sealed class RecognitionSession(SpeechRecognitionEngine engine)
    {
        private readonly List<(string Text, float Confidence)> _segments = [];
        private readonly object _segmentsSync = new();
        private int _completionStarted;
        private int _resourcesDisposed;

        public SpeechRecognitionEngine Engine { get; } = engine;

        public TaskCompletionSource<SpeechRecognitionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventHandler<SpeechRecognizedEventArgs>? RecognizedHandler { get; set; }

        public EventHandler<SpeechHypothesizedEventArgs>? HypothesizedHandler { get; set; }

        public EventHandler<RecognizeCompletedEventArgs>? CompletedHandler { get; set; }

        public bool IsActive => Volatile.Read(ref _completionStarted) == 0;

        public void AddSegment(string text, float confidence)
        {
            lock (_segmentsSync)
            {
                _segments.Add((text, confidence));
            }
        }

        public SpeechRecognitionResult BuildResult()
        {
            lock (_segmentsSync)
            {
                if (_segments.Count == 0)
                {
                    return SpeechRecognitionResult.Success();
                }

                return SpeechRecognitionResult.Success(
                    string.Join(" ", _segments.Select(segment => segment.Text)),
                    (float)_segments.Average(segment => segment.Confidence));
            }
        }

        public bool TryBeginCompletion() =>
            Interlocked.Exchange(ref _completionStarted, 1) == 0;

        public void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            {
                return;
            }

            if (RecognizedHandler is not null)
            {
                Engine.SpeechRecognized -= RecognizedHandler;
            }

            if (HypothesizedHandler is not null)
            {
                Engine.SpeechHypothesized -= HypothesizedHandler;
            }

            if (CompletedHandler is not null)
            {
                Engine.RecognizeCompleted -= CompletedHandler;
            }

            Engine.Dispose();
        }
    }

    private sealed class SpeechSession(SpeechSynthesizer synthesizer)
    {
        private int _completionStarted;
        private int _resourcesDisposed;

        public SpeechSynthesizer Synthesizer { get; } = synthesizer;

        public TaskCompletionSource<SpeechOperationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventHandler<SpeakCompletedEventArgs>? CompletedHandler { get; set; }

        public bool TryBeginCompletion() =>
            Interlocked.Exchange(ref _completionStarted, 1) == 0;

        public void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            {
                return;
            }

            if (CompletedHandler is not null)
            {
                Synthesizer.SpeakCompleted -= CompletedHandler;
            }

            Synthesizer.Dispose();
        }
    }
}
