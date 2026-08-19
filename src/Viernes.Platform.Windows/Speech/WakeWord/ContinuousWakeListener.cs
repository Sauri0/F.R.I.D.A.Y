using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio;
using NAudio.Wave;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// El oído: micrófono siempre abierto, diez segundos de memoria y el nombre en cualquier posición.
/// </summary>
/// <remarks>
/// Reemplaza la coreografía anterior, que era: SAPI tiene el micrófono → oye el nombre → se lo
/// suelta → se esperan 220 ms a que el driver lo libere → Whisper lo abre y empieza a grabar. En ese
/// esquema, todo lo dicho antes del nombre no lo tenía nadie, así que había que decir «Hola
/// Viernes», esperar, y recién ahí hablar.
/// <para>
/// Acá el micrófono lo abre una sola cosa —esta clase— y el audio se reparte a tres lugares al mismo
/// tiempo: una ventana rodante de diez segundos, el reconocedor de nombre (que ahora lee de un caño
/// en memoria en vez de abrir el dispositivo por su cuenta) y el detector de voz. Cuando el nombre
/// suena, el audio anterior <em>ya está grabado</em>: se lo saca de la ventana, se sigue grabando
/// hasta que la persona se calla, y se entrega todo junto. «Viernes creame una carpeta en el
/// escritorio» sale de un tirón.
/// </para>
/// <para>
/// Y como al dispararse manda la frase entera al modelo en vez de contestar «¿sí?», un falso
/// positivo dejó de doler: «el viernes tengo turno» le llega al modelo, que ve que no es un pedido y
/// no hace nada. Por eso el umbral de confianza puede bajar a 0,60 y aceptar el nombre solo.
/// </para>
/// <para>
/// Cómo se cablea desde el shell: se escucha <see cref="UtteranceCaptured"/> y el WAV se le pasa a
/// <c>WhisperSpeechRecognitionProvider.TranscribeWaveAsync</c>, que transcribe sin tomar el
/// micrófono; el texto que sale va directo al modelo. No hace falta ninguna coordinación de
/// dispositivo, que era todo lo que hacía <c>WakeWordRecognitionCoordinator</c>. Lo que sí hay que
/// mantener: parar esto antes de un push-to-talk y mientras la asistente habla, porque si no se oye
/// a sí misma y se activa sola.
/// </para>
/// <para>
/// <b>Qué se comprobó y qué no.</b> Esto se escribió, compiló, se midió pieza por pieza… y no
/// funcionaba <em>nada</em>: no arrancaba, y cuando arrancaba no oía. Tres cosas, todas en el
/// encuentro con SAPI y todas silenciosas, todas arregladas en <see cref="AudioPipeStream"/> y con
/// el detalle escrito ahí:
/// </para>
/// <para>
/// 1. SAPI pide el largo del caño apenas se lo pasan y el caño lanzaba por no tener largo; esa
/// excepción no estaba entre las esperadas y dejaba la inicialización del asistente a medio hacer.
/// 2. Antes de leer un byte pregunta la posición con <c>Seek(0, Current)</c>; lanzando ahí, abandona
/// la entrada sin leer, sin reconocer y sin fallar. 3. Si una lectura devuelve menos de lo pedido,
/// no vuelve a pedir nunca.
/// </para>
/// <para>
/// Hoy sí: abre el micrófono de este equipo en unos 300 ms, queda en <c>Listening</c> y se sostiene,
/// y el nombre se reconoce a lo largo del caño —hay una prueba que lo corre de punta a punta con voz
/// sintetizada—. Lo que sigue sin comprobarse es con una persona hablando: que la gramática dispare
/// con esa voz, y que el recorte del pre-roll caiga donde tiene que caer. Para eso hay que hablarle.
/// </para>
/// </remarks>
public sealed class ContinuousWakeListener : IWakeWordService
{
    private static readonly WaveFormat CaptureFormat = new(16_000, 16, 1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ContinuousWakeListenerOptions _options;
    private readonly string[] _phrases;
    private readonly object _sync = new();
    private readonly IVoiceActivityDetector _detector;
    private readonly bool _ownsDetector;
    private readonly WakeUtteranceAssembler _assembler;

    private WaveInEvent? _capture;
    private AudioPipeStream? _pipe;
    private SpeechRecognitionEngine? _engine;
    private WakeWordServiceState _state;
    private bool _isMicrophoneActive;
    private bool _isMuted;
    private bool _desiredRunning;
    private bool _disposed;

    /// <summary>
    /// Arma el oído. No abre el micrófono hasta <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="options">Qué nombres, cuánto recuerda y con qué detector.</param>
    /// <param name="detector">
    /// Quién decide qué es voz. Si se pasa uno, <b>no se desecha acá</b>: es de quien lo prestó.
    /// </param>
    /// <remarks>
    /// El detector entra por afuera para poder compartir <em>uno solo</em> con la sesión en vivo. El
    /// modelo entrenado tarda en cargarse y esa carga estaba pasando dos veces —una acá y otra por
    /// conversación, adentro del micrófono de la sesión en vivo, justo después de abrir el
    /// websocket—, así que se perdían las primeras palabras de la primera frase. Compartirlo es
    /// seguro porque los dos nunca capturan a la vez: abrir la sesión en vivo para el oído continuo
    /// y viceversa; el micrófono es uno solo y eso ya estaba resuelto.
    /// </remarks>
    public ContinuousWakeListener(
        ContinuousWakeListenerOptions? options = null,
        IVoiceActivityDetector? detector = null)
    {
        _options = options ?? new ContinuousWakeListenerOptions();
        _phrases = _options.ValidateAndNormalizePhrases();
        _ownsDetector = detector is null;
        string? reason = null;
        _detector = detector ?? CreateDetector(_options, out reason);
        TrainedDetectorUnavailableReason = reason;
        _assembler = new WakeUtteranceAssembler(
            _options,
            _detector,
            CaptureFormat.SampleRate,
            CaptureFormat.BitsPerSample,
            CaptureFormat.Channels);
    }

    /// <summary>
    /// Por qué no se está usando el detector entrenado, si es que no se está usando.
    /// </summary>
    public string? TrainedDetectorUnavailableReason { get; }

    /// <summary>Quién está decidiendo qué es voz.</summary>
    public VoiceActivityDetectorInfo VoiceDetectorInfo => _detector.Info;

    /// <summary>
    /// La comparación entre los dos detectores, si está encendida.
    /// </summary>
    /// <remarks>
    /// Es lo que hay que mirar antes de confiar en el detector entrenado: si coinciden el 99 % del
    /// tiempo, cambiar no aporta; si difieren, hay que escuchar en qué difieren.
    /// </remarks>
    public VoiceActivityAgreement? DetectorAgreement =>
        (_detector as VoiceActivityScoreboard)?.Agreement;

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

    /// <summary>
    /// Sigue siendo una gramática de SAPI y sigue equivocándose; lo que cambió es cuánto cuesta.
    /// </summary>
    public bool IsDemoOnly => true;

    public string ReliabilityNotice =>
        "El nombre lo detecta una gramática de SAPI, que se equivoca: «el viernes tengo turno» " +
        "dispara con la misma confianza que un pedido real. Ya no interrumpe por eso —manda la " +
        "frase entera al modelo, que ve que nadie le pidió nada— pero mientras escucha mantiene " +
        "el micrófono abierto y visible en el indicador.";

    public IReadOnlyList<string> Phrases => _phrases;

    public event EventHandler<WakeWordStateChangedEventArgs>? StateChanged;

    public event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    public event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected;

    public event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    /// <summary>Nivel del micrófono, para que la interfaz se mueva con la voz aun sin activar.</summary>
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <summary>
    /// La frase completa, ya con el audio anterior al nombre pegado adelante.
    /// </summary>
    public event EventHandler<WakeUtteranceEventArgs>? UtteranceCaptured;

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

                if (_capture is not null)
                {
                    return SpeechOperationResult.Success();
                }
            }

