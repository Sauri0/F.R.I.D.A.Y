namespace Viernes.Core.Live;

/// <summary>
/// El enchufe de la sesión en vivo: elige el camino, la abre, traduce lo que pasa a momentos del
/// orbe y se cae sola al camino de siempre cuando algo se rompe.
/// </summary>
/// <remarks>
/// <see cref="GeminiLiveClient"/> sabe hablar el protocolo y nada más: no sabe que existe otro
/// camino, ni que hay un orbe, ni cuándo conviene no intentar. Todo eso es la decisión del
/// anfitrión, y estaba sin escribir — por eso el cliente quedó completo y sin que nadie lo llamara.
/// <para>
/// Vive en Core y no en la aplicación por una sola razón, que es la que importa: de este lado se
/// puede probar entero sin red, sin micrófono y sin parlantes. Lo que queda del otro lado son los
/// dispositivos y la traducción de cuatro momentos a cuatro estados del orbe.
/// </para>
/// <para>
/// No toca el micrófono ni los parlantes: recibe audio por <see cref="PushMicrophoneAsync"/> y
/// entrega por el <see cref="ILiveAudioSink"/> que le pasen.
/// </para>
/// </remarks>
public sealed class LiveVoiceSession : IAsyncDisposable
{
    private readonly GeminiLiveOptions _options;
    private readonly Func<string?> _apiKey;
    private readonly LiveFallbackLatch _latch;
    private readonly GeminiLiveClient _client;
    private readonly LiveUserSpeechGate _speechGate;
    private readonly ILiveAudioSink _sink;
    private readonly Lock _momentGate = new();

    private LiveOrbMoment _moment = LiveOrbMoment.Listening;
    private bool _waitingForReply;
    private bool _echoRun;

    /// <summary>Cuánta voz lleva este tramo con el parlante ya callado.</summary>
    /// <remarks>
    /// Es lo único que separa el eco de alguien hablando encima de la cola de la respuesta: el eco
    /// <em>es</em> el parlante, así que no puede sobrevivirlo, y una persona sí. Ver
    /// <see cref="NoteUserAudio"/>.
    /// </remarks>
    /// <summary>
    /// Cuánta voz con el parlante ya callado hace falta para creer que no es la cola del eco.
    /// </summary>
    /// <remarks>
    /// Contra cero no alcanzaba, y ese fue el arreglo anterior. El eco no deja de existir en el
    /// instante en que la cola llega a cero: entre el parlante y el bloque que se está clasificando
    /// hay el búfer de captura, la latencia del driver de entrada, la propagación del cuarto, y sobre
    /// todo la histéresis del detector —una vez adentro de una frase sigue diciendo «voz» con una
    /// probabilidad más baja de la que hizo falta para entrar—. Un solo bloque de 20 ms de eco
    /// residual alcanzaba para que la cola se leyera como una persona hablando, y el orbe volvía a
    /// quedarse clavado en «Pensando…» sin que nadie hubiera hablado: el mismo síntoma que el arreglo
    /// venía a cerrar, entrando por la puerta de al lado.
    /// <para>
    /// 180 ms parte los dos casos con margen: alguien que le habla encima pasa ese umbral con la
    /// primera sílaba, y una cola de eco —que son unas pocas decenas de milisegundos por encima del
    /// margen de la medición de la cola— no llega. No sale del fuente de los bocetos: es una
    /// constante de esta implementación y por eso está acá, con su razón, en vez de escrita en la
    /// comparación.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan MinimumVoiceOverEcho = TimeSpan.FromMilliseconds(180);

    private TimeSpan _voiceAfterSpeaker;
    private TimeSpan _idleSilence;
    private bool _quietRaised;
    private int _drainWatch;
    private int _disposed;

