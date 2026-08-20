using System.Net.WebSockets;

namespace Viernes.Core.Live;

/// <summary>
/// La sesión hablada con Gemini Live: audio para arriba, voz para abajo, y se la puede cortar.
/// </summary>
/// <remarks>
/// Lo que cambia respecto del camino de siempre —grabar, reconocer, pensar, sintetizar, reproducir—
/// no es sólo que tarda menos. Es que mientras ella habla el micrófono sigue abierto y el servidor
/// escucha, así que hablarle encima la calla. Eso no se puede hacer con el otro camino por más que
/// se lo apure: ahí no hay nadie escuchando mientras habla.
/// <para>
/// Con una salvedad que costó una sesión entera de cortarse sola: el servidor escucha, pero no sabe
/// distinguir la voz de la persona de la voz de ella volviendo por los parlantes, y le manda
/// <c>interrupted</c> a las dos. Quién puede subir cada bloque se decide antes de llegar acá, en
/// <see cref="LiveEchoGate"/>.
/// </para>
/// <para>
/// Esta clase no toca el micrófono ni los parlantes. Recibe audio por <see cref="SendAudioAsync"/> y
/// entrega audio por <see cref="ILiveAudioSink"/>. De ese modo el proyecto de Windows es dueño de
/// sus dispositivos y esto se puede probar entero sin hardware y sin red.
/// </para>
/// <para>
/// <b>Reconectar es parte del funcionamiento, no un caso de error.</b> La conexión dura unos diez
/// minutos y la sesión de audio quince; antes de cerrar llega un <c>goAway</c> con cuánto falta. Con
/// <c>sessionResumption</c> la reconexión conserva la conversación, así que la charla sigue y el
/// usuario no se entera.
/// </para>
/// </remarks>
public sealed class GeminiLiveClient : IAsyncDisposable
{
    private const int MaximumConsecutiveReconnects = 5;

    /// <summary>
    /// Cómo se abre la sesión. No es de sólo lectura porque la instrucción se refresca antes de cada
    /// conexión: ver <see cref="UseSystemInstruction"/>.
    /// </summary>
    private GeminiLiveOptions _options;
    private readonly Func<string?> _apiKey;
    private readonly ILiveAudioSink _sink;
    private readonly Func<ILiveTransport> _transportFactory;
    private readonly LiveTurnMachine _turns = new();
    private readonly SemaphoreSlim _audioGate = new(1, 1);
    private readonly List<byte> _pendingAudio = [];

    /// <summary>Quién ejecuta lo que el servidor pide. Sin esto, la sesión conversa y nada más.</summary>
    private readonly ILiveToolBridge? _tools;

    /// <summary>
    /// Las llamadas que el servidor canceló mientras corrían.
    /// </summary>
    /// <remarks>
    /// Se anotan del lado del que lee y se consultan del lado del que ejecuta, que son dos hilos
    /// distintos: por eso el candado. No crece sin límite porque cada id se saca al mirarlo, y las
    /// que sobren se van con la sesión.
    /// </remarks>
    private readonly HashSet<string> _cancelledToolCalls = new(StringComparer.Ordinal);
    private readonly Lock _toolGate = new();

    private ILiveTransport? _transport;
    private CancellationTokenSource? _lifetime;
    private Task? _readLoop;
    private string? _resumptionHandle;
    private volatile bool _connected;
    private volatile bool _reconnectWhenIdle;
    private int _disposed;

    /// <summary>Arma el cliente.</summary>
    /// <param name="options">Cómo se abre la sesión y cómo se comporta.</param>
    /// <param name="apiKey">
    /// De dónde sale la clave de Google. Se lee en cada conexión y no se guarda: si el usuario acaba
    /// de pegar una clave nueva, la reconexión la toma sin reiniciar nada.
    /// </param>
    /// <param name="audioSink">Dónde sale la voz. Es quien tiene que poder vaciarse de golpe.</param>
    /// <param name="transportFactory">
    /// Con qué se conecta. Se deja abierto para poder probar el comportamiento sin red; en
    /// producción es <see cref="WebSocketLiveTransport"/>.
    /// </param>
    /// <param name="tools">
    /// Las manos de la sesión. Sin esto no se declara ninguna herramienta y la sesión conversa y
    /// nada más, que es como nació este cliente.
    /// </param>
    public GeminiLiveClient(
        GeminiLiveOptions options,
        Func<string?> apiKey,
        ILiveAudioSink audioSink,
        Func<ILiveTransport>? transportFactory = null,
        ILiveToolBridge? tools = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _sink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _transportFactory = transportFactory ?? (() => new WebSocketLiveTransport());
        _tools = tools;
    }

