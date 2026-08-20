using NAudio.Wave;
using Viernes.Core.Live;

namespace Viernes.Platform.Windows.Live;

/// <summary>
/// Los parlantes de la sesión en vivo: un caño de audio que se puede vaciar de golpe.
/// </summary>
/// <remarks>
/// No se parece a <see cref="Speech.NeuralSpeechPlayer"/> y no puede parecérsele. Aquel reproduce
/// una respuesta entera que ya está completa; acá el audio llega en bloques mientras se genera, así
/// que el dispositivo se abre una vez y se queda abierto toda la conversación. Abrirlo y cerrarlo
/// por bloque metería el arranque del driver —decenas de milisegundos— en el medio de cada sílaba.
/// <para>
/// <b>La geometría de los búferes existe para que <see cref="Flush"/> se oiga en el acto.</b> La
/// transición del orbe de hablando a interrumpida dura 80 ms y es un corte, no un fundido: si el
/// audio tarda más que eso en callarse, la persona ve el corte y lo sigue escuchando, que se lee
/// como que no hizo caso.
/// </para>
/// <para>
/// <b>Y lo otro que hay acá es el colchón de arranque, que vino después y por otro motivo.</b> El
/// usuario reportó que a veces se le corta la voz mientras habla. La salida reproduce con
/// <c>ReadFully</c>, que es lo correcto, pero tiene un costo invisible: cuando la cola se queda
/// corta el hueco se rellena con silencio y no lo denuncia nadie. Y el peor momento para que se
/// quede corta es el arranque: NAudio le pide al proveedor todos sus búferes en la primera vuelta
/// —cien milisegundos de una— y la reproducción arrancaba con el primer bloque de veinte que
/// llegara, así que ochenta de esos cien salían de silencio, adentro de la primera palabra, en cada
/// respuesta. Está medido con esta misma geometría y da <c>relleno = 100 ms − lo encolado</c>,
/// clavado, y cero justo al llegar a cien. <see cref="LivePlayout"/> espera ese colchón —ni un
/// milisegundo más, porque de ahí en adelante es demora pura— y deja escrito en la bitácora cuándo
/// el parlante se quedó sin nada que sonar.
/// </para>
/// </remarks>
public sealed class LiveSpeakerSink : ILiveAudioSink, IDisposable
{
    /// <summary>
    /// Cuánto audio le da al driver por vez.
    /// </summary>
    /// <remarks>
    /// Es el número que decide cuánto tarda el corte. <c>waveOutReset</c> tira los búferes que
    /// todavía no arrancaron, pero el que está sonando termina de sonar: el resto audible después
    /// de un <see cref="Flush"/> es, como mucho, uno de estos. A 20 ms entra cómodo adentro de los
    /// 80 ms que dura la transición de interrumpida. Con los valores de fábrica de NAudio —300 ms
    /// repartidos en dos— serían 150 ms y el corte se oiría llegar tarde.
    /// <para>
    /// Son los mismos 20 ms con los que se sube el audio del micrófono
    /// (<see cref="GeminiLiveOptions.DefaultChunkMilliseconds"/>), y por el mismo motivo: acá cada
    /// milisegundo acumulado se oye.
    /// </para>
    /// </remarks>
    private const int BufferMilliseconds = 20;

    private const int BufferCount = 5;

    /// <summary>
    /// Cuánto audio llega a tener el driver en la mano, como mucho.
    /// </summary>
    /// <remarks>
    /// Es el <c>DesiredLatency</c> con el que se abre la salida, escrito una sola vez: cien
    /// milisegundos repartidos en cinco búferes de veinte. Todo lo que el driver ya se llevó
    /// <b>salió de la cola y todavía no sonó</b>, y ése es exactamente el margen que
    /// <see cref="Pending"/> se estaba comiendo.
    /// </remarks>
    private static readonly TimeSpan DriverLatency =
        TimeSpan.FromMilliseconds(BufferMilliseconds * BufferCount);