    /// <summary>Cada cuánto se le pregunta al parlante si ya terminó de sonar.</summary>
    /// <remarks>
    /// Cuarenta milisegundos: dos bloques de los que el parlante le da al driver. Preguntar más
    /// seguido no adelanta nada —el corte del audio no es más fino que eso— y preguntar mucho menos
    /// seguido deja el orbe diciendo «hablando» después de que se calló.
    /// </remarks>
    private static readonly TimeSpan SpeakerPollInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Hasta cuándo se sigue mirando el parlante antes de darlo por perdido.
    /// </summary>
    /// <remarks>
    /// Lo que aguanta la cola del parlante. Es un tope de seguridad, no un plazo esperado: sin él,
    /// una salida que informe mal cuánto le queda dejaría esta tarea girando hasta que se apague la
    /// máquina.
    /// </remarks>
    private static readonly TimeSpan SpeakerDrainLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cuánto silencio hace falta para dar la charla por abandonada.
    /// </summary>
    /// <remarks>
    /// Un minuto, que es lo que ya tarda en cerrarse el camino de siempre: seis ventanas de
    /// escucha de diez segundos sin que nadie diga nada. No se elige un número nuevo porque el
    /// usuario no tiene por qué notar dos paciencias distintas según por dónde haya ido la charla.
    /// </remarks>
    public static readonly TimeSpan DefaultQuietTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _quietTimeout;

    /// <summary>Arma la sesión sin abrir nada.</summary>
    /// <param name="options">Cómo se abre y cómo se comporta la sesión en vivo.</param>
    /// <param name="apiKey">
    /// De dónde sale la clave de Google. Entra como función porque se lee en cada conexión: si el
    /// usuario acaba de pegar una clave nueva, la reconexión la toma sin reiniciar nada.
    /// <b>El valor no se guarda en ningún campo de esta clase</b>; lo único que se conserva es la
    /// función.
    /// </param>
    /// <param name="audioSink">Dónde sale la voz. Tiene que poder vaciarse de golpe.</param>
    /// <param name="transportFactory">Con qué se conecta. Se deja abierto para poder probar sin red.</param>
    /// <param name="latch">La traba de caída. Si no se pasa una, se arma con los valores por defecto.</param>
    /// <param name="quietTimeout">
    /// Cuánto silencio hace falta para avisar que la charla quedó abandonada. Por defecto
    /// <see cref="DefaultQuietTimeout"/>; se puede acortar para probarlo sin esperar el minuto.
    /// </param>
    public LiveVoiceSession(
        GeminiLiveOptions options,
        Func<string?> apiKey,
        ILiveAudioSink audioSink,
        Func<ILiveTransport>? transportFactory = null,
        LiveFallbackLatch? latch = null,
        TimeSpan? quietTimeout = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _latch = latch ?? new LiveFallbackLatch();
        _quietTimeout = quietTimeout ?? DefaultQuietTimeout;
        _speechGate = new LiveUserSpeechGate(TimeSpan.FromMilliseconds(options.SilenceDurationMs));

        // La salida se guarda además de pasársela al cliente: el cliente la usa para encolar y acá
        // se la mira para saber si todavía está sonando, que es lo que separa «terminó el turno» de
        // «se calló». Son dos cosas distintas y confundirlas fue el bug.
        _sink = audioSink ?? throw new ArgumentNullException(nameof(audioSink));
        _client = new GeminiLiveClient(options, apiKey, audioSink, transportFactory);

        _client.TurnStateChanged += OnTurnStateChanged;
        _client.Interrupted += OnInterrupted;
        _client.TranscriptReceived += OnTranscriptReceived;
        _client.Failed += OnFailed;
    }

    /// <summary>Cambió el momento de la charla. Es lo que mira el orbe.</summary>
    public event EventHandler<LiveMomentChangedEventArgs>? MomentChanged;

    /// <summary>Llegó texto transcripto, de uno u otro lado.</summary>
    public event EventHandler<LiveTranscriptEventArgs>? TranscriptReceived;

    /// <summary>
    /// La sesión en vivo se cayó y hay que seguir por el camino de siempre.
    /// </summary>
    /// <remarks>
    /// Sólo se dispara con fallas de las que no se vuelve. Las pasajeras —una desconexión de la que
    /// el cliente se recupera solo— no llegan acá: avisar de cada una haría que el anfitrión cortara
    /// una conversación que en realidad siguió andando.
    /// </remarks>
    public event EventHandler<LiveFailureEventArgs>? FellBack;

