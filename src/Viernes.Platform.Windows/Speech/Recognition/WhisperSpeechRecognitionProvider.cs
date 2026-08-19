using System.Buffers.Binary;
using System.Text;
using NAudio;
using NAudio.Utils;
using NAudio.Wave;
using Whisper.net;

namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// PTT local con Whisper.net. El modelo GGML debe existir previamente; este servicio
/// nunca lo descarga y nunca transmite audio.
/// </summary>
public sealed class WhisperSpeechRecognitionProvider : ISpeechRecognitionProvider
{
    private static readonly WaveFormat CaptureFormat = new(16_000, 16, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly WhisperSpeechRecognitionOptions _options;
    private readonly string _modelPath;
    private readonly object _sync = new();
    private readonly object _factoryGate = new();
    private WhisperFactory? _factory;
    private CaptureSession? _session;
    private SpeechRecognitionResult? _lastResult;
    private SpeechRecognitionProviderState _state;
    private bool _isMicrophoneActive;
    private bool _isMicrophoneMuted;
    private bool _disposed;

    public WhisperSpeechRecognitionProvider(WhisperSpeechRecognitionOptions? options = null)
    {
        _options = options ?? new WhisperSpeechRecognitionOptions();
        _options.Validate();
        _modelPath = Path.GetFullPath(_options.ModelPath);
    }

    public SpeechRecognitionProviderInfo Info { get; } = new(
        SpeechRecognitionProviderKind.WhisperLocal,
        "Whisper local",
        RunsLocally: true,
        "Graba únicamente durante push-to-talk y procesa el WAV en el equipo; no descarga modelos ni envía audio.");

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

    public event EventHandler<SpeechRecognitionProviderStateChangedEventArgs>? StateChanged;

    public event EventHandler<MicrophoneActivityChangedEventArgs>? MicrophoneActivityChanged;

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    public event EventHandler<SpeechTranscriptionEventArgs>? TranscriptionUpdated;

    public event EventHandler<SpeechServiceErrorEventArgs>? ServiceError;

    public SpeechRecognitionProviderAvailability GetAvailability()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return Unavailable("El runtime CPU de Whisper requiere Windows 11 o Windows Server 2022.");
        }

        if (_options.RequireModelUnderViernesLocalAppData && !IsInsideDefaultModelDirectory(_modelPath))
        {
            return Unavailable("El modelo debe ubicarse bajo %LOCALAPPDATA%\\Viernes\\Models\\Whisper.");
        }

        if (!string.Equals(Path.GetExtension(_modelPath), ".bin", StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("La ruta configurada no apunta a un modelo GGML .bin.");
        }

        if (!File.Exists(_modelPath))
        {
            return Unavailable($"Falta el modelo local en {_modelPath}.");
        }

        try
        {
            if (new FileInfo(_modelPath).Length < _options.MinimumModelSizeBytes)
            {
                return Unavailable("El archivo configurado es demasiado pequeño para ser un modelo Whisper válido.");
            }

            if (WaveInEvent.DeviceCount == 0)
            {
                return Unavailable("Windows no informó ningún micrófono.");
            }

            // -1 es WAVE_MAPPER y siempre es válido: se resuelve al predeterminado de Windows.
            if (_options.InputDeviceNumber >= 0 && WaveInEvent.DeviceCount <= _options.InputDeviceNumber)
            {
                return Unavailable("No se encontró el dispositivo de entrada configurado.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MmException)
        {
            return Unavailable($"No se pudo validar Whisper local: {exception.Message}");
        }

        return new SpeechRecognitionProviderAvailability(true);
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
                if (_session is not null)
                {
                    return SpeechOperationResult.Success();
                }
            }

            var availability = GetAvailability();
            if (!availability.IsAvailable)
            {
                return SpeechOperationResult.Failure(
                    SpeechErrorCode.Unavailable,
                    availability.UnavailableReason!);
            }

            CaptureSession? session = null;
            try
            {
                var capture = new WaveInEvent
                {
                    DeviceNumber = _options.InputDeviceNumber,
                    BufferMilliseconds = _options.BufferMilliseconds,
                    WaveFormat = CaptureFormat
                };
                var maximumAudioBytes = checked(
                    (long)(CaptureFormat.AverageBytesPerSecond * _options.MaximumRecordingDuration.TotalSeconds));
                session = new CaptureSession(capture, maximumAudioBytes);
                session.LevelObserver = (level, isVoice) =>
                    AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(level, isVoice));
                session.DataHandler = (_, eventArgs) => OnDataAvailable(session, eventArgs);
                session.StoppedHandler = (_, eventArgs) => OnRecordingStopped(session, eventArgs);
                capture.DataAvailable += session.DataHandler;
                capture.RecordingStopped += session.StoppedHandler;

                lock (_sync)
                {
                    _session = session;
                    _lastResult = null;
                }

                SetState(SpeechRecognitionProviderState.Capturing);
                SetMicrophoneActivity(isActive: true);
                capture.StartRecording();
                return SpeechOperationResult.Success();
            }
            catch (Exception exception) when (exception is MmException
                or InvalidOperationException
                or UnauthorizedAccessException)
            {
                var failure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.DeviceError,
                    $"No se pudo abrir el micrófono para Whisper: {exception.Message}");
                if (session is not null)
                {
                    CompleteSession(session, failure);
                }
                else
                {
                    SetMicrophoneActivity(isActive: false);
                    SetState(SpeechRecognitionProviderState.Idle);
                    RaiseError(failure.ErrorCode, failure.ErrorMessage!);
                }

                return SpeechOperationResult.Failure(failure.ErrorCode, failure.ErrorMessage!);
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
        CaptureSession? session;
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return SpeechRecognitionResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");
        }

        try
        {
            lock (_sync)
            {
                session = _session;
                if (session is null)
                {
                    return _lastResult ?? SpeechRecognitionResult.Success();
                }
            }

            RequestCaptureStop(session);
        }
        finally
        {
            _operationGate.Release();
        }

        if (!session.TryBeginProcessing())
        {
            try
            {
                return await session.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.ProcessingCancellation.Cancel();
                return SpeechRecognitionResult.Failure(SpeechErrorCode.Cancelled, "La transcripción fue cancelada.");
            }
        }

        try
        {
            var stopped = await session.Stopped.Task
                .WaitAsync(_options.CaptureStopTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (stopped.Exception is not null)
            {
                var captureFailure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.DeviceError,
                    $"La captura del micrófono falló: {stopped.Exception.Message}");
                CompleteSession(session, captureFailure);
                return captureFailure;
            }

            session.FinalizeWaveFile();
            if (session.LimitReached)
            {
                var limitFailure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.TimedOut,
                    "La captura alcanzó el límite configurado y fue detenida.");
                CompleteSession(session, limitFailure);
                return limitFailure;
            }

            if (session.AudioDuration < _options.MinimumRecordingDuration)
            {
                var empty = SpeechRecognitionResult.Success();
                CompleteSession(session, empty);
                return empty;
            }

            SetState(SpeechRecognitionProviderState.Transcribing);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                session.ProcessingCancellation.Token);
            var transcription = await TranscribeWaveCoreAsync(session.Audio, linkedCancellation.Token)
                .ConfigureAwait(false);

