using System.Threading.Channels;
using NAudio.Wave;
using Viernes.Core.Live;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Live;

/// <summary>
/// El micrófono de la sesión en vivo: toma sin parar, también mientras ella habla, y sube todo menos
/// su propio eco.
/// </summary>
/// <remarks>
/// Que el micrófono quede abierto durante la respuesta no es un descuido: es lo único que hace que
/// se la pueda interrumpir. El camino de siempre no puede hacerlo por más que se lo apure —ahí,
/// mientras ella habla, no hay nadie escuchando—, y por eso el bucle de conversación de siempre
/// espera a que la voz termine antes de volver a abrir la captura.
/// <para>
/// Lo que <b>no</b> puede pasar es que ese micrófono abierto le suba a Gemini su propia voz saliendo
/// por los parlantes: el detector de voz del servidor la toma por la persona, manda
/// <c>interrupted</c>, y la conversación entra en un bucle de cortarse sola. Eso lo decide
/// <see cref="LiveEchoGate"/>, bloque por bloque, y acá sólo se ejecuta lo que decidió.
/// </para>
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

    /// <summary>
    /// Cuánto audio frenado se guarda para poder devolverlo si resulta ser una persona.
    /// </summary>
    /// <remarks>
    /// La compuerta necesita ochenta milisegundos de voz por encima del listón para creer que
    /// alguien está hablando encima, y esos ochenta milisegundos ya pasaron cuando lo cree: son la
    /// primera sílaba. Sin esta antesala la interrupción le llegaría al servidor empezada por la
    /// mitad —«…ará» en vez de «pará»—, y una palabra cortada no es sólo fea: es lo que hace que el
    /// detector del servidor dude de si eso fue voz.
    /// <para>
    /// Trescientos milisegundos son de sobra para los ochenta que hacen falta, y aun así son quince
    /// bloques de 640 bytes: nueve kilobytes. El costo de tenerla de más es cero y el de tenerla de
    /// menos es perder el arranque de cada interrupción.
    /// </para>
    /// </remarks>
    private const int PreRollBlocks = 300 / BlockMilliseconds;

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
    private readonly LiveEchoGate _echo = new();

    /// <summary>
    /// Los bloques frenados que todavía podrían resultar ser de una persona.
    /// </summary>
    /// <remarks>
    /// Sin candado a propósito: la toca <see cref="OnDataAvailable"/>, que corre en el hilo del
    /// dispositivo y en ninguno más, y <see cref="Start"/>, que la vacía con la captura todavía
    /// cerrada. Ponerle un candado sería tomar uno por bloque en el hilo del audio para proteger algo
    /// que nadie más mira.
    /// </remarks>
    private readonly Queue<byte[]> _preRoll = new(PreRollBlocks);

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

    /// <summary>
    /// Se soltó la antesala: alguien le habló encima y sube de golpe lo que estaba frenado.
    /// </summary>
    /// <remarks>
    /// Es la única forma de ver <em>cuándo</em> pasa esto. El contador
    /// <see cref="Breakthroughs"/> se escribe al cerrar la charla y dice cuántas veces, que no
    /// alcanza para cruzarlo con un corte de voz. Y hace falta cruzarlo: la sospecha de que esta
    /// ráfaga de trescientos milisegundos entrando de golpe es lo que el servidor lee como una
    /// interrupción sólo se puede confirmar o tirar abajo mirando si el corte cae al lado de esta
    /// marca.
    /// <para>
    /// Sale del hilo del dispositivo, así que quien escuche no puede tardar.
    /// </para>
    /// </remarks>
    public event EventHandler<LivePreRollReleasedEventArgs>? PreRollReleased;

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

    /// <summary>
    /// Bloques que no se subieron por ser su propia voz volviendo por el micrófono.
    /// </summary>
    /// <remarks>
    /// Se cuentan recién cuando se tiran, así que los que resultaron ser de una persona y se
    /// soltaron no están acá. Este número por veinte milisegundos es cuánto eco se le ahorró al
    /// servidor, y es lo que hay que mirar en la bitácora para saber si la compuerta está haciendo
    /// algo o está de adorno.
    /// </remarks>
    public long EchoBlocks { get; private set; }

    /// <summary>Cuántas veces alguien le habló encima y la compuerta lo dejó pasar.</summary>
    public int Breakthroughs => _echo.Breakthroughs;

    /// <summary>Cuánto eco tiene medido la compuerta, y el listón que salió de esa medición.</summary>
    public (double Echo, double Bar) EchoProfile => (_echo.EchoLevel, _echo.Bar);

    /// <summary>Si la compuerta tuvo que rendirse porque el parlante nunca dejó de sonar.</summary>
    /// <remarks>
    /// Va a la bitácora al cerrar la charla. Una compuerta rendida no se nota desde afuera —todo
    /// sigue andando y sólo vuelve el eco— así que sin esto nadie sabría que el problema estuvo en la
    /// salida de audio.
    /// </remarks>
    public bool EchoGaveUp => _echo.GaveUp;

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

                // La compuerta vuelve a arrancar sin medición, y la antesala se vacía: lo que quedó
                // frenado pertenece a una conversación que ya terminó. Se hace acá, con la captura
                // todavía cerrada, que es el único momento en que nadie más las está tocando.
                _echo.Reset();
                _preRoll.Clear();

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

        // Si el detector se cae no hay veredicto, y sin veredicto la compuerta no tiene con qué
        // decidir: se sube, que es lo que hacía siempre. Se pierde la protección contra el eco
        // mientras dure la falla, y eso es preferible a un micrófono que se cierra sin que nadie
        // pueda saber por qué.
        var verdict = LiveMicrophoneVerdict.Send;

        try
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(block);

            // «Adentro de una frase» sirve para que el detector no aprenda ruido de fondo de algo que
            // no es fondo. Cuentan las dos voces: mientras habla la persona, y también mientras habla
            // ella — su propia voz vuelve por los parlantes y tomarla como ambiente subiría el piso
            // de ruido del cuarto en cada respuesta, hasta dejar de oír a quien habla bajo.
            var ocupado = _session.IsUserSpeaking || _session.TurnState != LiveTurnState.Idle;
            var decision = _detector.Analyze(samples, ocupado);

            // Se le pregunta por el parlante y no por el turno: el servidor despacha la respuesta
            // más rápido que tiempo real, así que el turno cierra con segundos de voz todavía por
            // sonar, y esos segundos son los que siguen entrando por el micrófono.
            verdict = _echo.Decide(
                _session.IsSpeakerBusy,
                decision.IsVoice,
                decision.Level,
                BlockDuration);

            _session.NoteUserAudio(decision.IsVoice, BlockDuration);
            LevelChanged?.Invoke(this, new Speech.AudioLevelEventArgs(decision.Level, decision.IsVoice));
        }
        catch (Exception)
        {
            // Que el detector se caiga no puede cortar la subida: sin él se pierde el momento
            // «pensando», que es un dibujo, y no la conversación, que es lo que importa.
        }

        // El búfer del driver se reutiliza en cuanto vuelve este método, así que se copia siempre,
        // vaya a la cola de subida o a la antesala.
        switch (verdict)
        {
            case LiveMicrophoneVerdict.Hold:
                Hold(block.ToArray());
                return;

            case LiveMicrophoneVerdict.Release:
                ReleaseHeld(writer);
                break;

            default:
                DiscardHeld();
                break;
        }

        Upload(writer, block.ToArray());
    }

    /// <summary>Guarda un bloque frenado, tirando el más viejo si la antesala se llenó.</summary>
    private void Hold(byte[] block)
    {
        while (_preRoll.Count >= PreRollBlocks)
        {
            _preRoll.Dequeue();
            EchoBlocks++;
        }

        _preRoll.Enqueue(block);
    }

    /// <summary>
    /// Sube lo que estaba frenado: no era eco, era alguien hablándole encima.
    /// </summary>
    /// <remarks>
    /// Van en orden y antes que el bloque actual. El servidor no sabe que hubo una pausa —lo que
    /// recibe es audio seguido— así que la frase le llega entera desde su primera sílaba, que es lo
    /// único que hace que la reconozca como una interrupción y no como un ruido.
    /// </remarks>
    private void ReleaseHeld(ChannelWriter<byte[]> writer)
    {
        var soltados = _preRoll.Count;

        while (_preRoll.Count > 0)
        {
            Upload(writer, _preRoll.Dequeue());
        }

        if (soltados == 0)
        {
            return;
        }

        try
        {
            PreRollReleased?.Invoke(
                this,
                new LivePreRollReleasedEventArgs(soltados, BlockDuration * soltados));
        }
        catch (Exception)
        {
            // Un oyente roto no puede cortar la captura.
        }
    }

    /// <summary>Tira lo frenado: era eco y nadie habló encima.</summary>
    private void DiscardHeld()
    {
        EchoBlocks += _preRoll.Count;
        _preRoll.Clear();
    }

    private void Upload(ChannelWriter<byte[]> writer, byte[] block)
    {
        if (!writer.TryWrite(block))
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

/// <summary>Se soltó la antesala del micrófono porque alguien le habló encima.</summary>
/// <remarks>
/// Lleva cuánto se soltó porque eso es lo que importa del asunto: son los milisegundos que le entran
/// al servidor de una sola vez, y la duda es justamente si esa ráfaga se lee como una interrupción.
/// </remarks>
public sealed class LivePreRollReleasedEventArgs(int blocks, TimeSpan duration) : EventArgs
{
    /// <summary>Cuántos bloques frenados se soltaron.</summary>
    public int Blocks { get; } = blocks;

    /// <summary>Cuánto audio suman.</summary>
    public TimeSpan Duration { get; } = duration;
}