    /// <summary>
    /// Pasó un minuto sin que nadie hable: la charla quedó abandonada.
    /// </summary>
    /// <remarks>
    /// Se dispara una sola vez por tramo de silencio; si la persona vuelve a hablar, el contador se
    /// borra y puede volver a dispararse más tarde. Quien la cierra es el anfitrión: acá sólo se
    /// avisa, porque cerrar es una decisión sobre la conversación entera y no sobre esta conexión.
    /// </remarks>
    public event EventHandler? WentQuiet;

    /// <summary>Si la sesión está abierta y aceptada.</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>En qué anda el turno.</summary>
    public LiveTurnState TurnState => _client.TurnState;

    /// <summary>
    /// Si la compuerta considera que la persona está hablando ahora mismo.
    /// </summary>
    /// <remarks>
    /// Lo lee el anfitrión para saber si el audio que está entrando es voz de alguien o es el cuarto:
    /// mientras hay alguien hablando, nada de lo que llega sirve para estimar el ruido de fondo.
    /// </remarks>
    public bool IsUserSpeaking => _speechGate.IsSpeaking;

    /// <summary>
    /// Si todavía queda voz por salir por los parlantes.
    /// </summary>
    /// <remarks>
    /// No es lo mismo que tener el turno abierto. El servidor despacha el audio más rápido que
    /// tiempo real, así que cuando cierra el turno pueden quedar segundos de respuesta esperando de
    /// este lado.
    /// </remarks>
    public bool IsSpeakerBusy => _sink.Pending > TimeSpan.Zero;

    /// <summary>El momento que le corresponde al orbe ahora mismo.</summary>
    public LiveOrbMoment Moment
    {
        get
        {
            lock (_momentGate)
            {
                return _moment;
            }
        }
    }

    /// <summary>Cuántas veces la cortaron desde que arrancó.</summary>
    public int InterruptionCount => _client.InterruptionCount;

    /// <summary>Cuánto va costando la sesión.</summary>
    public LiveCostMeter Cost => _client.Cost;

    /// <summary>Lo último que salió mal. Nunca incluye la clave.</summary>
    public string? LastFailure => _client.LastFailure;

    /// <summary>Por qué está trabado el camino nuevo, si lo está.</summary>
    public string? BlockedReason => _latch.BlockedReason;

    /// <summary>
    /// Qué camino corresponde ahora mismo, sin abrir nada.
    /// </summary>
    /// <remarks>
    /// Se puede llamar todas las veces que haga falta: no toca la red ni cambia nada. Sirve para
    /// mostrar por dónde va a ir la próxima conversación antes de empezarla.
    /// </remarks>
    public VoiceRouteDecision Decide() =>
        VoiceRouter.Choose(
            _options.Enabled,
            !string.IsNullOrWhiteSpace(_apiKey()),
            _latch.BlockedReason);

    /// <summary>
    /// Elige el camino y, si es el nuevo, abre la sesión.
    /// </summary>
    /// <remarks>
    /// Nunca lanza por no poder conectar: devuelve la decisión con el motivo, que es lo que el
    /// anfitrión necesita para escribirlo en la bitácora y seguir por el camino de siempre. Un
    /// asistente que se queda mudo porque un servicio no contestó es exactamente lo que esto viene a
    /// impedir.
    /// </remarks>
    public async Task<VoiceRouteDecision> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var decision = Decide();
        if (!decision.IsLive)
        {
            return decision;
        }

        _speechGate.Reset();
        _echoRun = false;
        _voiceAfterSpeaker = TimeSpan.Zero;
        _idleSilence = TimeSpan.Zero;
        _quietRaised = false;
        SetWaiting(false);

        if (await _client.StartAsync(cancellationToken).ConfigureAwait(false))
        {
            // Abrir es la única prueba de que el servicio volvió; recién acá se borra la escalera.
            _latch.Reset();
            Publish(LiveOrbMoment.Listening);
            return decision;
        }