    /// <summary>Hay clave y la sesión en vivo está encendida por configuración.</summary>
    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>
    /// Cambia la instrucción de sistema para la próxima conexión.
    /// </summary>
    /// <remarks>
    /// Existe porque la instrucción no puede ser fija. El camino de siempre le arma al modelo, en
    /// cada turno, lo que sabe del usuario y las misiones abiertas con su pregunta sin contestar; la
    /// sesión hablada nacía con un texto escrito a mano y nada más, así que hablando era amnésica:
    /// no sabía quién era el usuario ni que le había dejado una pregunta ayer.
    /// <para>
    /// Toma efecto en la próxima conexión, no en la que esté abierta: el <c>setup</c> se manda una
    /// sola vez al abrir. Como cada conversación abre y cierra, eso alcanza — y es lo correcto,
    /// porque cambiarle la instrucción a una charla en curso la haría contestar distinto a mitad de
    /// frase.
    /// </para>
    /// </remarks>
    public void UseSystemInstruction(string? instruction) =>
        _options = _options.WithSystemInstruction(instruction);

    /// <summary>La sesión está abierta y ya pasó el <c>setupComplete</c>.</summary>
    public bool IsConnected => _connected;

    /// <summary>En qué anda el turno.</summary>
    public LiveTurnState TurnState => _turns.State;

    /// <summary>Cuántas veces la cortaron.</summary>
    public int InterruptionCount => _turns.InterruptionCount;

    /// <summary>Lo último que salió mal, para poder decirlo. Nunca incluye la clave.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Cuánto va costando.</summary>
    public LiveCostMeter Cost { get; } = new();

    /// <summary>Cambió el estado del turno.</summary>
    public event EventHandler<LiveTurnStateChangedEventArgs>? TurnStateChanged;

    /// <summary>Llegó texto transcripto, de uno u otro lado.</summary>
    public event EventHandler<LiveTranscriptEventArgs>? TranscriptReceived;

    /// <summary>La cortaron hablándole encima. La cola ya se vació cuando esto se dispara.</summary>
    public event EventHandler? Interrupted;

    /// <summary>Algo salió mal.</summary>
    public event EventHandler<LiveFailureEventArgs>? Failed;

    /// <summary>Arrancó o terminó una herramienta pedida por el servidor.</summary>
    public event EventHandler<LiveToolEventArgs>? ToolActivity;

    /// <summary>
    /// Abre la sesión y deja andando el bucle de lectura.
    /// </summary>
    /// <remarks>
    /// Devuelve <c>false</c> en vez de lanzar cuando no se pudo: el anfitrión tiene que poder
    /// intentar esto y seguir con el camino de siempre si no salió, sin envolver la llamada en un
    /// <c>try</c>. El motivo queda en <see cref="LastFailure"/>.
    /// </remarks>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_readLoop is not null)
        {
            return _connected;
        }

