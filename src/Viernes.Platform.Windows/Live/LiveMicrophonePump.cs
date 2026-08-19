using System.Threading.Channels;
using NAudio.Wave;
using Viernes.Core.Live;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Live;

/// <summary>
/// El micrófono de la sesión en vivo: toma sin parar y sube todo, también mientras ella habla.
/// </summary>
/// <remarks>
/// Que el micrófono quede abierto durante la respuesta no es un descuido: es lo único que hace que
/// se la pueda interrumpir. El camino de siempre no puede hacerlo por más que se lo apure —ahí,
/// mientras ella habla, no hay nadie escuchando—, y por eso el bucle de conversación de siempre
/// espera a que la voz termine antes de volver a abrir la captura.
/// <para>
/// Es dueño único del dispositivo mientras dura la conversación en vivo. Quien lo arranque tiene
/// que haber apagado antes el oído continuo, igual que hace el bucle de siempre: dos capturas sobre
/// el mismo micrófono es la falla que ya costó una tarde.
/// </para>
/// </remarks>
public sealed class LiveMicrophonePump : IAsyncDisposable
{
    /// <summary>
    /// Cuánto audio junta antes de entregarlo.
    /// </summary>
    /// <remarks>
    /// Los mismos 20 ms con los que la sesión sube los fragmentos
    /// (<see cref="GeminiLiveOptions.DefaultChunkMilliseconds"/>). Juntar más acá no ahorra nada
    /// —abajo se reparte igual— y cada milisegundo que se acumula es un milisegundo que el detector
    /// de voz del servidor todavía no vio: se suma entero a lo que tarda en contestar y a lo que
    /// tarda en darse cuenta de que la interrumpiste.
    /// </remarks>
    private const int BlockMilliseconds = 20;

    /// <summary>
    /// Cuántos bloques aguanta la cola de subida.
    /// </summary>
    /// <remarks>
    /// Un segundo. Cuando se llena se tira el más viejo, no el más nuevo: audio del micrófono de
    /// hace un segundo, mandado ahora, es peor que no mandar nada — llega fuera de lugar y el
    /// servidor lo transcribe igual, encima de lo que la persona está diciendo de verdad.
    /// </remarks>
    private const int QueuedBlocks = 1000 / BlockMilliseconds;

    private static readonly WaveFormat Format = new(
        LiveAudioFormat.InputSampleRate,
        LiveAudioFormat.BitsPerSample,
        LiveAudioFormat.Channels);

    private static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(BlockMilliseconds);

    private readonly LiveVoiceSession _session;
    private readonly IVoiceActivityDetector _detector;
    private readonly bool _ownsDetector;
    private readonly int _deviceNumber;
    private readonly Lock _gate = new();

    private Channel<byte[]>? _queue;
    private WaveInEvent? _capture;
    private CancellationTokenSource? _lifetime;
    private Task? _pump;
    private int _disposed;