    /// <summary>
    /// Cuánto audio de respuesta aguanta la cola.
    /// </summary>
    /// <remarks>
    /// Generoso a propósito. Quien encola es el bucle que lee del servidor, y ese bucle es el mismo
    /// que trae el aviso de interrupción: <b>si encolar bloqueara, el aviso de que la cortaron
    /// llegaría detrás del audio que había que tirar</b>. Por eso la cola nunca espera y nunca
    /// lanza; para no tener que descartar en la práctica, entra una respuesta larguísima entera.
    /// Dos minutos a 24 kHz de 16 bits son menos de seis megabytes.
    /// </remarks>
    private static readonly TimeSpan QueueCapacity = TimeSpan.FromMinutes(2);

    private static readonly WaveFormat Format = new(
        LiveAudioFormat.OutputSampleRate,
        LiveAudioFormat.BitsPerSample,
        LiveAudioFormat.Channels);

    /// <summary>
    /// Hasta cuándo se espera el colchón antes de arrancar igual.
    /// </summary>
    /// <remarks>
    /// Red de seguridad, no plazo esperado. <see cref="CompleteTurnAsync"/> ya suelta la respuesta
    /// corta que nunca junta el colchón, pero eso depende de que el turno cierre: si el servidor
    /// manda dos palabras y después se va diez segundos a esperar una herramienta, sin este plazo
    /// esas dos palabras se quedarían calladas en la cola todo ese rato.
    /// <para>
    /// <b>Ciento cincuenta milisegundos es el techo de lo que todo esto puede demorar una voz</b>, y
    /// está escrito acá para que se lea de una: es la cuenta que hay que mirar antes de creer que
    /// esto sale gratis. Vale una vez y media el colchón, que es de cien y está medido; el intento
    /// anterior pedía trescientos porque el colchón era de doscientos cincuenta, y esos ciento
    /// cincuenta de más se pagaban en cada respuesta —también en la que sigue a una interrupción, que
    /// es la interacción que el usuario dijo que andaba bien.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan PrimeDeadline = TimeSpan.FromMilliseconds(150);

    private readonly Lock _gate = new();

    /// <summary>
    /// La cuenta de cuándo arrancar y de si se oyó un silencio que no estaba en la voz.
    /// </summary>
    /// <remarks>
    /// Vive en Core porque es lo único de todo esto que se puede probar sin tarjeta de sonido, y
    /// porque el defecto que viene a cerrar —cortes de voz que ningún contador registraba— ya se
    /// buscó una vez leyendo código y no apareció.
    /// </remarks>
    private readonly LivePlayout _playout = new();

    private BufferedWaveProvider? _queue;
    private DriverTap? _tap;
    private WaveOutEvent? _output;
    private System.Threading.Timer? _primeTimer;
    private bool _primeArmed;
    private bool _started;
    /// <summary>
    /// Si el turno ya cerró y no viene más audio.
    /// </summary>
    /// <remarks>
    /// Va como entero y se lee sin candado porque lo consulta el hilo del driver en cada lectura, y
    /// ese hilo no puede pedir el candado de la salida. Ver <see cref="NoteDriverRead"/>.
    /// </remarks>
    private int _turnClosedFlag;
    private bool _disposed;

    private bool TurnClosed
    {
        get => Volatile.Read(ref _turnClosedFlag) != 0;
        set => Volatile.Write(ref _turnClosedFlag, value ? 1 : 0);
    }

    /// <summary>Cuántas veces la mandaron a callar. Para la bitácora y el diagnóstico.</summary>
    public int FlushCount { get; private set; }