        if (!_options.Enabled)
        {
            LastFailure = "La sesión en vivo está apagada por configuración.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_apiKey()))
        {
            LastFailure = "No hay clave de Google configurada.";
            return false;
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!await ConnectAsync(_lifetime.Token).ConfigureAwait(false))
        {
            _lifetime.Dispose();
            _lifetime = null;
            return false;
        }

        // El token se saca acá y no adentro del lambda. Adentro se leería _lifetime en el momento en
        // que la tarea arranca, y StopAsync lo pone en null antes de esperar el bucle: la sesión se
        // cerraba con una NullReferenceException en un hilo del pool en vez de terminar.
        var token = _lifetime.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(token), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Manda audio del micrófono. PCM 16 bits little endian, mono, 16 kHz.
    /// </summary>
    /// <remarks>
    /// Acepta cualquier tamaño y lo reparte en fragmentos del tamaño configurado, guardando el resto
    /// para la próxima llamada. Eso resuelve dos cosas a la vez: que el dispositivo de captura
    /// entregue bloques de un tamaño que no eligió nadie, y que un bloque de cantidad impar de bytes
    /// no desalinee todo lo que viene después —el servidor lee de a dos y no tiene forma de darse
    /// cuenta de que arrancó corrido: no falla, suena a ruido—.
    /// <para>
    /// Devuelve <c>false</c> si la sesión no está lista. No encola para más tarde a propósito: audio
    /// del micrófono de hace tres segundos, mandado ahora, es peor que no mandar nada.
    /// </para>
    /// </remarks>
    public async Task<bool> SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken cancellationToken = default)
    {
        if (!_connected || pcm16k.IsEmpty)
        {
            return false;
        }

        var chunkBytes = _options.ChunkBytes;
        var sent = false;

        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pendingAudio.AddRange(pcm16k.Span);

            while (_pendingAudio.Count >= chunkBytes)
            {
                var chunk = new byte[chunkBytes];
                _pendingAudio.CopyTo(0, chunk, 0, chunkBytes);
                _pendingAudio.RemoveRange(0, chunkBytes);

                if (!await SendRawAsync(LiveClientMessages.BuildAudioChunk(chunk), cancellationToken).ConfigureAwait(false))
                {
                    return sent;
                }

                Cost.AddInput(chunk.Length);
                sent = true;
            }
        }
        finally
        {
            _audioGate.Release();
        }

