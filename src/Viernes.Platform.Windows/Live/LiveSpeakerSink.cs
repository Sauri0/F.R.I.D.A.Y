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
                DesiredLatency = BufferMilliseconds * BufferCount,
                NumberOfBuffers = BufferCount
            };

            output.Init(queue);

            _queue = queue;
            _output = output;
            _started = false;
            return true;
        }
        catch (Exception)
        {
            _queue = null;
            _output = null;
            return false;
        }
    }
}