            return StartCore();
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

            StopCore(WakeWordServiceState.Stopped);
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
                // Silenciar tiene que soltar el dispositivo, no sólo ignorar lo que llega: el
                // indicador del sistema es lo que le dice al usuario si lo están oyendo.
                StopCore(WakeWordServiceState.Muted);
                return SpeechOperationResult.Success();
            }

            if (shouldResume)
            {
                return StartCore();
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

            StopCore(WakeWordServiceState.Stopped);
            if (_ownsDetector)
            {
                _detector.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// El detector que le corresponde a estas opciones: el entrenado si está el modelo, y si no la
    /// heurística.
    /// </summary>
    /// <remarks>
    /// Público para que el anfitrión pueda armar <b>uno solo</b> antes de que haga falta y
    /// prestárselo también a la sesión en vivo. Cargar el modelo cuesta, y hacerlo en el medio de una
    /// conversación se paga con las primeras palabras.
    /// </remarks>
    public static IVoiceActivityDetector CreateDetector(
        ContinuousWakeListenerOptions options,
        out string? unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(options);
        return BuildDetector(options, out unavailableReason);
    }

    private static IVoiceActivityDetector BuildDetector(
        ContinuousWakeListenerOptions options,
        out string? unavailableReason)
    {
        unavailableReason = null;
        var heuristic = new HeuristicVoiceActivityDetector();
        if (!options.PreferTrainedVoiceDetector)
        {
            return heuristic;
        }

        var runner = OnnxVadModelRunner.TryCreate(options.TrainedVoiceModelPath, out unavailableReason);
        if (runner is null)
        {
            // Que falte el modelo no puede dejar al asistente sordo: la heurística sigue siendo el
            // respaldo y el motivo queda a la vista para poder decirlo en la interfaz.
            return heuristic;
        }

        var trained = new SileroVoiceActivityDetector(runner);
        return options.CompareVoiceDetectors
            ? new VoiceActivityScoreboard(trained, heuristic)
            : trained;
    }

    private SpeechOperationResult StartCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetState(WakeWordServiceState.Unavailable);
            return SpeechOperationResult.Failure(
                SpeechErrorCode.Unavailable,
                "El oído continuo solo está disponible en Windows.");
        }

        WaveInEvent? capture = null;
        AudioPipeStream? pipe = null;
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

            // El caño guarda dos segundos: si SAPI se atrasa más que eso, prefiere perder audio
            // viejo antes que crecer sin límite en un proceso que escucha todo el día.
            pipe = new AudioPipeStream(TimeSpan.FromSeconds(2), CaptureFormat.AverageBytesPerSecond);
            engine = new SpeechRecognitionEngine(recognizer.Id)
            {
                // Los mismos valores que usaba la demo anterior. No se ponen en cero aunque cero
                // signifique «sin límite» en SAPI: acá ya funcionaba así y no es el lugar para
                // averiguar si el cero se interpreta igual leyendo de un stream.
                InitialSilenceTimeout = TimeSpan.FromHours(1),
                BabbleTimeout = TimeSpan.FromHours(1)
            };
            var grammarBuilder = new GrammarBuilder { Culture = recognizer.Culture };
            grammarBuilder.Append(new Choices(_phrases));
            engine.LoadGrammar(new Grammar(grammarBuilder) { Name = "Viernes.OidoContinuo" });
            engine.SpeechRecognized += OnSpeechRecognized;

            capture = new WaveInEvent
            {
                DeviceNumber = _options.InputDeviceNumber,
                BufferMilliseconds = _options.BufferMilliseconds,
                WaveFormat = CaptureFormat
            };
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            lock (_sync)
            {
                _capture = capture;
                _pipe = pipe;
                _engine = engine;
            }

            _assembler.Reset();
            SetState(WakeWordServiceState.Listening);
            SetMicrophoneActivity(isActive: true);

            // Primero la captura y después el reconocedor: al revés, SAPI se queda bloqueado
            // leyendo un caño que todavía no tiene quien lo llene.
            capture.StartRecording();
            engine.SetInputToAudioStream(
                pipe,
                new SpeechAudioFormatInfo(
                    CaptureFormat.SampleRate,
                    AudioBitsPerSample.Sixteen,
                    AudioChannel.Mono));
            engine.RecognizeAsync(RecognizeMode.Multiple);
            return SpeechOperationResult.Success();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            CleanUp(capture, engine, pipe);
            lock (_sync)
            {
                _capture = null;
                _engine = null;
                _pipe = null;
            }

            SetMicrophoneActivity(isActive: false);
            SetState(WakeWordServiceState.Faulted);
            var message = $"El oído continuo no pudo iniciar: {exception.Message}";
            RaiseError(SpeechErrorCode.DeviceError, message);
            return SpeechOperationResult.Failure(SpeechErrorCode.DeviceError, message);
        }
    }

    private void StopCore(WakeWordServiceState finalState)
    {
        WaveInEvent? capture;
        SpeechRecognitionEngine? engine;
        AudioPipeStream? pipe;
        lock (_sync)
        {
            capture = _capture;
            engine = _engine;
            pipe = _pipe;
            _capture = null;
            _engine = null;
            _pipe = null;
        }

        _assembler.Reset();
        CleanUp(capture, engine, pipe);
        SetMicrophoneActivity(isActive: false);
        SetState(finalState);
    }

    private static void CleanUp(WaveInEvent? capture, SpeechRecognitionEngine? engine, AudioPipeStream? pipe)
    {
        if (capture is not null)
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
            }
        }

        // Cerrar el caño antes de desechar el engine: si no, el hilo de SAPI queda esperando audio
        // adentro de Read y Dispose no vuelve nunca.
        pipe?.Complete();

        if (engine is not null)
        {
            try
            {
                engine.RecognizeAsyncCancel();
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
            }

            engine.Dispose();
        }

        capture?.Dispose();
        pipe?.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        AudioPipeStream? pipe;
        lock (_sync)
        {
            pipe = _pipe;
        }

        if (pipe is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        var block = eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded);

        // El mismo audio va a los dos lados: al reconocedor de nombre por el caño y al ensamblador,
        // que es el que guarda la ventana rodante y decide dónde termina la frase.
        pipe.Write(block);
        var utterance = _assembler.Write(block);
        var decision = _assembler.LastDecision;
        RaiseSafely(AudioLevelChanged, new AudioLevelEventArgs(decision.Level, decision.IsVoice));

        if (utterance is not null)
        {
            RaiseSafely(
                UtteranceCaptured,
                new WakeUtteranceEventArgs(
                    utterance.Wave,
                    utterance.Phrase,
                    utterance.Confidence,
                    utterance.PreRollDuration,
                    utterance.TailDuration,
                    utterance.StopReason,
                    utterance.DetectedAt));
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        if (eventArgs.Result is null || eventArgs.Result.Confidence < _options.MinimumConfidence)
        {
            return;
        }

        var text = WakePhrasePolicy.Normalize(eventArgs.Result.Text);
        var phrase = _phrases.FirstOrDefault(candidate =>
            string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
        if (phrase is null || !WakePhrasePolicy.Accepts(phrase, _options.RequireCompoundPhrase))
        {
            return;
        }

        if (_assembler.NameHeard(phrase, eventArgs.Result.Confidence))
        {
            RaiseSafely(
                WakeWordDetected,
                new WakeWordDetectedEventArgs(phrase, eventArgs.Result.Confidence, DateTimeOffset.UtcNow));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        SetMicrophoneActivity(isActive: false);
        if (eventArgs.Exception is not null)
        {
            SetState(WakeWordServiceState.Faulted);
            RaiseError(
                SpeechErrorCode.DeviceError,
                $"La captura continua se detuvo: {eventArgs.Exception.Message}");
        }
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

    /// <summary>
    /// Qué fallas se dan por posibles al abrir o cerrar el oído.
    /// </summary>
    /// <remarks>
    /// Acá decía <c>PlatformNotSupportedException</c> y tenía que decir <c>NotSupportedException</c>,
    /// que es la madre. La diferencia no era teórica: SAPI pedía el largo del caño de audio, el caño
    /// —que es infinito— lanzaba la de la madre, y como no estaba en esta lista se iba para arriba
    /// desde <see cref="StartAsync"/> y dejaba la inicialización del asistente a medio hacer sin un
    /// renglón en ningún lado. El largo ya está arreglado; esto queda para que la próxima sorpresa
    /// de una API que no es nuestra degrade en vez de tumbar.
    /// </remarks>
    private static bool IsExpectedFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or ObjectDisposedException
            or COMException
            or MmException
            or UnauthorizedAccessException
            or IOException;

    private static SpeechOperationResult CancelledOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");

    private static SpeechOperationResult DisposedOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Disposed, "El oído ya fue cerrado.");

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
                // Un consumidor que falla no puede dejar el micrófono tomado ni cortar la escucha.
            }
        }
    }

    private sealed record StateChange(WakeWordServiceState Previous, WakeWordServiceState Current);
}