        return sent;
    }

    /// <summary>
    /// Avisa que se cortó el micrófono.
    /// </summary>
    /// <remarks>
    /// Manda primero lo que haya quedado en el resto. Son unos milisegundos y suelen ser el final de
    /// la última palabra.
    /// </remarks>
    public async Task EndAudioStreamAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            return;
        }

        await _audioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingAudio.Count >= LiveAudioFormat.BytesPerSample)
            {
                var tail = _pendingAudio.Count - (_pendingAudio.Count % LiveAudioFormat.BytesPerSample);
                var chunk = new byte[tail];
                _pendingAudio.CopyTo(0, chunk, 0, tail);
                _pendingAudio.RemoveRange(0, tail);
                if (await SendRawAsync(LiveClientMessages.BuildAudioChunk(chunk), cancellationToken).ConfigureAwait(false))
                {
                    Cost.AddInput(chunk.Length);
                }
            }
        }
        finally
        {
            _audioGate.Release();
        }

        await SendRawAsync(LiveClientMessages.BuildAudioStreamEnd(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Corta la reproducción por decisión de este lado, sin esperar a que el servidor avise.
    /// </summary>
    /// <remarks>
    /// Existe por algo que se vio probando contra el servidor de verdad: el <c>interrupted</c> no
    /// siempre llega. En una de cada tres pruebas se le metieron tres segundos de voz clara mientras
    /// hablaba y el servidor no mandó nada —y tampoco transcribió esa voz, o sea que directamente no
    /// la registró—. Las otras dos veces sí, y ahí funcionó como está documentado.
    /// <para>
    /// El anfitrión de Windows ya tiene su propio detector de voz —el mismo que dispara la palabra
    /// de activación, con el piso de ruido medido en este equipo—, así que puede saber que la persona
    /// arrancó a hablar antes y con más certeza que el servidor. Cuando lo sabe, llama a esto y la
    /// voz se corta igual. Es de un solo sentido: no le dice nada al servidor, sólo calla los
    /// parlantes; el turno lo sigue cerrando el servidor con su <c>turnComplete</c>.
    /// </para>
    /// <para>
    /// <b>Vaciar el parlante no alcanza</b>, y durante un tiempo esto fue sólo eso. El servidor sigue
    /// mandando el audio que ya despachó, el turno seguía en <c>Responding</c> así que ese audio se
    /// encolaba igual, y el parlante volvía a arrancar solo: la voz se cortaba un instante y seguía.
    /// Marcar el turno como interrumpido de este lado es lo que hace que lo que viene en camino se
    /// descarte. Los dos pasos, o no calla.
    /// </para>
    /// </remarks>
    public void SilenceNow()
    {
        // Primero el turno y después la cola. Al revés, el audio que entre entre las dos líneas se
        // encola detrás de lo que se acaba de vaciar y queda sonando justo lo que había que tirar.
        _turns.InterruptLocally();
        _sink.Flush();
    }

    /// <summary>Manda texto escrito y cierra el turno para que conteste.</summary>
    public Task<bool> SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        _connected
            ? SendRawAsync(LiveClientMessages.BuildText(text), cancellationToken)
            : Task.FromResult(false);

    /// <summary>Cierra la sesión y deja de escuchar.</summary>
    public async Task StopAsync()
    {
        var lifetime = _lifetime;
        var loop = _readLoop;
        _readLoop = null;
        _lifetime = null;
        _connected = false;

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelar es la forma normal de terminar este bucle.
            }
        }

        lifetime?.Dispose();

        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        _sink.Flush();
        _turns.Reset();

        lock (_toolGate)
        {
            _cancelledToolCalls.Clear();
        }

        // El handle de reanudación se tira acá y sólo acá.
        //
        // Sirve para que un corte de transporte —el goAway del servidor— no le haga perder el hilo
        // a la persona: se reconecta y sigue la misma charla. Pero ese camino entra derecho por
        // ConnectAsync y nunca pasa por acá, así que llegar a StopAsync significa que la charla se
        // dio por terminada de verdad. Guardarlo igual hacía que la charla SIGUIENTE mandara el
        // handle de la anterior y el servidor continuara aquel historial: preguntabas algo nuevo y
        // contestaba arrastrando lo de antes, con los turnos locales ya destilados y vaciados. Y un
        // handle vencido es además una forma más de que el setup rebote y se caiga al camino viejo.
        _resumptionHandle = null;
        _reconnectWhenIdle = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _audioGate.Dispose();
    }

    /// <summary>
    /// Conecta, manda el setup y espera el <c>setupComplete</c>.
    /// </summary>
    /// <remarks>
    /// Esperar el <c>setupComplete</c> no es una formalidad: cualquier cosa mandada antes se pierde
    /// o cierra la conexión, y como el que la manda es el micrófono, lo que se pierde son las
    /// primeras palabras de la frase que la persona ya empezó a decir.
    /// </remarks>
    private async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            LastFailure = "No hay clave de Google configurada.";
            return false;
        }

        var transport = _transportFactory();

        try
        {
            await transport.ConnectAsync(LiveEndpoint.Build(key), cancellationToken).ConfigureAwait(false);

            // Las herramientas se preguntan en cada conexión y no una sola vez al armar el cliente:
            // el setup se manda una vez por conexión y es el único momento en que se pueden declarar,
            // así que lo que haya cambiado entre una charla y otra entra acá o no entra nunca.
            await transport
                .SendAsync(
                    LiveClientMessages.BuildSetup(_options, _resumptionHandle, _tools?.Declarations),
                    cancellationToken)
                .ConfigureAwait(false);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshake.CancelAfter(_options.SetupTimeout);

            while (true)
            {
                var message = await transport.ReceiveAsync(handshake.Token).ConfigureAwait(false);
                if (message is null)
                {
                    LastFailure = "El servidor cerró la sesión antes de aceptarla.";
                    await transport.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                var serverEvent = LiveServerEventParser.Parse(message);
                if (serverEvent.Error is { Length: > 0 } error)
                {
                    LastFailure = error;
                    await transport.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                if (serverEvent.SetupComplete)
                {
                    break;
                }
            }

            var previous = _transport;
            _transport = transport;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _connected = true;
            _reconnectWhenIdle = false;
            LastFailure = null;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastFailure = "El servidor no confirmó la sesión a tiempo.";
            await transport.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException or InvalidOperationException)
        {
            // El mensaje de la excepción no se copia: en esta API la dirección lleva la clave adentro
            // y más de una implementación la escribe en el texto del error.
            LastFailure = $"No pude abrir la sesión en vivo ({exception.GetType().Name}).";
            await transport.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var transport = _transport;
            if (transport is null)
            {
                return;
            }

            string? message;
            try
            {
                message = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException)
            {
                message = null;
            }

            if (message is null)
            {
                _connected = false;

                // La cola se vacía antes de reconectar. Lo que quedaba encolado pertenece a un turno
                // que del otro lado ya no existe, y dejarlo sonar mientras se reconecta es contarle
                // al usuario el final de una respuesta que la sesión nueva no va a recordar.
                _sink.Flush();

                if (++consecutiveFailures > MaximumConsecutiveReconnects)
                {
                    LastFailure = "Se cortó la sesión en vivo y no pude reconectar.";
                    Raise(Failed, new LiveFailureEventArgs(LastFailure, fatal: true));
                    return;
                }

                if (!await WaitBeforeRetryAsync(consecutiveFailures, cancellationToken).ConfigureAwait(false) ||
                    !await ConnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                consecutiveFailures = 0;
                continue;
            }

            consecutiveFailures = 0;
            await HandleAsync(LiveServerEventParser.Parse(message), cancellationToken).ConfigureAwait(false);

            // La bandera la limpia ConnectAsync sólo cuando la reconexión salió bien. Si falla, queda
            // puesta y se vuelve a intentar cuando llegue el próximo mensaje: la conexión vieja
            // todavía sirve hasta que se agote el margen que anunció el goAway.
            if (_reconnectWhenIdle && _turns.State == LiveTurnState.Idle)
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Aplica un mensaje del servidor.
    /// </summary>
    /// <remarks>
    /// El orden es el que importa. La máquina de estados se consulta <b>antes</b> de encolar audio,
    /// porque si un mismo mensaje trae audio y la interrupción juntos, encolar primero deja sonando
    /// justo lo que había que tirar.
    /// <para>
    /// Por la misma razón, mientras el turno está en <see cref="LiveTurnState.Interrupted"/> el audio
    /// que siga llegando se descarta en vez de encolarse. El servidor deja de generar en cuanto
    /// detecta la voz, pero lo que ya salió viaja igual: encolarlo devuelve a los parlantes el mismo
    /// pedazo de respuesta que la persona acaba de mandar a callar.
    /// </para>
    /// </remarks>
    private async Task HandleAsync(LiveServerEvent serverEvent, CancellationToken cancellationToken)
    {
        if (serverEvent.Error is { Length: > 0 } error)
        {
            LastFailure = error;
            Raise(Failed, new LiveFailureEventArgs(error, fatal: false));
        }

        if (serverEvent.ResumptionHandle is { Length: > 0 } handle && serverEvent.ResumptionHandleIsResumable)
        {
            _resumptionHandle = handle;
        }

        var transition = _turns.Apply(serverEvent);

        if (transition.FlushPlayback)
        {
            _sink.Flush();
            Raise(Interrupted, EventArgs.Empty);
        }
        else if (serverEvent.Audio.Count > 0 && _turns.State != LiveTurnState.Interrupted)
        {
            foreach (var block in serverEvent.Audio)
            {
                Cost.AddOutput(block.Length);
                await _sink.EnqueueAsync(block, cancellationToken).ConfigureAwait(false);
            }
        }

        if (serverEvent.InputTranscript is { Length: > 0 } heard)
        {
            Raise(TranscriptReceived, new LiveTranscriptEventArgs(LiveSpeaker.User, heard));
        }

        if (serverEvent.OutputTranscript is { Length: > 0 } said)
        {
            Raise(TranscriptReceived, new LiveTranscriptEventArgs(LiveSpeaker.Assistant, said));
        }

        if (transition.TurnEnded)
        {
            await _sink.CompleteTurnAsync(cancellationToken).ConfigureAwait(false);
        }

        if (transition.Changed)
        {
            Raise(TurnStateChanged, new LiveTurnStateChangedEventArgs(transition.Previous, transition.Current));
        }

        if (serverEvent.CancelledToolCalls.Count > 0)
        {
            lock (_toolGate)
            {
                foreach (var id in serverEvent.CancelledToolCalls)
                {
                    _cancelledToolCalls.Add(id);
                }
            }
        }

        if (serverEvent.FunctionCalls.Count > 0)
        {
            DispatchToolCalls(serverEvent.FunctionCalls, cancellationToken);
        }

        if (serverEvent.GoAwayTimeLeft is not null)
        {
            // No se reconecta acá aunque el aviso llegue en el medio de una frase: cortar para
            // reconectar se oye peor que aprovechar el margen que el propio servidor está dando.
            _reconnectWhenIdle = true;
        }
    }

    /// <summary>
    /// Ejecuta lo que el servidor pidió y le devuelve el resultado.
    /// </summary>
    /// <remarks>
    /// Sale por una tarea aparte y esto no es prolijidad: quien llama es el bucle de lectura, que es
    /// el mismo que trae el <c>interrupted</c>. Abrir una aplicación tarda un segundo largo, y
    /// esperarla adentro del bucle es quedarse sordo justo mientras la persona podría estar
    /// hablándole encima.
    /// <para>
    /// Las llamadas se ejecutan <b>en orden</b> y no en paralelo. Cuando el modelo pide dos cosas
    /// juntas suelen depender una de la otra —abrir la ventana y después escribir en ella—, y
    /// largarlas a la vez las mezcla.
    /// </para>
    /// <para>
    /// Se contesta siempre, incluso cuando falla. El servidor no cierra el turno hasta recibir la
    /// respuesta: una llamada sin contestar no se ve como un error sino como que se quedó muda a
    /// mitad de frase.
    /// </para>
    /// </remarks>
    private void DispatchToolCalls(IReadOnlyList<LiveFunctionCall> calls, CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                var responses = new List<LiveFunctionResponse>(calls.Count);

                foreach (var call in calls)
                {
                    if (WasCancelled(call.Id))
                    {
                        continue;
                    }

                    Raise(ToolActivity, new LiveToolEventArgs(call.Name, finished: false, succeeded: false, null));

                    LiveToolOutcome outcome;
                    try
                    {
                        outcome = _tools is null
                            ? LiveToolOutcome.Failed("No tengo esa herramienta conectada en la sesión hablada.")
                            : await _tools.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // La charla se cerró mientras la herramienta corría. No hay a quién
                        // contestarle y el resto del lote ya no tiene sentido.
                        return;
                    }
                    catch (Exception exception)
                    {
                        // El texto del error no viaja al modelo: puede llevar rutas de esta máquina.
                        LastFailure = $"Una herramienta de la sesión hablada falló ({exception.GetType().Name}).";
                        outcome = LiveToolOutcome.Failed("La herramienta no pudo completar la operación.");
                    }

                    Raise(ToolActivity, new LiveToolEventArgs(
                        call.Name,
                        finished: true,
                        outcome.Succeeded,
                        outcome.Message));

                    // Se vuelve a mirar recién ahora porque la cancelación llega mientras corría.
                    if (!WasCancelled(call.Id))
                    {
                        responses.Add(new LiveFunctionResponse(call.Id, call.Name, outcome));
                    }
                }

                if (responses.Count > 0)
                {
                    await SendRawAsync(LiveClientMessages.BuildToolResponse(responses), cancellationToken)
                        .ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Si el servidor canceló esta llamada. La saca al mirarla: se pregunta una vez por id.</summary>
    private bool WasCancelled(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        lock (_toolGate)
        {
            return _cancelledToolCalls.Remove(id);
        }
    }

    private async Task<bool> SendRawAsync(string message, CancellationToken cancellationToken)
    {
        var transport = _transport;
        if (transport is null || !_connected)
        {
            return false;
        }

        try
        {
            await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            _connected = false;
            LastFailure = $"Se cortó el envío a la sesión en vivo ({exception.GetType().Name}).";
            return false;
        }
    }

    private static async Task<bool> WaitBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(4_000, 250 * Math.Pow(2, attempt - 1)));
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dispara un evento sin dejar que un suscriptor roto tumbe el bucle de lectura.
    /// </summary>
    /// <remarks>
    /// Los suscriptores viven en la interfaz, y una excepción de la interfaz que suba hasta acá
    /// mataría la tarea que lee del servidor: la sesión quedaría abierta y muda, que es la peor
    /// forma de fallar porque no se parece a un error.
    /// </remarks>
    private void Raise<TArgs>(EventHandler<TArgs>? handler, TArgs args)
        where TArgs : EventArgs
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastFailure = $"Un suscriptor de la sesión en vivo falló ({exception.GetType().Name}).";
        }
    }

    private void Raise(EventHandler? handler, EventArgs args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastFailure = $"Un suscriptor de la sesión en vivo falló ({exception.GetType().Name}).";
        }
    }
}
