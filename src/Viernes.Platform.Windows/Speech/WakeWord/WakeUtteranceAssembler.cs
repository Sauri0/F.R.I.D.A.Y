using System.Buffers.Binary;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>La frase entera ya armada, lista para transcribir.</summary>
public sealed record AssembledUtterance(
    Stream Wave,
    string Phrase,
    float Confidence,
    TimeSpan PreRollDuration,
    TimeSpan TailDuration,
    UtteranceStopReason StopReason,
    DateTimeOffset DetectedAt)
{
    /// <summary>Cuánto audio se entrega en total, antes y después del nombre.</summary>
    public TimeSpan Duration => PreRollDuration + TailDuration;
}

/// <summary>
/// El corazón del oído: junta lo que se dijo antes del nombre con lo que se dijo después.
/// </summary>
/// <remarks>
/// Vive aparte del <see cref="ContinuousWakeListener"/> por una razón muy concreta: mezclado con el
/// micrófono y con SAPI, esto sólo se podía probar hablándole a la máquina, y lo que puede salir mal
/// —que la juntura repita medio segundo, que se pierda un bloque, que el recorte se lleve la
/// conversación de recién en vez de la frase actual— es invisible al oído. Whisper transcribe lo que
/// le den y nadie se entera.
/// <para>
/// Acá adentro no hay dispositivo ni reconocedor: entran bloques de bytes y sale un WAV. Eso se
/// prueba con un ramp de bytes y comprobando que el resultado siga siendo un ramp continuo, que es
/// la forma exacta de decir «ni se perdió ni se repitió nada en la juntura».
/// </para>
/// </remarks>
public sealed class WakeUtteranceAssembler
{
    private readonly object _sync = new();
    private readonly IVoiceActivityDetector _detector;
    private readonly UtteranceEndpointer _endpointer;
    private readonly RollingAudioBuffer _buffer;
    private readonly TimeSpan _preRoll;
    private readonly int _sampleRate;
    private readonly int _bitsPerSample;
    private readonly int _channels;
    private readonly int _bytesPerSecond;

    /// <summary>
    /// Cuánto silencio corta una tanda de habla. Debajo de esto es un micro-silencio de los que hay
    /// adentro de cualquier frase; encima, es que empezó a decir otra cosa.
    /// </summary>
    private static readonly TimeSpan SpeechRunGap = TimeSpan.FromMilliseconds(700);

    /// <summary>Colchón antes del comienzo detectado, para no comerse la primera consonante.</summary>
    private static readonly TimeSpan PreRollMargin = TimeSpan.FromMilliseconds(400);

    /// <summary>Piso del recorte: aunque diga sólo el nombre, algo de contexto se manda igual.</summary>
    private static readonly TimeSpan MinimumPreRoll = TimeSpan.FromMilliseconds(1500);

    private short[] _samples = new short[1024];
    private UtteranceTail? _tail;
    private byte[] _pendingPreRoll = [];
    private long _speechRunBytes;
    private TimeSpan _silenceRun;
    private string _phrase = string.Empty;
    private float _confidence;
    private TimeSpan _preRollDuration;
    private DateTimeOffset _detectedAt;

    /// <summary>
    /// Arma el ensamblador con los plazos dados y el detector que decida qué es voz.
    /// </summary>
    public WakeUtteranceAssembler(
        ContinuousWakeListenerOptions options,
        IVoiceActivityDetector detector,
        int sampleRate = 16_000,
        int bitsPerSample = 16,
        int channels = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _endpointer = new UtteranceEndpointer(options.Endpointer);
        _preRoll = options.PreRoll;
        _sampleRate = sampleRate;
        _bitsPerSample = bitsPerSample;
        _channels = channels;
        _bytesPerSecond = sampleRate * channels * bitsPerSample / 8;
        _buffer = new RollingAudioBuffer(options.PreRoll, _bytesPerSecond, channels * bitsPerSample / 8);
    }

    /// <summary>Si en este momento se está grabando la cola de una frase.</summary>
    public bool IsCapturing
    {
        get
        {
            lock (_sync)
            {
                return _tail is not null;
            }
        }
    }

    /// <summary>Lo último que dijo el detector, para el indicador de la interfaz.</summary>
    public VoiceActivityDecision LastDecision { get; private set; }

    /// <summary>
    /// Procesa un bloque de audio recién capturado.
    /// </summary>
    /// <param name="block">El PCM tal como vino de la captura.</param>
    /// <returns>La frase completa si con este bloque se cerró; <c>null</c> mientras siga abierta.</returns>
    public AssembledUtterance? Write(ReadOnlySpan<byte> block)
    {
        if (block.Length == 0)
        {
            return null;
        }

        var position = _buffer.Write(block);
        var sampleCount = block.Length / sizeof(short);
        if (_samples.Length < sampleCount)
        {
            _samples = new short[sampleCount];
        }

        for (var index = 0; index < sampleCount; index++)
        {
            _samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                block.Slice(index * sizeof(short), sizeof(short)));
        }