    /// <summary>Arma el micrófono sin abrirlo.</summary>
    /// <param name="session">A quién se le entrega el audio.</param>
    /// <param name="detector">
    /// Quién decide si un bloque es voz. De acá sale el momento «pensando», que el servidor no
    /// manda. Si no se pasa ninguno se usa la heurística local, que no necesita nada instalado.
    /// </param>
    /// <param name="deviceNumber">Dispositivo de entrada; cero es el predeterminado de Windows.</param>
    public LiveMicrophonePump(
        LiveVoiceSession session,
        IVoiceActivityDetector? detector = null,
        int deviceNumber = 0)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsDetector = detector is null;
        _detector = detector ?? BuildDetector();
        _deviceNumber = deviceNumber;
    }

    /// <summary>
    /// El detector entrenado si está el modelo, y la heurística si no.
    /// </summary>
    /// <remarks>
    /// El mismo criterio que el oído continuo, y por el mismo motivo: el modelo ya está descargado y
    /// medido contra este micrófono, así que usar la heurística cuando el modelo está sería tirar
    /// ese trabajo. Que falte el modelo no puede impedir la sesión en vivo — acá el detector sólo
    /// decide cuándo dibujar «pensando», no si el audio sube.
    /// </remarks>
    private static IVoiceActivityDetector BuildDetector()
    {
        var runner = OnnxVadModelRunner.TryCreate(modelPath: null, out _);
        return runner is null
            ? new HeuristicVoiceActivityDetector()
            : new SileroVoiceActivityDetector(runner);
    }

    /// <summary>Quién está decidiendo si lo que entra es voz. Para poder decirlo en un informe.</summary>
    public VoiceActivityDetectorInfo DetectorInfo => _detector.Info;

    /// <summary>
    /// Nivel del micrófono, bloque a bloque, para que el orbe se mueva con la voz.
    /// </summary>
    /// <remarks>
    /// Es el mismo evento que publica la captura de siempre y por la misma razón: sin él, en la
    /// sesión en vivo el orbe dibuja «te escucho» completamente quieto, y quieto no se distingue de
    /// colgado. Va aparte del estado porque llega decenas de veces por segundo: mueve la forma, no
    /// reescribe la burbuja.
    /// </remarks>
    public event EventHandler<Speech.AudioLevelEventArgs>? LevelChanged;

    /// <summary>Si el micrófono está tomando.</summary>
    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _capture is not null;
            }
        }
    }

    /// <summary>Bloques que se tiraron por atraso de la subida. Si crece, la red no da abasto.</summary>
    public long DroppedBlocks { get; private set; }

    /// <summary>Por qué no pudo abrir el micrófono, si no pudo.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Abre el micrófono y empieza a subir.
    /// </summary>
    /// <remarks>
    /// Devuelve <c>false</c> en vez de lanzar: quien llama tiene que poder cerrar la sesión en vivo
    /// y seguir por el camino de siempre sin envolver esto en un <c>try</c>.
    /// </remarks>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            if (_capture is not null)
            {
                return true;
            }

            try
            {
                _detector.Reset();

                var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueuedBlocks)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true
                });

                var capture = new WaveInEvent
                {
                    DeviceNumber = _deviceNumber,
                    BufferMilliseconds = BlockMilliseconds,
                    WaveFormat = Format
                };
                capture.DataAvailable += OnDataAvailable;

                var lifetime = new CancellationTokenSource();

                _queue = queue;
                _capture = capture;
                _lifetime = lifetime;

                capture.StartRecording();

                // El token se saca acá y no adentro del lambda: adentro se leería el campo en el
                // momento en que la tarea arranca, y para entonces el cierre ya puede haberlo puesto
                // en null. Es la misma trampa que ya se pagó en el bucle de lectura del cliente.
                var token = lifetime.Token;
                _pump = Task.Run(() => PumpAsync(queue.Reader, token), CancellationToken.None);

                LastFailure = null;
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LastFailure = $"No pude abrir el micrófono para la sesión en vivo ({exception.GetType().Name}).";
                var (capture, lifetime) = TakeDeviceLocked();
                CloseDevice(capture);
                lifetime?.Dispose();
                return false;
            }
        }
    }

    /// <summary>Cierra el micrófono y deja de subir.</summary>
    public async Task StopAsync()
    {
        Task? pump;
        CancellationTokenSource? lifetime;
        WaveInEvent? capture;

        lock (_gate)
        {
            pump = _pump;
            _pump = null;
            (capture, lifetime) = TakeDeviceLocked();
        }

        // El dispositivo se cierra <b>fuera</b> del candado. Adentro sería un abrazo mortal: cerrar
        // espera a que termine la devolución de llamada que está en vuelo, y esa devolución toma
        // este mismo candado para dejar su bloque en la cola. Es el mismo orden que usa el oído
        // continuo, y por la misma razón.
        CloseDevice(capture);

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelar es la forma normal de terminar este bucle.
            }
        }

        lifetime?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        if (_ownsDetector)
        {
            _detector.Dispose();
        }
    }

    /// <summary>
    /// Llega un bloque del driver.
    /// </summary>
    /// <remarks>
    /// Corre en el hilo del dispositivo y no puede tardar: lo único que hace es opinar sobre el
    /// bloque —microsegundos— y dejarlo en la cola. Subir es trabajo de red y se hace en otra tarea;
    /// hacerlo acá trabaría la captura en cada hipo de la conexión y el audio se perdería de verdad.
    /// <para>
    /// No es <c>async void</c> a propósito, que es lo que uno escribiría para poder esperar el
    /// envío: una excepción adentro de un <c>async void</c> no tiene a dónde ir y se lleva puesto
    /// el proceso. Ya pasó una vez en este repositorio.
    /// </para>
    /// </remarks>
    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        ChannelWriter<byte[]>? writer;
        lock (_gate)
        {
            writer = _queue?.Writer;
        }

        if (writer is null)
        {
            return;
        }

        var block = eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded);

        try
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(block);

            // «Adentro de una frase» sirve para que el detector no aprenda ruido de fondo de algo que
            // no es fondo. Cuentan las dos voces: mientras habla la persona, y también mientras habla
            // ella — su propia voz vuelve por los parlantes y tomarla como ambiente subiría el piso
            // de ruido del cuarto en cada respuesta, hasta dejar de oír a quien habla bajo.
            var ocupado = _session.IsUserSpeaking || _session.TurnState != LiveTurnState.Idle;
            var decision = _detector.Analyze(samples, ocupado);
            _session.NoteUserAudio(decision.IsVoice, BlockDuration);
            LevelChanged?.Invoke(this, new Speech.AudioLevelEventArgs(decision.Level, decision.IsVoice));
        }
        catch (Exception)
        {
            // Que el detector se caiga no puede cortar la subida: sin él se pierde el momento
            // «pensando», que es un dibujo, y no la conversación, que es lo que importa.
        }

        // El búfer del driver se reutiliza en cuanto vuelve este método, así que se copia.
        if (!writer.TryWrite(block.ToArray()))
        {
            DroppedBlocks++;
        }
    }

    private async Task PumpAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var block in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _session.PushMicrophoneAsync(block, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cerrar la conversación cancela esto; no es un error.
        }
        catch (Exception)
        {
            // La sesión ya anota sus propias fallas y se cae sola al camino de siempre. Lo único que
            // no puede pasar es que esta tarea muera con una excepción sin atender en el pool.
        }
    }

    /// <summary>
    /// Suelta los campos del dispositivo bajo el candado y devuelve lo que hay que cerrar afuera.
    /// </summary>
    private (WaveInEvent? Capture, CancellationTokenSource? Lifetime) TakeDeviceLocked()
    {
        var capture = _capture;
        var lifetime = _lifetime;

        _capture = null;
        _lifetime = null;

        // Cerrar el caño despierta al bombeador aunque la cancelación llegue después.
        _queue?.Writer.TryComplete();
        _queue = null;

        return (capture, lifetime);
    }

    private void CloseDevice(WaveInEvent? capture)
    {
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnDataAvailable;

        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // Detener algo que ya se detuvo no es un error.
        }

        capture.Dispose();
    }
}
