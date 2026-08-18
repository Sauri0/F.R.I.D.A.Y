using System.Globalization;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// Cómo escucha el oído continuo: qué nombres, cuánto recuerda y con qué detector de voz.
/// </summary>
public sealed record ContinuousWakeListenerOptions
{
    /// <summary>Los nombres que la despiertan.</summary>
    public IReadOnlyList<string> Phrases { get; init; } = ["Viernes", "Hola Viernes", "Che Viernes"];

    /// <summary>Idioma del reconocedor de nombre.</summary>
    public string RecognitionCulture { get; init; } = "es-AR";

    /// <summary>
    /// Confianza mínima para dar por oído el nombre.
    /// </summary>
    /// <remarks>
    /// Medido en este equipo: las detecciones reales entran entre 0,61 y 0,72, con media 0,69. El
    /// umbral va en 0,60 porque por encima de eso empieza a dejar afuera a la persona, y los falsos
    /// positivos ya no se controlan con el umbral sino mandando la frase entera al modelo.
    /// </remarks>
    public float MinimumConfidence { get; init; } = 0.60f;

    /// <summary>
    /// Cuánto audio anterior al nombre se guarda para poder mandarlo junto con lo que sigue.
    /// </summary>
    /// <remarks>
    /// Diez segundos: alcanza para «che, necesito que Viernes me abra Spotify» y para frases
    /// bastante más largas, y son 320 kB de memoria a 16 kHz mono de 16 bits. Guardar más no mejora
    /// nada porque el recorte se hace desde donde empezó la frase, no desde el principio de la
    /// ventana.
    /// </remarks>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Si hace falta decir dos palabras («Hola Viernes») en vez de sólo el nombre.
    /// </summary>
    /// <remarks>
    /// Apagado de fábrica: ver <see cref="WakePhrasePolicy"/> para el porqué. Queda disponible para
    /// quien prefiera el disparo más difícil.
    /// </remarks>
    public bool RequireCompoundPhrase { get; init; }

    /// <summary>Plazos de fin de frase de la parte que se graba después del nombre.</summary>
    public UtteranceEndpointerOptions Endpointer { get; init; } = new()
    {
        // Después del nombre no hay que esperar ocho segundos a que arranque: o sigue hablando o ya
        // dijo todo lo que quería y está en el pre-roll.
        InitialSilenceTimeout = TimeSpan.FromSeconds(2),
        EndSilenceTimeout = TimeSpan.FromMilliseconds(850),
        MaximumDuration = TimeSpan.FromSeconds(20)
    };

    /// <summary>
    /// Dispositivo de entrada. <c>-1</c> es <c>WAVE_MAPPER</c>: el predeterminado de Windows.
    /// </summary>
    public int InputDeviceNumber { get; init; } = WhisperSpeechRecognitionOptions.DefaultInputDevice;

    /// <summary>Tamaño del bloque de captura; 30 ms es lo que ya usa el resto de la voz.</summary>
    public int BufferMilliseconds { get; init; } = 30;

    /// <summary>
    /// Si se intenta usar el detector de voz entrenado antes de caer en la heurística.
    /// </summary>
    public bool PreferTrainedVoiceDetector { get; init; } = true;

    /// <summary>
    /// Si el detector que no manda igual corre al lado para poder compararlos.
    /// </summary>
    /// <remarks>
    /// Cuesta unos microsegundos por bloque y es la única forma de saber cuál acierta más en el
    /// cuarto real antes de confiar en el nuevo.
    /// </remarks>
    public bool CompareVoiceDetectors { get; init; } = true;

    /// <summary>Ruta del modelo entrenado; si es nula se usa la carpeta predeterminada.</summary>
    public string? TrainedVoiceModelPath { get; init; }

    internal string[] ValidateAndNormalizePhrases()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RecognitionCulture);
        _ = CultureInfo.GetCultureInfo(RecognitionCulture);
        if (MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumConfidence));
        }

        if (PreRoll < TimeSpan.FromSeconds(1) || PreRoll > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(PreRoll));
        }

        if (InputDeviceNumber < -1 || BufferMilliseconds is < 20 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferMilliseconds));
        }

        Endpointer.Validate();

        if (Phrases is null || Phrases.Count is < 1 or > 8)
        {
            throw new ArgumentException("Configurá entre 1 y 8 frases de activación.", nameof(Phrases));
        }

        var normalized = Phrases.Select(WakePhrasePolicy.Normalize).ToArray();
        if (normalized.Any(phrase => phrase.Length is < 2 or > 40))
        {
            throw new ArgumentException("Cada frase debe contener entre 2 y 40 caracteres seguros.", nameof(Phrases));
        }

        return [.. normalized.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