            // Al push-to-talk no le sirven los tiempos: no hay nada anterior al nombre que separar,
            // porque acá lo abrió el usuario apretando.
            var result = transcription.Result;
            CompleteSession(session, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var cancelled = SpeechRecognitionResult.Failure(
                SpeechErrorCode.Cancelled,
                "La transcripción fue cancelada.");
            CompleteSession(session, cancelled);
            return cancelled;
        }
        catch (TimeoutException)
        {
            var timeout = SpeechRecognitionResult.Failure(
                SpeechErrorCode.TimedOut,
                "El micrófono no se liberó a tiempo y la captura fue cancelada.");
            CompleteSession(session, timeout);
            return timeout;
        }
        catch (Exception exception)
        {
            var failure = SpeechRecognitionResult.Failure(
                SpeechErrorCode.Failed,
                $"Whisper local no pudo transcribir el audio: {exception.Message}");
            CompleteSession(session, failure);
            return failure;
        }
    }

    public async Task<SpeechRecognitionResult> RecognizeSingleUtteranceAsync(
        SingleUtteranceRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new SingleUtteranceRecognitionOptions();
        effectiveOptions.Validate();

        // Cargar el modelo mientras el usuario habla, no después de que terminó. Los segundos de la
        // frase ya estaban ahí; usarlos para tener el modelo listo hace que la carga de la primera
        // vez no se cobre como espera. Si falla, la transcripción lo vuelve a intentar y reporta.
        _ = Task.Run(() =>
        {
            try
            {
                GetFactory();
            }
            catch (Exception)
            {
                // El error real sale en la transcripción, donde se puede informar al usuario.
            }
        }, CancellationToken.None);

        var started = await StartPushToTalkAsync(cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded)
        {
            return SpeechRecognitionResult.Failure(started.ErrorCode, started.ErrorMessage!);
        }

        CaptureSession? session;
        lock (_sync)
        {
            session = _session;
        }

        if (session is null)
        {
            return _lastResult ?? SpeechRecognitionResult.Success();
        }

        session.ConfigureSingleUtterance(effectiveOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(effectiveOptions.MaximumDuration + _options.CaptureStopTimeout);
        try
        {
            // Si el dispositivo no entrega audio, el VAD nunca ve nada y ningún timeout basado en
            // audio dispara: quedaría «Escuchando» hasta agotar la duración máxima, en silencio y
            // sin explicación. Esto lo convierte en un error claro en un segundo y medio.
            var silentDevice = WatchForSilentDeviceAsync(session, timeout.Token);
            var completed = await Task.WhenAny(session.AutoStop.Task, session.Result.Task, silentDevice)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);

            if (ReferenceEquals(completed, silentDevice))
            {
                await CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
                return SpeechRecognitionResult.Failure(
                    SpeechErrorCode.DeviceError,
                    "El micrófono no entregó audio: puede haber quedado tomado por otra aplicación.");
            }

            if (ReferenceEquals(completed, session.Result.Task))
            {
                return await session.Result.Task.ConfigureAwait(false);
            }

            var reason = await session.AutoStop.Task.ConfigureAwait(false);
            if (reason == SingleUtteranceStopReason.InitialSilence)
            {
                await CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
                return SpeechRecognitionResult.Success();
            }

            return await StopPushToTalkAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
            return SpeechRecognitionResult.Failure(
                SpeechErrorCode.TimedOut,
                "El VAD simple no completó una frase dentro del tiempo permitido.");
        }
        catch (OperationCanceledException)
        {
            await CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
            return SpeechRecognitionResult.Failure(
                SpeechErrorCode.Cancelled,
                "El reconocimiento de una frase fue cancelado.");
        }
    }

    public async Task<SpeechOperationResult> CancelPushToTalkAsync(
        CancellationToken cancellationToken = default)
    {
        CaptureSession? session;
        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledOperation();
        }

        try
        {
            lock (_sync)
            {
                session = _session;
                if (session is null)
                {
                    return _disposed ? DisposedOperation() : SpeechOperationResult.Success();
                }
            }

            session.ProcessingCancellation.Cancel();
            RequestCaptureStop(session);
        }
        finally
        {
            _operationGate.Release();
        }

        await CompleteCancellationAsync(session, cancellationToken).ConfigureAwait(false);
        return SpeechOperationResult.Success();
    }

    /// <summary>
    /// Carga el modelo ahora, sin esperar a la primera frase. Devuelve si quedó cargado.
    /// </summary>
    /// <remarks>
    /// El push-to-talk ya adelanta la carga por su cuenta —arranca a cargar mientras la persona
    /// habla, así los segundos de la frase pagan la espera—, pero el oído continuo no puede: entrega
    /// el WAV entero y llama derecho a <see cref="TranscribeWaveWithSegmentsAsync"/>, con la persona
    /// <em>ya callada</em>. Sin esto, la primera vez que alguien dice el nombre después de arrancar
    /// se le cobra la carga entera como demora antes de que el orbe conteste. Medido en este equipo
    /// con <c>--check-whisper</c>: <c>ggml-small.bin</c> tarda 388 ms en cargar.
    /// <para>
    /// Es la misma trampa que ya se pagó con el detector de voz entrenado en el camino en vivo, del
    /// otro lado del mismo problema: cargar un modelo cuando ya hay alguien esperando.
    /// </para>
    /// <para>
    /// No lanza. Si el modelo falta o está roto, el error real sale en la transcripción, que es
    /// donde hay a quién decírselo.
    /// </para>
    /// </remarks>
    public Task<bool> WarmUpAsync() => Task.Run(() =>
    {
        try
        {
            GetFactory();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Transcribe un WAV compatible sin tomar el micrófono. El stream sigue perteneciendo
    /// al llamador y este proveedor no lo persiste.
    /// </summary>
    public async Task<SpeechRecognitionResult> TranscribeWaveAsync(
        Stream waveStream,
        CancellationToken cancellationToken = default) =>
        (await TranscribeWaveWithSegmentsAsync(waveStream, cancellationToken).ConfigureAwait(false))
            .Result;

    /// <summary>
    /// Lo mismo, pero además dice dónde cae cada tramo adentro del audio.
    /// </summary>
    /// <remarks>
    /// Lo necesita el oído continuo: entrega un WAV que arranca <em>antes</em> de que sonara el
    /// nombre, y ese pedazo se dibuja distinto porque no te lo dijeron a vos. Separarlo por los
    /// tiempos que Whisper ya devuelve es gratis; la alternativa era transcribir dos veces el mismo
    /// audio, o sea esperar dos veces.
    /// </remarks>
    public async Task<WaveTranscription> TranscribeWaveWithSegmentsAsync(
        Stream waveStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waveStream);
        if (!waveStream.CanRead)
        {
            throw new ArgumentException("El stream WAV debe ser legible.", nameof(waveStream));
        }

        if (!await TryEnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            return Nothing(SpeechErrorCode.Cancelled, "La operación fue cancelada.");
        }

        try
        {
            if (IsDisposed())
            {
                return Nothing(SpeechErrorCode.Disposed, "El proveedor de voz ya fue cerrado.");
            }

            lock (_sync)
            {
                if (_session is not null)
                {
                    return Nothing(
                        SpeechErrorCode.Failed,
                        "No se puede transcribir un WAV mientras el micrófono está en uso.");
                }
            }

            var availability = GetAvailability();
            if (!availability.IsAvailable)
            {
                return Nothing(SpeechErrorCode.Unavailable, availability.UnavailableReason!);
            }

            if (waveStream.CanSeek)
            {
                waveStream.Position = 0;
            }

            SetState(SpeechRecognitionProviderState.Transcribing);
            try
            {
                return await TranscribeWaveCoreAsync(waveStream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Nothing(SpeechErrorCode.Cancelled, "La transcripción WAV fue cancelada.");
            }
            catch (Exception exception)
            {
                var failure = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Failed,
                    $"Whisper local no pudo transcribir el WAV: {exception.Message}");
                RaiseError(failure.ErrorCode, failure.ErrorMessage!);
                return new WaveTranscription(failure, []);
            }
            finally
            {
                SetState(SpeechRecognitionProviderState.Idle);
            }
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
        CaptureSession? session = null;
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

                _isMicrophoneMuted = isMuted;
                if (isMuted)
                {
                    session = _session;
                }
            }

            if (session is not null)
            {
                session.ProcessingCancellation.Cancel();
                RequestCaptureStop(session);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (session is not null)
        {
            await CompleteCancellationAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return SpeechOperationResult.Success();
    }

    private async Task CompleteCancellationAsync(
        CaptureSession session,
        CancellationToken cancellationToken)
    {
        if (session.TryBeginProcessing())
        {
            try
            {
                await session.Stopped.Task
                    .WaitAsync(_options.CaptureStopTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }

            CompleteSession(
                session,
                SpeechRecognitionResult.Failure(SpeechErrorCode.Cancelled, "La captura fue cancelada."));
        }
        else
        {
            try
            {
                await session.Result.Task
                    .WaitAsync(_options.CaptureStopTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        CaptureSession? session;
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                session = _session;
            }

            if (session is not null)
            {
                session.ProcessingCancellation.Cancel();
                RequestCaptureStop(session);
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (session is not null)
        {
            await CompleteCancellationAsync(session, CancellationToken.None).ConfigureAwait(false);
        }

        SetMicrophoneActivity(isActive: false);
        SetState(SpeechRecognitionProviderState.Idle);

        lock (_factoryGate)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }

    /// <summary>
    /// El modelo se carga una sola vez y queda en memoria. Antes se construía una fábrica nueva en
    /// cada transcripción, lo que significaba leer del disco y mapear los ~466 MB del modelo, usarlo
    /// para dos segundos de audio y tirarlo — en cada turno de la conversación. Era la mayor parte
    /// del silencio entre que terminabas de hablar y te contestaba.
    /// </summary>
    private WhisperFactory GetFactory()
    {
        var factory = Volatile.Read(ref _factory);
        if (factory is not null)
        {
            return factory;
        }

        lock (_factoryGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _factory ??= WhisperFactory.FromPath(_modelPath);
        }
    }

    /// <summary>
    /// Vocabulario con el que se ceba el decodificador antes de transcribir.
    /// </summary>
    /// <remarks>
    /// Whisper elige entre palabras que suenan parecido usando lo que viene antes como contexto. Sin
    /// nada antes elige por frecuencia general del castellano, y ahí «carpeta» pierde contra
    /// «garpeza». Está medido en el registro real de este equipo: «¿podés hacerme una carpeta en el
    /// escritorio…?» llegó como <em>«Podéis hacer una carpeta en el escritorio y desearme pan
    /// triste»</em>, y «no veo ninguna carpeta» como <em>«no veo ninguna garpeza»</em>. El modelo
    /// recibía una orden que no quería decir nada, y no hacía nada — y desde afuera se veía como si
    /// no supiera crear carpetas.
    /// <para>
    /// Es una frase y no una lista de palabras sueltas, porque el prompt entra como texto previo:
    /// tiene que parecerse a lo que se va a decir. Y va en rioplatense por la misma razón —cebar con
    /// «puedes» hace que escuche «puedes» donde dijiste «podés»—.
    /// </para>
    /// </remarks>
    private const string VocabularyPrompt =
        "Che, creame una carpeta en el escritorio y abrila. Abrí Spotify y poné una canción. " +
        "Buscá el archivo en documentos, descargas o imágenes. Movelo, copialo, borralo, " +
        "recuperalo. Cerrá esa aplicación, subí el volumen, pausá. Acordate de esto, recordame " +
        "mañana. Mirá la pantalla. Callate, desactivate, dejá de escuchar, descansá. Dale, listo.";

    private static WaveTranscription Nothing(SpeechErrorCode code, string message) =>
        new(SpeechRecognitionResult.Failure(code, message), []);

    private async Task<WaveTranscription> TranscribeWaveCoreAsync(
        Stream waveStream,
        CancellationToken cancellationToken)
    {
        if (waveStream.CanSeek)
        {
            waveStream.Position = 0;
        }

        using var processor = GetFactory().CreateBuilder()
            .WithLanguage(_options.Language)
            .WithPrompt(VocabularyPrompt)
            .WithProbabilities()

            // Los horarios por palabra son lo único que permite separar lo que se dijo antes del
            // nombre de lo que se le dijo a ella. Por tramo no alcanza: medido con una frase que
            // tiene un punto en el medio, Whisper devolvió UN tramo de punta a punta.
            .WithTokenTimestamps()
            .Build();
        var text = new StringBuilder();
        var probabilities = new List<float>();
        var segments = new List<TranscribedSegment>();

        await foreach (var segment in processor.ProcessAsync(waveStream, cancellationToken).ConfigureAwait(false))
        {
            var segmentText = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(segmentText))
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(segmentText);
            var probability = float.IsFinite(segment.Probability)
                ? Math.Clamp(segment.Probability, 0f, 1f)
                : 0f;
            probabilities.Add(probability);
            segments.Add(new TranscribedSegment(
                segmentText,
                segment.Start,
                segment.End,
                probability,
                ReadWords(segment)));
            RaiseSafely(
                TranscriptionUpdated,
                new SpeechTranscriptionEventArgs(segmentText, probability, isFinal: true));
        }

        return new WaveTranscription(
            SpeechRecognitionResult.Success(
                text.ToString(),
                probabilities.Count == 0 ? null : probabilities.Average()),
            segments);
    }

    /// <summary>
    /// Junta los tokens del tramo en palabras con su horario.
    /// </summary>
    /// <remarks>
    /// Whisper no entrega palabras: entrega tokens, y una palabra puede ser varios. El corte es el
    /// espacio de adelante —así los devuelve el tokenizador—, así que se abre palabra nueva cuando
    /// un token empieza con espacio y si no se pega al anterior.
    /// <para>
    /// Los tokens especiales llegan mezclados con los de texto y se ven como <c>[_TT_310]</c> o
    /// <c>&lt;|es|&gt;</c>. No son nada que se haya dicho; si se colaran, la línea mostraría marcas
    /// del modelo entre las palabras de la persona.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<TimedWord> ReadWords(SegmentData segment)
    {
        if (segment.Tokens is null || segment.Tokens.Length == 0)
        {
            return [];
        }

        var words = new List<TimedWord>();
        var pending = new StringBuilder();
        var from = TimeSpan.Zero;
        var to = TimeSpan.Zero;

        void Close()
        {
            var text = pending.ToString().Trim();
            pending.Clear();
            if (text.Length > 0)
            {
                words.Add(new TimedWord(text, from, to));
            }
        }

        foreach (var token in segment.Tokens)
        {
            var text = token.Text;
            if (string.IsNullOrEmpty(text) ||
                (text.StartsWith('[') && text.EndsWith(']')) ||
                (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal)))
            {
                continue;
            }

            // El t0/t1 de whisper.cpp viene en centésimas de segundo, no en milisegundos. Tomarlo
            // por milisegundos da una frase cien veces más corta y el corte cae siempre en el mismo
            // lado: nunca se rescataría nada.
            var start = TimeSpan.FromMilliseconds(token.Start * 10);
            var end = TimeSpan.FromMilliseconds(token.End * 10);

            if (text.StartsWith(' ') && pending.Length > 0)
            {
                Close();
            }

            if (pending.Length == 0)
            {
                from = start;
            }

            pending.Append(text);
            to = end > to ? end : to;
        }

        Close();
        return words;
    }

    private void OnDataAvailable(CaptureSession session, WaveInEventArgs eventArgs)
    {
        if (!session.IsActive)
        {
            return;
        }

        var reachedLimit = session.WriteAudio(eventArgs.Buffer, eventArgs.BytesRecorded);
        if (reachedLimit && session.TryQueueLimitStop())
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                RequestCaptureStop(session);
                _ = CompleteLimitReachedAsync(session);
            });
        }
    }

    private void OnRecordingStopped(CaptureSession session, StoppedEventArgs eventArgs)
    {
        SetMicrophoneActivity(isActive: false);
        session.Stopped.TrySetResult(eventArgs);
        if (eventArgs.Exception is not null && session.TryQueueErrorCompletion())
        {
            ThreadPool.QueueUserWorkItem(_ => _ = CompleteCaptureErrorAsync(session, eventArgs.Exception));
        }
    }

    private async Task CompleteLimitReachedAsync(CaptureSession session)
    {
        if (!session.TryBeginProcessing())
        {
            return;
        }

        try
        {
            await session.Stopped.Task.WaitAsync(_options.CaptureStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        CompleteSession(
            session,
            SpeechRecognitionResult.Failure(
                SpeechErrorCode.TimedOut,
                "La captura alcanzó el límite configurado y fue detenida."));
    }

    private async Task CompleteCaptureErrorAsync(CaptureSession session, Exception exception)
    {
        if (!session.TryBeginProcessing())
        {
            return;
        }

        await Task.Yield();
        CompleteSession(
            session,
            SpeechRecognitionResult.Failure(
                SpeechErrorCode.DeviceError,
                $"La captura del micrófono falló: {exception.Message}"));
    }

    private void CompleteSession(CaptureSession session, SpeechRecognitionResult result)
    {
        if (!session.TryComplete())
        {
            return;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                _lastResult = result;
            }
        }

        SetMicrophoneActivity(isActive: false);
        SetState(SpeechRecognitionProviderState.Idle);
        session.Result.TrySetResult(result);
        session.Dispose();

        if (!result.Succeeded && result.ErrorCode != SpeechErrorCode.Cancelled)
        {
            RaiseError(result.ErrorCode, result.ErrorMessage!);
        }
    }

    private static void RequestCaptureStop(CaptureSession session)
    {
        if (!session.TryRequestStop())
        {
            return;
        }

        try
        {
            session.Capture.StopRecording();
        }
        catch (Exception exception) when (exception is MmException or InvalidOperationException)
        {
            session.Stopped.TrySetResult(new StoppedEventArgs(exception));
        }
    }

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

        RaiseSafely(StateChanged, new SpeechRecognitionProviderStateChangedEventArgs(previous, state));
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
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsDisposed()
    {
        lock (_sync)
        {
            return _disposed;
        }
    }

    private static bool IsInsideDefaultModelDirectory(string modelPath)
    {
        var root = Path.GetFullPath(WhisperSpeechRecognitionOptions.GetDefaultModelDirectory());
        var relative = Path.GetRelativePath(root, modelPath);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static SpeechRecognitionProviderAvailability Unavailable(string reason) => new(false, reason);

    private static SpeechOperationResult CancelledOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Cancelled, "La operación fue cancelada.");

    private static SpeechOperationResult DisposedOperation() =>
        SpeechOperationResult.Failure(SpeechErrorCode.Disposed, "El proveedor de voz ya fue cerrado.");

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

    private sealed class CaptureSession : IDisposable
    {
        private readonly object _audioSync = new();
        private readonly long _maximumAudioBytes;
        private readonly IgnoreDisposeStream _nonClosingAudio;
        private readonly WaveFileWriter _writer;
        private int _stopRequested;
        private int _processingStarted;
        private int _completionStarted;
        private int _limitStopQueued;
        private int _errorCompletionQueued;
        private int _waveFinalized;
        private int _disposed;
        private SingleUtteranceRecognitionOptions? _singleUtteranceOptions;
        private long _singleUtteranceBytes;

        /// <summary>
        /// Bytes recibidos desde que arrancó la captura. Si esto queda en cero, el dispositivo no
        /// está entregando audio: el VAD nunca ve nada y ningún timeout basado en audio dispara.
        /// </summary>
        public long SingleUtteranceBytes => Interlocked.Read(ref _singleUtteranceBytes);
        private bool _voiceDetected;
        private TimeSpan _voiceEnergy;
        private TimeSpan _trailingSilence;
        private short _lastSample;

        /// <summary>
        /// Cuánta energía sostenida hace falta para dar por empezada la voz.
        /// </summary>
        /// <remarks>
        /// Eran 240 ms y hacían falta seguidos. Un «sí», un «dale» o un «listo» tienen unos 200 ms
        /// de núcleo vocálico: no llegaban nunca. Con 150 ms y decaimiento en vez de reinicio, la
        /// palabra corta entra y el portazo —dos buffers y se apaga— sigue afuera.
        /// </remarks>
        private static readonly TimeSpan RequiredVoiceEnergy = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// Piso de ruido del cuarto, heredado de la captura anterior.
        /// </summary>
        /// <remarks>
        /// Era un campo de instancia y se crea una sesión por captura, así que arrancaba en cero en
        /// cada turno: el piso se aprendía de nuevo desde el silencio absoluto mientras el cuarto ya
        /// sonaba. Con la tele puesta, el umbral quedaba por debajo del ruido, la voz se daba por
        /// detectada de entrada y se grababa el cuarto entero. Justo el caso que el piso existe para
        /// resolver.
        /// <para>
        /// Estático porque el cuarto es uno solo y no cambia entre capturas. Lo que se aprendió del
        /// ambiente en el turno anterior sigue siendo cierto en el siguiente.
        /// </para>
        /// </remarks>
        private static double _sharedNoiseFloor;

        private double _noiseFloor = Volatile.Read(ref _sharedNoiseFloor);

        /// <summary>Puente hacia el evento público; la sesión no conoce al proveedor.</summary>
        public Action<double, bool>? LevelObserver { get; set; }

        public CaptureSession(WaveInEvent capture, long maximumAudioBytes)
        {
            Capture = capture;
            _maximumAudioBytes = maximumAudioBytes;
            Audio = new MemoryStream();
            _nonClosingAudio = new IgnoreDisposeStream(Audio);
            _writer = new WaveFileWriter(_nonClosingAudio, capture.WaveFormat);
        }

        public WaveInEvent Capture { get; }

        public MemoryStream Audio { get; }

        public CancellationTokenSource ProcessingCancellation { get; } = new();

        public TaskCompletionSource<StoppedEventArgs> Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SpeechRecognitionResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<SingleUtteranceStopReason> AutoStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventHandler<WaveInEventArgs>? DataHandler { get; set; }

        public EventHandler<StoppedEventArgs>? StoppedHandler { get; set; }

        public bool LimitReached { get; private set; }

        public bool IsActive => Volatile.Read(ref _completionStarted) == 0;

        public TimeSpan AudioDuration
        {
            get
            {
                var audioBytes = Math.Max(0, Audio.Length - 44);
                return TimeSpan.FromSeconds((double)audioBytes / Capture.WaveFormat.AverageBytesPerSecond);
            }
        }

        public bool WriteAudio(byte[] buffer, int bytesRecorded)
        {
            lock (_audioSync)
            {
                if (!IsActive || Volatile.Read(ref _waveFinalized) != 0)
                {
                    return false;
                }

                var currentAudioBytes = Math.Max(0, Audio.Length - 44);
                var remaining = Math.Max(0, _maximumAudioBytes - currentAudioBytes);
                var toWrite = (int)Math.Min(bytesRecorded, remaining);
                toWrite -= toWrite % Capture.WaveFormat.BlockAlign;
                if (toWrite > 0)
                {
                    _writer.Write(buffer, 0, toWrite);
                    AnalyzeVoiceActivityUnsafe(buffer, toWrite);
                }

                LimitReached = toWrite < bytesRecorded || currentAudioBytes + toWrite >= _maximumAudioBytes;
                return LimitReached;
            }
        }

        public void FinalizeWaveFile()
        {
            lock (_audioSync)
            {
                if (Interlocked.Exchange(ref _waveFinalized, 1) != 0)
                {
                    return;
                }

                _writer.Dispose();
                Audio.Position = 0;
            }
        }

        public void ConfigureSingleUtterance(SingleUtteranceRecognitionOptions options)
        {
            lock (_audioSync)
            {
                _singleUtteranceOptions = options;
            }
        }

        public bool TryRequestStop() => Interlocked.Exchange(ref _stopRequested, 1) == 0;

        public bool TryBeginProcessing() => Interlocked.Exchange(ref _processingStarted, 1) == 0;

        public bool TryComplete() => Interlocked.Exchange(ref _completionStarted, 1) == 0;

        public bool TryQueueLimitStop() => Interlocked.Exchange(ref _limitStopQueued, 1) == 0;

        public bool TryQueueErrorCompletion() => Interlocked.Exchange(ref _errorCompletionQueued, 1) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (DataHandler is not null)
            {
                Capture.DataAvailable -= DataHandler;
            }

            if (StoppedHandler is not null)
            {
                Capture.RecordingStopped -= StoppedHandler;
            }

            lock (_audioSync)
            {
                if (Volatile.Read(ref _waveFinalized) == 0)
                {
                    _writer.Dispose();
                }
            }

            ProcessingCancellation.Dispose();
            Capture.Dispose();
            _nonClosingAudio.Dispose();
            Audio.Dispose();
        }

        private void AnalyzeVoiceActivityUnsafe(byte[] buffer, int bytesRecorded)
        {
            var options = _singleUtteranceOptions;
            if (options is null || AutoStop.Task.IsCompleted || bytesRecorded < sizeof(short))
            {
                return;
            }

            double squareSum = 0;
            var samples = bytesRecorded / sizeof(short);
            var zeroCrossings = 0;
            var previousSample = _lastSample;
            for (var offset = 0; offset + sizeof(short) <= bytesRecorded; offset += sizeof(short))
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, sizeof(short)));
                var normalized = sample / 32768d;
                squareSum += normalized * normalized;

                // Cruce por cero con banda muerta: sin ella, el ruido que ronda el cero cuenta miles
                // de cruces falsos y el número deja de decir nada sobre el contenido.
                if ((sample > 256 && previousSample < -256) || (sample < -256 && previousSample > 256))
                {
                    zeroCrossings++;
                }

                if (Math.Abs(sample) > 256)
                {
                    previousSample = sample;
                }
            }

            _lastSample = previousSample;
            var rootMeanSquare = Math.Sqrt(squareSum / samples);
            var zeroCrossingRate = samples > 0 ? (double)zeroCrossings / samples : 0;

            var bufferDuration = TimeSpan.FromSeconds((double)bytesRecorded / Capture.WaveFormat.AverageBytesPerSecond);
            _singleUtteranceBytes += bytesRecorded;
            var totalDuration = TimeSpan.FromSeconds(
                (double)_singleUtteranceBytes / Capture.WaveFormat.AverageBytesPerSecond);

            // El piso de ruido se sigue de forma continua, no se mide en una ventana inicial. Medirlo
            // en los primeros 300 ms daba por sentado que arrancás en silencio; en una conversación
            // arrancás hablando, así que el piso quedaba medido sobre tu propia voz y el umbral
            // pasaba a ser ocho veces tu voz. A partir de ahí no te detectaba nunca más.
            //
            // Baja rápido y sube lentísimo: el piso es el nivel más bajo reciente, así que se pega a
            // los silencios entre palabras y tarda en dejarse arrastrar por lo que suena.
            var threshold = NoiseFloorTracker.ThresholdFor(_noiseFloor);
            _noiseFloor = NoiseFloorTracker.Advance(_noiseFloor, rootMeanSquare, _voiceDetected);

            // Se deja aprendido para la próxima captura: si no, cada turno vuelve a empezar de cero.
            Volatile.Write(ref _sharedNoiseFloor, _noiseFloor);

            // El nivel para la interfaz se publica siempre, aunque no llegue al umbral: hay que poder
            // ver que entra algo por el micrófono antes de que sea voz sostenida. Se mide contra el
            // umbral —que ya lleva el piso de ruido adentro— para que reaccione igual con cualquier
            // micrófono. La raíz comprime el rango: entre un susurro y un grito hay dos órdenes de
            // magnitud, y en lineal la forma se queda pegada al máximo apenas abrís la boca.
            LevelObserver?.Invoke(
                Math.Clamp(Math.Sqrt(rootMeanSquare / (threshold * 20)), 0, 1),
                _voiceDetected);

            // La energía sola no distingue una voz de un portazo: los dos son fuertes. Lo que separa
            // a la voz es *cómo* está hecha. La tasa de cruces por cero mide eso casi gratis:
            //
            //   golpe, puerta, bajo de la música → casi sin cruces (energía en frecuencias graves)
            //   voz                              → banda media, la fundamental está entre 85 y 300 Hz
            //   siseo, teclado, estática         → cruces por todos lados (contenido de banda ancha)
            //
            // A 16 kHz, la banda de la voz cae aproximadamente entre 0,005 y 0,25. Fuera de ahí es
            // ruido, por fuerte que suene, y no tiene por qué abrir una captura.
            var soundsLikeVoice = zeroCrossingRate is >= 0.005 and <= 0.25;

            // Un golpe en el escritorio o una puerta superan el umbral por un instante. La voz no:
            // se sostiene. Esa diferencia es la que separa una de otra sin traer un detector
            // entrenado, y es lo que evita que el ruido abra una captura.
            if (rootMeanSquare >= threshold && soundsLikeVoice)
            {
                _voiceEnergy += bufferDuration;
                if (_voiceEnergy >= RequiredVoiceEnergy)
                {
                    _voiceDetected = true;
                    _trailingSilence = TimeSpan.Zero;
                }
            }
            else if (_voiceDetected)
            {
                // Un bache corto no reinicia nada: adentro de una frase hay micro-silencios.
                _trailingSilence += bufferDuration;
            }
            else
            {
                // Antes esto ponía la cuenta en cero, así que hacían falta 240 ms *seguidos* por
                // encima del umbral. Un «sí», un «dale» o un «listo» tienen unos 200 ms de núcleo
                // vocálico y jamás llegaban: se agotaba el silencio inicial y la captura devolvía
                // texto vacío sin siquiera transcribir lo que había grabado.
                //
                // Decaer en vez de reiniciar conserva lo que distingue a la voz del golpe. Un
                // portazo son dos buffers y vuelve a cero enseguida; una palabra corta son cinco o
                // seis seguidos y cruza el piso igual.
                _voiceEnergy = _voiceEnergy > bufferDuration
                    ? _voiceEnergy - bufferDuration
                    : TimeSpan.Zero;
            }

            if (!_voiceDetected && totalDuration >= options.InitialSilenceTimeout)
            {
                AutoStop.TrySetResult(SingleUtteranceStopReason.InitialSilence);
            }
            else if (_voiceDetected && _trailingSilence >= options.EndSilenceTimeout)
            {
                AutoStop.TrySetResult(SingleUtteranceStopReason.EndSilence);
            }
            else if (totalDuration >= options.MaximumDuration)
            {
                AutoStop.TrySetResult(SingleUtteranceStopReason.MaximumDuration);
            }
        }
    }

    /// <summary>
    /// Completa sólo si pasado el plazo no llegó ni un byte. Si llegó audio, nunca completa y deja
    /// que decidan el VAD o los timeouts normales.
    /// </summary>
    private static async Task WatchForSilentDeviceAsync(CaptureSession session, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            if (session.SingleUtteranceBytes > 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private enum SingleUtteranceStopReason
    {
        InitialSilence = 0,
        EndSilence,
        MaximumDuration
    }
}
