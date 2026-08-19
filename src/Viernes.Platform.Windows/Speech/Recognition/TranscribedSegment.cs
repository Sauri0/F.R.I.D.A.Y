namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>Una palabra con dónde cae adentro del audio.</summary>
/// <param name="Text">La palabra, sin el espacio de adelante.</param>
/// <param name="Start">Desde qué momento del audio.</param>
/// <param name="End">Hasta qué momento.</param>
public readonly record struct TimedWord(string Text, TimeSpan Start, TimeSpan End);

/// <summary>Un tramo de lo transcripto, con dónde cae adentro del audio.</summary>
/// <param name="Text">Lo que se dijo en ese tramo, ya recortado.</param>
/// <param name="Start">Desde qué momento del audio.</param>
/// <param name="End">Hasta qué momento.</param>
/// <param name="Probability">Cuánta confianza le tuvo el modelo.</param>
/// <param name="Words">
/// Las palabras del tramo con su propio horario, si el modelo las dio. Vacío si no.
/// </param>
/// <remarks>
/// Los tramos de Whisper no son frases: <b>medido con «Estaba pensando en el asado. Che Viernes,
/// anotá que falta carbón», que tiene un punto en el medio, salió un solo tramo de 0 a 4,92 s</b>.
/// O sea que cortar por tramo es no cortar nunca, justo en el caso para el que existe el corte. Por
/// eso hacen falta las palabras.
/// </remarks>
public readonly record struct TranscribedSegment(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    float Probability,
    IReadOnlyList<TimedWord> Words)
{
    /// <summary>El mismo tramo sin palabras sueltas, para quien no las necesita.</summary>
    public TranscribedSegment(string text, TimeSpan start, TimeSpan end, float probability)
        : this(text, start, end, probability, [])
    {
    }
}

/// <summary>
/// Lo transcripto de un WAV, entero y también tramo por tramo.
/// </summary>
/// <remarks>
/// Los tiempos existen por una sola razón: el oído continuo entrega un WAV que empieza <em>antes</em>
/// de que sonara el nombre, y lo de antes se dibuja distinto —al 40 %, porque no te lo dijeron a
/// vos—. Sin saber dónde cae cada tramo, la única forma de separarlo sería transcribir dos veces, y
/// transcribir dos veces es esperar dos veces.
/// </remarks>
public sealed record WaveTranscription(
    SpeechRecognitionResult Result,
    IReadOnlyList<TranscribedSegment> Segments);
