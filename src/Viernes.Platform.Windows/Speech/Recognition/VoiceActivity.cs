namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Veredicto de un detector de voz sobre un bloque de audio.
/// </summary>
/// <remarks>
/// <see cref="Level"/> va aparte de <see cref="Confidence"/> a propósito: el nivel es para que la
/// interfaz se mueva con la voz y tiene que reaccionar aunque el detector diga que no es voz —hay
/// que poder ver que algo entra por el micrófono antes de que califique—, mientras que la confianza
/// es lo que se compara entre detectores.
/// </remarks>
public readonly record struct VoiceActivityDecision(bool IsVoice, double Confidence, double Level);

/// <summary>Quién decidió y con qué; se muestra en el banco de medición.</summary>
public sealed record VoiceActivityDetectorInfo(string Name, bool IsTrainedModel, string Description);

/// <summary>
/// Decide, bloque por bloque, si lo que entra es voz humana o cualquier otra cosa.
/// </summary>
/// <remarks>
/// Está detrás de una interfaz porque hay dos implementaciones que conviven: la heurística de
/// siempre —energía, banda y forma de la señal— y un modelo entrenado. Poder cambiar una por otra
/// sin tocar la captura es lo que permite medir cuál acierta más antes de confiar en la nueva; ver
/// <see cref="VoiceActivityScoreboard"/>.
/// </remarks>
public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>Quién es este detector, para poder decirlo en un informe.</summary>
    VoiceActivityDetectorInfo Info { get; }

    /// <summary>
    /// Juzga un bloque de muestras de 16 bits.
    /// </summary>
    /// <param name="samples">Las muestras del bloque, mono.</param>
    /// <param name="insideUtterance">
    /// Si en este momento ya se dio por empezada una frase. Importa para los detectores que estiman
    /// el ruido de fondo: mientras sabemos que hay alguien hablando, nada de lo que llega es
    /// ambiente.
    /// </param>
    /// <returns>El veredicto para ese bloque.</returns>
    VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance);

    /// <summary>Vuelve al estado de arranque, sin perder lo aprendido del cuarto.</summary>
    void Reset();
}
