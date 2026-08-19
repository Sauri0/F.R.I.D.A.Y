using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>La frase transcripta, partida en lo que se dijo antes del nombre y lo que se dijo a él.</summary>
/// <param name="Recovered">Lo que ya se venía diciendo cuando sonó el nombre. Puede venir vacío.</param>
/// <param name="Spoken">El nombre y lo que siguió: el pedido.</param>
public readonly record struct SplitUtterance(string Recovered, string Spoken)
{
    /// <summary>Todo junto, en el orden en que se dijo.</summary>
    public string Full => string.IsNullOrEmpty(Recovered) ? Spoken : $"{Recovered} {Spoken}";
}

/// <summary>
/// Separa el tramo rescatado de la ventana rodante del pedido propiamente dicho.
/// </summary>
/// <remarks>
/// El oído continuo entrega un WAV que arranca <em>antes</em> de que sonara el nombre, y esa mitad
/// se dibuja distinta —al 40 %— porque no te la dijeron a vos. El corte cae donde termina el
/// pre-roll, que es donde el reconocedor oyó el nombre.
/// <para>
/// <b>Corta por palabra, no por tramo, y eso no es refinamiento.</b> La primera versión cortaba por
/// tramo de Whisper suponiendo que Whisper corta en los puntos; no corta. Medido con «Estaba
/// pensando en el asado. Che Viernes, anotá que falta carbón» —que tiene un punto justo donde va el
/// corte— salió <em>un solo tramo</em> de 0 a 4,92 s, o sea que no se rescataba nunca nada, justo en
/// el caso para el que existe todo esto. Los horarios por palabra los da el mismo pase de
/// transcripción, así que separar no cuesta una transcripción de más.
/// </para>
/// <para>
/// Si el modelo no diera horarios por palabra, se cae al tramo: se pierde precisión, no correctitud.
/// </para>
/// </remarks>
public static class UtteranceTranscript
{
    /// <summary>Parte la transcripción por dónde terminó el pre-roll.</summary>
    /// <param name="segments">Los tramos que devolvió el reconocedor, con sus tiempos.</param>
    /// <param name="preRoll">
    /// Cuánto audio anterior al nombre se rescató. <b>No son los diez segundos de la ventana</b>: el
    /// recorte llega hasta donde arrancó esa tanda de habla, así que con la tele puesta no se meten
    /// diez segundos de tele adelante del pedido.
    /// </param>
    /// <param name="phrase">
    /// El nombre con el que la llamaron, si se sabe. Sirve para que el nombre quede del lado del
    /// pedido: el reconocedor avisa <em>después</em> de que la palabra terminó, así que el corte cae
    /// justo detrás del nombre y sin esto «Viernes» se dibujaría como algo que no le dijeron a ella.
    /// </param>
    public static SplitUtterance Split(
        IReadOnlyList<TranscribedSegment>? segments,
        TimeSpan preRoll,
        string? phrase = null)
    {
        if (segments is null || segments.Count == 0)
        {
            return new SplitUtterance(string.Empty, string.Empty);
        }

        var recovered = new List<string>();
        var spoken = new List<string>();

        // Las piezas con las que se corta: palabras si el modelo las fechó, y si no los tramos.
        var pieces = segments
            .SelectMany(segment => segment.Words is { Count: > 0 }
                ? segment.Words
                : [new TimedWord(segment.Text, segment.Start, segment.End)])
            .Where(piece => !string.IsNullOrWhiteSpace(piece.Text))
            .ToList();

        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];

            // La última pieza nunca es rescatada. Es la que trae el nombre —o lo que vino después—,
            // y una línea entera al 40 % diría «te escuché sin querer» sobre algo que sí te dijeron.
            var isLast = index == pieces.Count - 1;
            if (!isLast && preRoll > TimeSpan.Zero && piece.End <= preRoll)
            {
                recovered.Add(piece.Text.Trim());
            }
            else
            {
                spoken.Add(piece.Text.Trim());
            }
        }

        MoveNameToTheRequest(recovered, spoken, phrase);

        return new SplitUtterance(string.Join(' ', recovered), string.Join(' ', spoken));
    }

    /// <summary>
    /// Pasa el nombre del lado rescatado al lado del pedido.
    /// </summary>
    /// <remarks>
    /// El reconocedor de nombre avisa cuando la palabra <em>ya terminó</em>, así que el pre-roll la
    /// incluye y el corte cae detrás de ella. Sin esto, «Che Viernes, anotá» se dibujaba con «Che
    /// Viernes» al 40 % —o sea, como algo que no le dijeron a ella— y sólo «anotá» pleno.
    /// <para>
    /// Se compara contra la frase que <b>efectivamente</b> disparó y no contra una lista fija: si
    /// alguien le puso otro nombre al asistente, la que corresponde es esa.
    /// </para>
    /// </remarks>
    private static void MoveNameToTheRequest(List<string> recovered, List<string> spoken, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase) || recovered.Count == 0)
        {
            return;
        }

        var words = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        while (recovered.Count > 0 && words.Contains(Bare(recovered[^1])))
        {
            spoken.Insert(0, recovered[^1]);
            recovered.RemoveAt(recovered.Count - 1);
        }
    }

    /// <summary>La palabra sin lo que la rodea, para poder compararla con el nombre.</summary>
    private static string Bare(string word) =>
        new string([.. word.Where(char.IsLetter)]).ToLowerInvariant();
}