    /// <summary>
    /// Se oyó un silencio en el medio de una respuesta.
    /// </summary>
    /// <remarks>
    /// Se dispara <b>desde el hilo del driver</b>, así que quien se suscriba no puede hacer nada que
    /// tarde: anotar un renglón en la bitácora —que encola y escribe en otro hilo— está bien;
    /// escribir en disco o tocar la interfaz, no.
    /// <para>
    /// Existe porque el corte que reportó el usuario no dejaba ningún rastro: la cola se rellena con
    /// silencio y la reproducción sigue como si nada. Sin esto, cualquier arreglo del audio es una
    /// suposición.
    /// </para>
    /// </remarks>
    public event EventHandler<LiveAudioGapEventArgs>? GapHeard;

    /// <summary>
    /// Cuántos huecos se oyeron desde que se armó la salida. Para el renglón de cierre.
    /// </summary>
    /// <remarks>
    /// Acumulado y no por conversación, igual que <see cref="FlushCount"/>: la salida se reusa entre
    /// charlas y este número es del asistente encendido, que es la unidad en la que el usuario cuenta
    /// lo que le pasó.
    /// </remarks>
    public int Gaps => _playout.Gaps;

    /// <summary>Cuánto silencio suman esos huecos.</summary>
    public TimeSpan GapTotal => _playout.GapTotal;

    /// <summary>
    /// Todo el silencio que <c>ReadFully</c> metió, se haya informado como hueco o no.
    /// </summary>
    /// <remarks>
    /// Va aparte de <see cref=GapTotal/> porque no acusa a nadie: acá adentro está el final de
    /// cada frase y cada herramienta que tardó, que no son defectos. Sirve para poder escribir «se
    /// rellenaron X ms» sin tener que afirmar que algo estuvo mal.
    /// </remarks>
    public TimeSpan Filler => _playout.Filler;

