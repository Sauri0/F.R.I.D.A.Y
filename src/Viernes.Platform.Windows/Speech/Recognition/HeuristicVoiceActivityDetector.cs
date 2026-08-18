namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// El detector de siempre, ahora aparte de la captura y con una prueba que lo cubre.
/// </summary>
/// <remarks>
/// Es exactamente la lógica que venía adentro de la sesión de captura —energía sobre el piso de
/// ruido más la banda de cruces por cero— con una sola cosa nueva: la inclinación espectral. Se
/// agregó porque la banda de cruces por cero <em>no</em> alcanza para lo que el usuario reportó.
/// <para>
/// El caso concreto: algo pesado que se cae y resuena. A 16 kHz, un golpe que resuena en 60 Hz cruza
/// el cero unas 3,6 veces por bloque de 30 ms, o sea una tasa de 0,0075 — <b>adentro</b> de la banda
/// de voz 0,005–0,25. La tasa de cruces no puede distinguirlo: mide con qué frecuencia cambia de
/// signo, y un tono grave lo hace poco, igual que una vocal grave.
/// </para>
/// <para>
/// La inclinación sí lo separa, y sale casi gratis: la energía de la primera diferencia dividida por
/// la energía total es una medida del centro de gravedad del espectro. Para un tono de 60 Hz da
/// 0,00055; para la voz, cuyo primer formante vive entre 300 y 800 Hz, da entre 0,014 y 0,09; para
/// ruido blanco —un aplauso, siseo, teclas— tiende a 2, porque diferenciar ruido blanco duplica su
/// potencia. Pedir que caiga entre 0,003 y 1,2 deja pasar la voz y deja afuera los dos extremos.
/// </para>
/// <para>
/// Aun así esto sigue siendo una heurística y el propio usuario tiene razón en desconfiar: un objeto
/// que resuena con armónicos en la banda media puede pasar. Para eso está
/// <see cref="SileroVoiceActivityDetector"/>; esto es el respaldo cuando el modelo no está.
/// </para>
/// </remarks>
public sealed class HeuristicVoiceActivityDetector : IVoiceActivityDetector
{
    /// <summary>
    /// Piso de ruido del cuarto, compartido entre capturas.
    /// </summary>
    /// <remarks>
    /// Era un campo de instancia y se crea un detector por captura, así que arrancaba en cero en
    /// cada turno: el piso se aprendía de nuevo desde el silencio absoluto mientras el cuarto ya
    /// sonaba. Con la tele puesta, el umbral quedaba por debajo del ruido, la voz se daba por
    /// detectada de entrada y se grababa el cuarto entero. Estático porque el cuarto es uno solo:
    /// lo que se aprendió del ambiente en el turno anterior sigue siendo cierto en el siguiente.
    /// </remarks>
    private static double _sharedNoiseFloor;

    private double _noiseFloor = Volatile.Read(ref _sharedNoiseFloor);
    private short _lastSample;

    /// <summary>Banda de la voz en cruces por cero a 16 kHz. Fuera de acá es ruido, por fuerte que suene.</summary>
    private const double MinimumZeroCrossingRate = 0.005;

    private const double MaximumZeroCrossingRate = 0.25;

    /// <summary>
    /// Inclinación espectral mínima: por debajo es un retumbe grave, no una voz.
    /// </summary>
    /// <remarks>
    /// Para un tono puro la inclinación vale 4·sin²(π·f/fs), así que este 0,003 equivale a un centro
    /// de gravedad de unos 139 Hz. Está elegido con margen hacia los dos lados: un retumbe de 60 Hz
    /// da 0,00055 —cinco veces menos— y uno de 120 Hz da 0,0022, así que quedan afuera; la voz
    /// concentra su energía en el primer formante, entre 300 y 800 Hz, que da entre 0,014 y 0,09.
    /// Poner el corte en 0,01 —el equivalente a 250 Hz— habría dejado afuera una voz grave hablando
    /// bajo, que es exactamente el error que no se puede cometer.
    /// </remarks>
    private const double MinimumSpectralTilt = 0.003;

    /// <summary>Inclinación espectral máxima: por encima es siseo, teclas o un aplauso.</summary>
    private const double MaximumSpectralTilt = 1.2;

    public VoiceActivityDetectorInfo Info { get; } = new(
        "Heurística local",
        IsTrainedModel: false,
        "Energía sobre el piso de ruido medido, banda de cruces por cero e inclinación espectral. " +
        "No usa modelo ni red: corre en microsegundos y no necesita nada instalado.");

    /// <summary>Piso de ruido estimado ahora mismo. Lo mira el banco de medición.</summary>
    public double NoiseFloor => _noiseFloor;

    /// <summary>Umbral que separa voz de fondo con el piso actual.</summary>
    public double Threshold => NoiseFloorTracker.ThresholdFor(_noiseFloor);

    public VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance)
    {
        if (samples.Length == 0)
        {
            return new VoiceActivityDecision(false, 0, 0);
        }

        double squareSum = 0;
        double differenceSquareSum = 0;
        var zeroCrossings = 0;
        var previousSample = _lastSample;
        var previousNormalized = _lastSample / 32768d;
        foreach (var sample in samples)
        {
            var normalized = sample / 32768d;
            squareSum += normalized * normalized;

            var difference = normalized - previousNormalized;
            differenceSquareSum += difference * difference;
            previousNormalized = normalized;

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
        var rootMeanSquare = Math.Sqrt(squareSum / samples.Length);
        var zeroCrossingRate = (double)zeroCrossings / samples.Length;
        var spectralTilt = squareSum > 0 ? differenceSquareSum / squareSum : 0;

        // El umbral se evalúa con el piso previo: la muestra que se está juzgando no puede
        // participar de su propio criterio.
        var threshold = NoiseFloorTracker.ThresholdFor(_noiseFloor);
        _noiseFloor = NoiseFloorTracker.Advance(_noiseFloor, rootMeanSquare, insideUtterance);
        Volatile.Write(ref _sharedNoiseFloor, _noiseFloor);

        var soundsLikeVoice =
            zeroCrossingRate is >= MinimumZeroCrossingRate and <= MaximumZeroCrossingRate &&
            spectralTilt is >= MinimumSpectralTilt and <= MaximumSpectralTilt;
        var isVoice = rootMeanSquare >= threshold && soundsLikeVoice;

        // La raíz comprime el rango: entre un susurro y un grito hay dos órdenes de magnitud, y en
        // lineal la forma se queda pegada al máximo apenas abrís la boca. Se mide contra el umbral
        // —que ya lleva el piso adentro— para que reaccione igual con cualquier micrófono.
        var level = Math.Clamp(Math.Sqrt(rootMeanSquare / (threshold * 20)), 0, 1);
        var confidence = soundsLikeVoice
            ? Math.Clamp(rootMeanSquare / (threshold * 2), 0, 1)
            : 0;

        return new VoiceActivityDecision(isVoice, confidence, level);
    }

    /// <summary>
    /// Vuelve al arranque de una frase sin olvidar el cuarto.
    /// </summary>
    /// <remarks>
    /// La última muestra sí se olvida —pertenece a un audio que ya terminó— pero el piso de ruido
    /// no: volver a aprenderlo desde cero en cada frase es justo el error que se arregló haciéndolo
    /// compartido.
    /// </remarks>
    public void Reset() => _lastSample = 0;

    public void Dispose()
    {
        // No hay nada nativo que soltar; existe para que la interfaz sirva también para el modelo.
    }
}