        var reason = _client.LastFailure ?? "no pude abrir la sesión en vivo";
        _latch.Trip(reason);
        return new VoiceRouteDecision(VoiceRoute.Classic, reason);
    }

    /// <summary>Manda audio del micrófono. PCM 16 bits little endian, mono, 16 kHz.</summary>
    public Task<bool> PushMicrophoneAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken cancellationToken = default) =>
        _client.SendAudioAsync(pcm16k, cancellationToken);

    /// <summary>
    /// Le cuenta a la sesión lo que el detector de voz del anfitrión opinó de un bloque.
    /// </summary>
    /// <remarks>
    /// Es de dónde sale «pensando», y es el único momento del orbe que el servidor no manda: entre
    /// que cerrás la frase y que llega el primer bloque de voz no hay ningún mensaje. Ver
    /// <see cref="LiveOrbMoments"/>.
    /// <para>
    /// El micrófono queda abierto mientras ella habla, así que acá entra también su propia voz
    /// filtrada por los parlantes. Por eso el borde de «terminó» sólo cuenta con el turno en reposo:
    /// si contara siempre, el eco de una respuesta larga dibujaría «pensando» encima de «hablando».
    /// </para>
    /// <para>
    /// Y por eso el tramo marcado como eco se sigue mirando en vez de darse por perdido: el eco no
    /// puede durar más que el parlante —es el parlante—, así que voz con el parlante ya callado es
    /// alguien hablando de verdad. Eso descansa en que <see cref="ILiveAudioSink.Pending"/> no llegue
    /// a cero antes de tiempo; <c>LiveSpeakerSink</c> le suma lo que el driver tiene en la mano
    /// justamente para eso.
    /// </para>
    /// <para>
    /// Lo llama un solo hilo, el del dispositivo de captura. La compuerta no lleva candado por eso;
    /// llamarlo desde varios lados a la vez le desordenaría el contador de silencio.
    /// </para>
    /// </remarks>
    public void NoteUserAudio(bool isVoice, TimeSpan blockDuration)
    {
        var edge = _speechGate.Write(isVoice, blockDuration);

        TrackAbandonment(isVoice, blockDuration);

        // Mientras el tramo marcado como eco sigue abierto se cuenta la voz que llega con el
        // parlante ya callado. Va antes de la salida por «no hubo borde» porque el tramo son
        // justamente todos esos bloques del medio, que es donde está la única evidencia disponible.
        if (_echoRun && isVoice && !IsSpeakerBusy)
        {
            _voiceAfterSpeaker += blockDuration;
        }

        if (edge == LiveSpeechEdge.None)
        {
            return;
        }

        if (edge == LiveSpeechEdge.Started)
        {
            // Si arrancó con los parlantes sonando, esto es —por ahora— su propia voz volviendo por
            // el micrófono. Se anota acá, en el borde de arranque, porque es el único momento en que
            // se sabe: cuando llegue el borde de «terminó» el parlante hace rato que se calló.
            _echoRun = IsSpeakerBusy;
            _voiceAfterSpeaker = TimeSpan.Zero;
            SetWaiting(false);
            Refresh();
            return;
        }

        if (_echoRun && _voiceAfterSpeaker < MinimumVoiceOverEcho)
        {
            // Era la cola del eco de la respuesta anterior y nada más. La compuerta necesita 700 ms
            // de silencio para cerrar, así que este borde llega bastante después, con el turno ya en
            // reposo. Sin esta marca, el orbe quedaba clavado en «Pensando…» sin que nadie hubiera
            // hablado — y encima tapaba «En vivo · decime «listo» para cortar», que es el único lugar
            // donde se dice cómo cerrar la charla.
            _echoRun = false;
            return;
        }

        // El tramo cierra como frase de alguien: o nunca fue eco, o empezó como eco y siguió con el
        // parlante ya callado, que es hablarle encima mientras drena la respuesta. Ese segundo caso
        // quedaba tragado entero por la marca: el servidor no manda «interrupted» —ya no está
        // generando— así que nadie la borraba, no había SetWaiting(true), y el orbe se quedaba en «te
        // escucho» durante toda la latencia del modelo. La prueba que había cubría el caso con
        // interrupción del servidor, que es otro.
        _echoRun = false;
        _voiceAfterSpeaker = TimeSpan.Zero;

        if (_client.TurnState == LiveTurnState.Idle && !IsSpeakerBusy)
        {
            SetWaiting(true);
            Refresh();
        }
    }

    /// <summary>
    /// Lleva la cuenta del silencio para poder avisar que la charla quedó abandonada.
    /// </summary>
    /// <remarks>
    /// El camino de siempre se cierra solo cuando nadie contesta; el nuevo no tenía cómo, y eso no
    /// es un detalle de prolijidad: una sesión en vivo abierta manda audio del micrófono a la nube
    /// sin parar y se cobra por minuto. Alguien que se levanta de la silla sin decir nada deja el
    /// micrófono transmitiendo hasta que apague la máquina.
    /// <para>
    /// Se cuenta sumando la duración de los bloques y no mirando el reloj, para que se pueda probar
    /// sin esperar el minuto de verdad. Y sólo cuenta el silencio con el turno en reposo: mientras
    /// ella contesta no hay nadie abandonando nada.
    /// </para>
    /// </remarks>
    private void TrackAbandonment(bool isVoice, TimeSpan blockDuration)
    {
        if (isVoice || _client.TurnState != LiveTurnState.Idle)
        {
            _idleSilence = TimeSpan.Zero;
            _quietRaised = false;
            return;
        }

        if (_quietRaised)
        {
            return;
        }

        _idleSilence += blockDuration;
        if (_idleSilence < _quietTimeout)
        {
            return;
        }

        _quietRaised = true;
        Raise(WentQuiet, EventArgs.Empty);
    }

    /// <summary>
    /// Calla los parlantes ahora, por decisión de este lado.
    /// </summary>
    /// <remarks>
    /// <b>No está enganchado al detector de voz a propósito.</b> El micrófono sigue abierto mientras
    /// ella habla, así que su propia voz vuelve por los parlantes y el detector la ve; engancharlo
    /// sin un umbral medido contra este equipo la haría cortarse sola a mitad de cada respuesta, que
    /// es peor que llegar unos milisegundos tarde a la interrupción. La interrupción de verdad la
    /// manda el servidor y el cliente ya la atiende.
    /// <para>
    /// Queda expuesto porque el freno del usuario —el botón de pánico, el orbe— sí tiene que poder
    /// callarla sin esperar a nadie.
    /// </para>
    /// </remarks>
    public void SilenceNow()
    {
        _client.SilenceNow();
        Publish(LiveOrbMoment.Interrupted);
    }

    /// <summary>Manda texto escrito por el mismo canal.</summary>
    public Task<bool> SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        _client.SendTextAsync(text, cancellationToken);

    /// <summary>Cierra la sesión y vuelve a dejar todo como al principio.</summary>
    public async Task StopAsync()
    {
        await _client.StopAsync().ConfigureAwait(false);
        _speechGate.Reset();
        _echoRun = false;
        _voiceAfterSpeaker = TimeSpan.Zero;
        _idleSilence = TimeSpan.Zero;
        _quietRaised = false;
        SetWaiting(false);
        Publish(LiveOrbMoment.Listening);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Primero desuscribir y después cerrar: al revés, el cierre dispara el último cambio de
        // turno y lo recibe un anfitrión que ya se dio por terminado.
        _client.TurnStateChanged -= OnTurnStateChanged;
        _client.Interrupted -= OnInterrupted;
        _client.TranscriptReceived -= OnTranscriptReceived;
        _client.Failed -= OnFailed;

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTurnStateChanged(object? sender, LiveTurnStateChangedEventArgs eventArgs)
    {
        // Empezó a llegar la respuesta: lo que se estaba esperando ya llegó.
        if (eventArgs.Current != LiveTurnState.Idle)
        {
            SetWaiting(false);
        }

        Refresh();

        // El turno cerró pero el parlante puede seguir sonando, y del servidor no va a llegar ningún
        // mensaje más que avise cuándo termina: el único que lo sabe es el parlante. Por eso acá
        // arranca alguien que le pregunte hasta que se calle.
        if (eventArgs.Current == LiveTurnState.Idle && IsSpeakerBusy)
        {
            WatchSpeakerDrain();
        }
    }

    /// <summary>
    /// La cortaron. La cola de audio ya se vació antes de que esto se dispare.
    /// </summary>
    /// <remarks>
    /// Se publica acá y no sólo desde el cambio de turno porque el orbe tiene que ver el corte en el
    /// mismo cuadro: la transición de hablando a interrumpida dura 80 ms y es un corte, no un
    /// fundido. Si esperara a que se recalcule el momento por otro camino, se vería como un fundido
    /// tardío y se leería como que no hizo caso.
    /// </remarks>
    private void OnInterrupted(object? sender, EventArgs eventArgs)
    {
        SetWaiting(false);
        _speechGate.Reset();

        // La cola ya se vació, así que lo que la persona siga diciendo arranca con el parlante
        // callado y no se va a confundir con eco. Sin borrarlo acá, la marca de la respuesta que
        // acaba de cortar se llevaba puesto el «pensando» de la frase nueva.
        _echoRun = false;
        _voiceAfterSpeaker = TimeSpan.Zero;
        Publish(LiveOrbMoment.Interrupted);
    }

    private void OnTranscriptReceived(object? sender, LiveTranscriptEventArgs eventArgs) =>
        Raise(TranscriptReceived, eventArgs);

    private void OnFailed(object? sender, LiveFailureEventArgs eventArgs)
    {
        if (!eventArgs.Fatal)
        {
            return;
        }

        _latch.Trip(eventArgs.Message);
        Raise(FellBack, eventArgs);
    }

    private void SetWaiting(bool waiting)
    {
        lock (_momentGate)
        {
            _waitingForReply = waiting;
        }
    }

    private void Refresh()
    {
        bool waiting;
        lock (_momentGate)
        {
            waiting = _waitingForReply;
        }

        Publish(LiveOrbMoments.For(_client.TurnState, waiting, IsSpeakerBusy));
    }

    /// <summary>
    /// Mira el parlante hasta que se calle y recién ahí recalcula el momento.
    /// </summary>
    /// <remarks>
    /// Es una tarea aparte y no una espera adentro del bucle de lectura a propósito: ese bucle es el
    /// mismo que trae el aviso de interrupción, y frenarlo esperando a que termine de sonar una
    /// respuesta es exactamente lo contrario de lo que hace falta.
    /// <para>
    /// Nunca hay dos: si el turno siguiente cierra mientras ésta todavía mira, la bandera la deja
    /// pasar y sigue mirando la que ya estaba.
    /// </para>
    /// </remarks>
    private void WatchSpeakerDrain()
    {
        if (Interlocked.Exchange(ref _drainWatch, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref _disposed) == 0 &&
                    IsSpeakerBusy &&
                    reloj.Elapsed < SpeakerDrainLimit)
                {
                    await Task.Delay(SpeakerPollInterval).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _disposed) == 0)
                {
                    Refresh();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Una salida que se rompa al preguntarle cuánto le queda no puede tumbar el proceso
                // desde una tarea del pool. El orbe queda un momento de más en «hablando», que es el
                // lado seguro del error.
            }
            finally
            {
                Interlocked.Exchange(ref _drainWatch, 0);
            }
        });
    }

    private void Publish(LiveOrbMoment moment)
    {
        LiveOrbMoment previous;
        lock (_momentGate)
        {
            if (_moment == moment)
            {
                return;
            }

            previous = _moment;
            _moment = moment;
        }

        Raise(MomentChanged, new LiveMomentChangedEventArgs(previous, moment));
    }

    /// <summary>
    /// Dispara un evento sin dejar que un suscriptor roto suba hasta el bucle de lectura del cliente.
    /// </summary>
    /// <remarks>
    /// El cliente ya se protege de esto, pero se protege anotando la falla y siguiendo: si la
    /// excepción sale de acá, lo que queda escrito es «un suscriptor de la sesión en vivo falló» sin
    /// decir cuál. Atajarla en el borde deja el motivo donde se puede leer.
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
            // Un oyente roto no puede cortar la charla.
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
            // Idem.
        }
    }
}