    /// <summary>Si el dispositivo está abierto.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _output is not null;
            }
        }
    }

    /// <summary>Bytes esperando salir por los parlantes.</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queue?.BufferedBytes ?? 0;
            }
        }
    }

    /// <summary>
    /// Cuánta voz queda por oírse: lo que está en la cola más lo que el driver ya se llevó.
    /// </summary>
    /// <remarks>
    /// <see cref="QueuedBytes"/> estaba y no lo leía nadie fuera de las pruebas, así que el orbe
    /// volvía a «te escucho» con segundos de respuesta todavía adentro de esta cola. Quien decide
    /// —<c>LiveVoiceSession</c>— razona en tiempo y no en bytes, y la frecuencia de salida es asunto
    /// de acá.
    /// <para>
    /// <b>La cola sola no alcanza y por eso está <see cref="DriverTap"/>.</b> El proveedor va con
    /// <c>ReadFully</c>, así que el driver se lleva búferes enteros y los rellena con silencio si
    /// hace falta: hasta <see cref="DriverLatency"/> de audio ya salió de <c>BufferedBytes</c> y
    /// todavía no sonó. Con eso solo, el contrato de <see cref="ILiveAudioSink.Pending"/> —cero es
    /// «el parlante está callado»— era falso sobre el final de cada respuesta, y el orbe volvía a «te
    /// escucho» antes de que se terminara de oír la última sílaba. Medido contra la salida real de
    /// este equipo con medio segundo de audio encolado: llegaba a cero a los 439 ms, o sea 96 ms
    /// antes de tiempo; con el margen contado, a los 535 ms.
    /// </para>
    /// <para>
    /// El margen se cuenta desde la última vez que el driver se llevó audio <em>de verdad</em>, no
    /// mientras la salida esté abierta: con <c>ReadFully</c> la reproducción no se detiene nunca
    /// sola, así que sumar la latencia por estar sonando dejaría esto clavado en cien milisegundos
    /// para siempre. Se pasa de largo por debajo de eso, que es el lado seguro: decir «todavía se
    /// oye» un instante de más no rompe nada; decir «me callé» antes de tiempo es el bug.
    /// </para>
    /// </remarks>
    public TimeSpan Pending
    {
        get
        {
            DriverTap? tap;
            lock (_gate)
            {
                tap = _tap;
            }

            return LiveAudioFormat.OutputDurationOf(QueuedBytes) + (tap?.Remaining ?? TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(ReadOnlyMemory<byte> pcm24k, CancellationToken cancellationToken)
    {
        if (pcm24k.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            if (!EnsureOpen())
            {
                return ValueTask.CompletedTask;
            }

            _queue!.AddSamples(pcm24k.ToArray(), 0, pcm24k.Length);

            // Llegó audio: el turno está abierto otra vez, venga de donde venga.
            TurnClosed = false;

            if (!_started)
            {
                ArmPrimeDeadlineLocked();
                StartIfPrimedLocked(force: false);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Le da el play al dispositivo si ya hay colchón juntado.
    /// </summary>
    /// <remarks>
    /// <b>Arrancar apenas llega el primer bloque es lo que hacía tartamudear la primera palabra de
    /// cada respuesta.</b> NAudio, en la primera vuelta de su hilo de reproducción, le pide al
    /// proveedor todos sus búferes de una: con la geometría de acá son cien milisegundos leídos de
    /// golpe. Si en la cola había un bloque de veinte, los otros ochenta salen de
    /// <c>ReadFully</c> —silencio— y ya sonaron para cuando llega el audio de verdad.
    /// <para>
    /// El colchón no le agrega demora al corte: <see cref="Flush"/> vacía la cola y resetea el
    /// dispositivo igual, y lo que se oye después de callarla no depende de cuánto había esperando.
    /// </para>
    /// </remarks>
    private void StartIfPrimedLocked(bool force)
    {
        if (_started || _output is null || _queue is null)
        {
            return;
        }

        var queued = LiveAudioFormat.OutputDurationOf(_queue.BufferedBytes);
        if (!force && !_playout.ShouldStart(queued, TurnClosed))
        {
            return;
        }

        if (queued <= TimeSpan.Zero)
        {
            return;
        }

        // La cuenta se abre antes del play: la primera lectura del driver puede llegar mientras Play
        // todavía no volvió, y una lectura fuera de la cuenta deja el reparto corrido para siempre.
        // El reloj no arranca acá sino en esa primera lectura — entre el play y ella hay un despacho
        // del ThreadPool que se midió tardando 56 ms, y contarlos como atraso es informar un hueco
        // que no sonó.
        _playout.NoteStarted();
        _output.Play();
        _started = true;
        DisarmPrimeDeadlineLocked();
    }

    /// <summary>
    /// Pone el plazo una sola vez por respuesta.
    /// </summary>
    /// <remarks>
    /// Una sola vez y no por bloque: rearmarlo en cada bloque haría que un servidor que manda el
    /// audio a cuentagotas lo corriera para adelante indefinidamente, que es justamente el caso
    /// contra el que está.
    /// </remarks>
    private void ArmPrimeDeadlineLocked()
    {
        if (_primeArmed)
        {
            return;
        }

        _primeArmed = true;
        (_primeTimer ??= new System.Threading.Timer(OnPrimeDeadline, null, Timeout.Infinite, Timeout.Infinite))
            .Change(PrimeDeadline, Timeout.InfiniteTimeSpan);
    }

    private void DisarmPrimeDeadlineLocked()
    {
        _primeArmed = false;
        _primeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnPrimeDeadline(object? state)
    {
        lock (_gate)
        {
            // El plazo ya se cumplió: se baja la marca acá y no adentro del arranque. Si se dejara
            // que la bajara sólo el arranque exitoso, un plazo que llega con el dispositivo cerrado
            // dejaría la marca puesta para siempre y la respuesta siguiente se quedaría sin red de
            // seguridad, en silencio, sin que nada lo explique.
            _primeArmed = false;

            if (_disposed)
            {
                return;
            }

            StartIfPrimedLocked(force: true);
        }
    }

    /// <summary>
    /// Tira lo que está sonando y lo que está encolado, ahora.
    /// </summary>
    /// <remarks>
    /// Son <b>dos</b> pasos y hacen falta los dos. <c>ClearBuffer</c> vacía nuestra cola, pero el
    /// driver ya tiene búferes en la mano y esos siguen sonando; <c>Stop</c> los tira. Implementar
    /// esto como «dejá de encolar» es el bug clásico de esta API y desde afuera no se ve como un
    /// problema de audio: se ve como que no escucha.
    /// </remarks>
    public void Flush()
    {
        lock (_gate)
        {
            FlushCount++;

            if (_disposed)
            {
                return;
            }

            _queue?.ClearBuffer();

            // Stop tira los búferes que el driver tenía en la mano, así que el margen que llevaba
            // contado deja de existir en el mismo instante. Sin esto, callarla dejaba a Pending
            // diciendo que todavía se la oía durante otros cien milisegundos.
            _tap?.Silence();

            try
            {
                _output?.Stop();
            }
            catch (Exception)
            {
                // Callar algo que ya estaba callado no es un error.
            }

            // La próxima cosa que se encole vuelve a arrancar la reproducción. Dejarlo en marcha
            // sobre una cola vacía haría que el primer bloque del turno nuevo saliera cortado.
            _started = false;
            TurnClosed = false;
            _playout.NoteStopped();
            DisarmPrimeDeadlineLocked();
        }
    }

    /// <summary>
    /// El turno cerró: no viene más audio.
    /// </summary>
    /// <remarks>
    /// Lo encolado tiene que <em>terminar de sonar</em>: confundir esto con <see cref="Flush"/>
    /// corta la última palabra de todas las respuestas.
    /// <para>
    /// Lo único que hace es anotar que no viene más, y eso decide dos cosas. Una: una respuesta de
    /// una palabra nunca junta el colchón de <see cref="LivePlayout"/>, así que si nadie avisara que
    /// se terminó, se quedaría esperando un audio que ya no existe. Dos: cuando la cola se vacía con
    /// el turno cerrado, ese silencio es el final de la respuesta y no un corte — sin esta marca, el
    /// final de cada frase se anotaría en la bitácora como un hueco.
    /// </para>
    /// </remarks>
    public ValueTask CompleteTurnAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            TurnClosed = true;
            _playout.NoteTurnEnded();
            StartIfPrimedLocked(force: false);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Cierra el dispositivo. La sesión que vuelva a encolar lo abre de nuevo.</summary>
    public void Close()
    {
        WaveOutEvent? output;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            output = _output;
            timer = _primeTimer;
            _output = null;
            _queue = null;
            _tap = null;
            _primeTimer = null;
            _primeArmed = false;
            _started = false;
            TurnClosed = false;
            _playout.NoteStopped();
        }

        // El reloj se suelta fuera del candado: su devolución de llamada lo toma, y desecharlo
        // adentro es cómo se arma un abrazo mortal con un temporizador.
        timer?.Dispose();

        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch (Exception)
        {
            // Idem.
        }

        output.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Close();
    }

    /// <summary>
    /// Abre el dispositivo si hace falta. Devuelve si quedó abierto.
    /// </summary>
    /// <remarks>
    /// Que no se pueda abrir el parlante no puede tirar la sesión: el bucle que llama a esto es el
    /// que lee del servidor. Se devuelve <c>false</c> y la charla sigue —muda, pero con la
    /// transcripción llegando— en vez de morirse en un hilo del pool.
    /// </remarks>
    private bool EnsureOpen()
    {
        if (_output is not null)
        {
            return true;
        }

        try
        {
            var queue = new BufferedWaveProvider(Format)
            {
                BufferDuration = QueueCapacity,

                // Nunca lanza: quien encola es el bucle de lectura del servidor y una excepción ahí
                // se lleva puesta la sesión entera por un búfer lleno.
                DiscardOnBufferOverflow = true,

                // Cuando la cola se queda corta un instante, devolver silencio en vez de cero
                // muestras. Con cero, WinMM da la reproducción por terminada y el resto del turno
                // sale a los tirones.
                ReadFully = true
            };

            var output = new WaveOutEvent
            {
                DesiredLatency = (int)DriverLatency.TotalMilliseconds,
                NumberOfBuffers = BufferCount
            };

            // El driver lee por acá y no de la cola directamente: es el único lugar desde donde se
            // ve cuándo se llevó audio de verdad, que es lo que Pending necesita saber.
            var tap = new DriverTap(queue, this);
            output.Init(tap);

            _queue = queue;
            _tap = tap;
            _output = output;
            _started = false;
            return true;
        }
        catch (Exception)
        {
            _queue = null;
            _tap = null;
            _output = null;
            return false;
        }
    }

    /// <summary>
    /// El driver leyó: se le cuenta a <see cref="LivePlayout"/> y se avisa si hubo un hueco.
    /// </summary>
    /// <remarks>
    /// <b>No toma el candado de la salida.</b> Corre en el hilo del driver, y ese candado se
    /// sostiene mientras se abre el dispositivo y mientras se lo resetea: pedirlo desde acá es cómo
    /// se traba una reproducción. <see cref="LivePlayout"/> lleva el suyo, que sólo protege
    /// aritmética.
    /// </remarks>
    private void NoteDriverRead(int available, int requested, long timestamp)
    {
        if (_playout.NoteRead(available, requested, timestamp) is not { } gap)
        {
            return;
        }

        try
        {
            GapHeard?.Invoke(this, new LiveAudioGapEventArgs(gap));
        }
        catch (Exception)
        {
            // Un oyente roto no puede cortar la reproducción, y menos desde el hilo del driver.
        }
    }

    /// <summary>
    /// El caño por el que el driver se lleva el audio, con la cuenta de hasta cuándo se sigue oyendo.
    /// </summary>
    /// <remarks>
    /// No transforma nada: pasa la lectura tal cual. Está para poder anotar el instante en que el
    /// driver se llevó muestras de verdad —las que había en la cola antes de leer— y no las de
    /// relleno que <c>ReadFully</c> agrega cuando la cola se queda corta. Desde ese instante, lo que
    /// se llevó tarda como mucho <see cref="DriverLatency"/> en sonar.
    /// <para>
    /// <c>Read</c> corre en el hilo del driver y <c>Remaining</c> se lee desde el que pregunta por
    /// el orbe: el instante viaja en un <c>long</c> con lectura y escritura atómicas y sin candado,
    /// porque tomar el candado de la salida adentro del hilo del driver es cómo se traba una
    /// reproducción.
    /// </para>
    /// <para>
    /// Y es además el único lugar donde se puede ver que el parlante se quedó sin audio: con
    /// <c>ReadFully</c> el hueco se rellena con silencio y no lo denuncia nadie. Por eso cada lectura
    /// se le cuenta al dueño.
    /// </para>
    /// </remarks>
    private sealed class DriverTap(BufferedWaveProvider queue, LiveSpeakerSink owner) : IWaveProvider
    {
        private long _audibleUntil;

        public WaveFormat WaveFormat => queue.WaveFormat;

        /// <summary>Cuánto falta para que termine de sonar lo último que se llevó el driver.</summary>
        public TimeSpan Remaining
        {
            get
            {
                var deadline = Volatile.Read(ref _audibleUntil);
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                return deadline <= now
                    ? TimeSpan.Zero
                    : System.Diagnostics.Stopwatch.GetElapsedTime(now, deadline);
            }
        }

        /// <summary>Lo que el driver tenía se tiró: ya no queda nada por oírse.</summary>
        public void Silence() => Volatile.Write(ref _audibleUntil, 0);

        public int Read(byte[] buffer, int offset, int count)
        {
            var real = queue.BufferedBytes;
            var read = queue.Read(buffer, offset, count);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (real > 0)
            {
                Volatile.Write(
                    ref _audibleUntil,
                    now + (long)(DriverLatency.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
            }

            owner.NoteDriverRead(real, count, now);
            return read;
        }
    }
}