        var capturing = IsCapturing;
        var decision = _detector.Analyze(_samples.AsSpan(0, sampleCount), capturing);
        LastDecision = decision;

        var frameDuration = WaveAudio.Duration(block.Length, _sampleRate, _bitsPerSample, _channels);
        TrackSpeechRun(decision.IsVoice, block.Length, frameDuration);

        if (!capturing)
        {
            return null;
        }

        lock (_sync)
        {
            _tail?.Append(position, block);
        }

        return _endpointer.Observe(decision.IsVoice, frameDuration) == UtteranceStopReason.None
            ? null
            : Finish();
    }

    /// <summary>
    /// Avisa que el reconocedor oyó el nombre. A partir de acá se graba la cola.
    /// </summary>
    /// <param name="phrase">La frase que se oyó.</param>
    /// <param name="confidence">Con cuánta confianza.</param>
    /// <returns><c>true</c> si esto abrió una captura nueva.</returns>
    public bool NameHeard(string phrase, float confidence)
    {
        lock (_sync)
        {
            if (_tail is not null)
            {
                // Ya está grabando: repetir el nombre en medio de la frase no reinicia nada.
                return false;
            }

            // Lo que ya venía diciendo, más un colchón, tope en la ventana. El piso existe para el
            // caso de decir sólo el nombre: mandarle al modelo medio segundo con «Viernes» y nada
            // más no le da con qué decidir.
            var spoken = WaveAudio.Duration(_speechRunBytes, _sampleRate, _bitsPerSample, _channels)
                + PreRollMargin;
            var lookback = spoken < MinimumPreRoll ? MinimumPreRoll : spoken;
            if (lookback > _preRoll)
            {
                lookback = _preRoll;
            }

            var snapshot = _buffer.Snapshot(lookback);
            _endpointer.Reset();
            _tail = new UtteranceTail(snapshot.EndPosition);
            _pendingPreRoll = snapshot.Pcm;
            _preRollDuration = snapshot.Duration;
            _phrase = phrase;
            _confidence = confidence;
            _detectedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>Tira lo grabado y vuelve a esperar el nombre.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _tail = null;
            _pendingPreRoll = [];
            _speechRunBytes = 0;
            _silenceRun = TimeSpan.Zero;
        }

        _endpointer.Reset();
        _detector.Reset();
        _buffer.Clear();
    }

    /// <summary>
    /// Lleva la cuenta de cuánto viene hablando sin pausa, que es lo que define cuánto rescatar.
    /// </summary>
    /// <remarks>
    /// Sin esto habría que mandar los diez segundos siempre, y en un cuarto con la tele puesta eso
    /// es diez segundos de tele adelante del pedido. Con esto, el recorte llega hasta donde arrancó
    /// esta tanda de habla y ni un segundo más.
    /// </remarks>
    private void TrackSpeechRun(bool isVoice, int bytes, TimeSpan frameDuration)
    {
        lock (_sync)
        {
            if (isVoice)
            {
                _silenceRun = TimeSpan.Zero;
                _speechRunBytes += bytes;
                return;
            }

            _silenceRun += frameDuration;
            if (_silenceRun >= SpeechRunGap)
            {
                _speechRunBytes = 0;
                return;
            }

            // Adentro de un micro-silencio se sigue sumando: si no, el recorte cortaría entre
            // palabras de la misma frase.
            _speechRunBytes += bytes;
        }
    }

    private AssembledUtterance Finish()
    {
        byte[] preRoll;
        byte[] tail;
        string phrase;
        float confidence;
        TimeSpan preRollDuration;
        DateTimeOffset detectedAt;
        lock (_sync)
        {
            preRoll = _pendingPreRoll;
            tail = _tail?.ToArray() ?? [];
            phrase = _phrase;
            confidence = _confidence;
            preRollDuration = _preRollDuration;
            detectedAt = _detectedAt;
            _tail = null;
            _pendingPreRoll = [];
        }

        return new AssembledUtterance(
            WaveAudio.CreateWave([preRoll, tail], _sampleRate, _bitsPerSample, _channels),
            phrase,
            confidence,
            preRollDuration,
            WaveAudio.Duration(tail.Length, _sampleRate, _bitsPerSample, _channels),
            _endpointer.StopReason,
            detectedAt);
    }
}
