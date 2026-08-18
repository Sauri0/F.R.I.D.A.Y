namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Detector de voz apoyado en un modelo entrenado que corre local, sin costo por uso.
/// </summary>
/// <remarks>
/// El objetivo textual del usuario era «solo debería entender voz no sonidos, si aplaudo o se cae
/// algo no debería interrumpirse». Ninguna combinación de energía y forma de onda resuelve eso del
/// todo: un objeto que resuena con armónicos en la banda media tiene la misma firma que una vocal.
/// Lo que sí lo resuelve es un modelo que vio millones de ejemplos de las dos cosas.
/// <para>
/// Silero VAD es el candidato natural: pesa poco más de un mega, corre en CPU en microsegundos por
/// ventana, es local y no cobra. Trabaja en ventanas fijas —512 muestras a 16 kHz, unos 32 ms— y
/// arrastra estado recurrente entre ventanas, que es lo que le permite distinguir un golpe de una
/// sílaba: mira la evolución, no un instante.
/// </para>
/// <para>
/// Los bloques de la captura son de 30 ms y las ventanas del modelo de 32: no coinciden. Por eso
/// esta clase acumula y sólo consulta el modelo cuando junta una ventana entera; mientras tanto
/// devuelve el último veredicto. Redondear a la fuerza el tamaño del bloque para que coincidiera
/// habría cambiado la cadencia de toda la captura por comodidad de esta clase.
/// </para>
/// <para>
/// La histéresis —entrar en 0,5 y salir recién en 0,35— es la recomendación del propio proyecto y
/// existe por la misma razón que en un termostato: con un solo umbral, una probabilidad que oscila
/// alrededor de él prende y apaga la voz varias veces por segundo, y el fin de frase se dispara en
/// medio de una palabra.
/// </para>
/// </remarks>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly IVadModelRunner _runner;
    private readonly double _startThreshold;
    private readonly double _stopThreshold;
    private readonly float[] _window;
    private readonly HeuristicVoiceActivityDetector _levels = new();
    private int _filled;
    private bool _isVoice;
    private double _lastProbability;
    private bool _disposed;

    /// <summary>
    /// Arma el detector sobre un modelo ya cargado, con los umbrales recomendados.
    /// </summary>
    public SileroVoiceActivityDetector(IVadModelRunner runner, double startThreshold = 0.5, double stopThreshold = 0.35)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (startThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startThreshold));
        }

        if (stopThreshold <= 0 || stopThreshold > startThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(stopThreshold));
        }

        _runner = runner;
        _startThreshold = startThreshold;
        _stopThreshold = stopThreshold;
        _window = new float[runner.WindowSamples];
    }

    public VoiceActivityDetectorInfo Info { get; } = new(
        "Silero VAD (ONNX local)",
        IsTrainedModel: true,
        "Red entrenada que corre en CPU dentro del equipo. No sube audio y no tiene costo por uso.");

    /// <summary>Última probabilidad que devolvió el modelo. Se muestra en la comparación.</summary>
    public double LastProbability => _lastProbability;

    public VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance)
    {
        // El nivel para la interfaz lo sigue calculando la heurística: el modelo dice si hay voz,
        // no cuán fuerte se oye, y la forma en pantalla tiene que moverse con el volumen igual.
        var reference = _levels.Analyze(samples, insideUtterance);

        foreach (var sample in samples)
        {
            _window[_filled++] = sample / 32768f;
            if (_filled < _window.Length)
            {
                continue;
            }

            _filled = 0;
            var probability = _runner.Probability(_window);
            _lastProbability = float.IsFinite(probability) ? Math.Clamp(probability, 0f, 1f) : 0;
            _isVoice = _isVoice
                ? _lastProbability >= _stopThreshold
                : _lastProbability >= _startThreshold;
        }

        return new VoiceActivityDecision(_isVoice, _lastProbability, reference.Level);
    }

    public void Reset()
    {
        _filled = 0;
        _isVoice = false;
        _lastProbability = 0;
        _runner.Reset();
        _levels.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runner.Dispose();
        _levels.Dispose();
    }
}
