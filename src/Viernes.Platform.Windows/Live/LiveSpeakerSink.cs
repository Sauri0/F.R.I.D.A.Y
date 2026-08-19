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
/// <b>Todo lo que sigue existe para que <see cref="Flush"/> se oiga en el acto.</b> La transición
/// del orbe de hablando a interrumpida dura 80 ms y es un corte, no un fundido: si el audio tarda
/// más que eso en callarse, la persona ve el corte y lo sigue escuchando, que se lee como que no
/// hizo caso.
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

    private readonly Lock _gate = new();
    private BufferedWaveProvider? _queue;
    private DriverTap? _tap;
    private WaveOutEvent? _output;
    private bool _started;
    private bool _disposed;

    /// <summary>Cuántas veces la mandaron a callar. Para la bitácora y el diagnóstico.</summary>
    public int FlushCount { get; private set; }

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

            if (!_started)
            {
                _output!.Play();
                _started = true;
            }
        }

        return ValueTask.CompletedTask;
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
        }
    }

    /// <summary>
    /// El turno cerró: no viene más audio.
    /// </summary>
    /// <remarks>
    /// No hace nada, y eso es lo correcto: lo encolado tiene que <em>terminar de sonar</em>.
    /// Confundir esto con <see cref="Flush"/> corta la última palabra de todas las respuestas.
    /// </remarks>
    public ValueTask CompleteTurnAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Cierra el dispositivo. La sesión que vuelva a encolar lo abre de nuevo.</summary>
    public void Close()
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            output = _output;
            _output = null;
            _queue = null;
            _tap = null;
            _started = false;
        }

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
            var tap = new DriverTap(queue);
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
    /// </remarks>
    private sealed class DriverTap(BufferedWaveProvider queue) : IWaveProvider
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
            if (real > 0)
            {
                Volatile.Write(
                    ref _audibleUntil,
                    System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(DriverLatency.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
            }

            return read;
        }
    }
}
