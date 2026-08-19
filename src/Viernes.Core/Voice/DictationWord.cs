namespace Viernes.Core.Voice;

/// <summary>
/// Qué tan firme es una palabra de lo que se está transcribiendo.
/// </summary>
/// <remarks>
/// El boceto no pinta la transcripción de un solo color, y no es decoración: el usuario tiene que
/// poder distinguir de un vistazo lo que el reconocedor ya dio por firme de lo que todavía puede
/// cambiar, sin leerlo dos veces.
/// </remarks>
public enum DictationQuality
{
    /// <summary>
    /// Lo que se dijo <b>antes</b> de nombrarla, sacado de la ventana rodante del oído continuo.
    /// </summary>
    /// <remarks>
    /// Se dibuja al 40 %. Es información que la persona no sabe que el asistente tiene, así que
    /// mostrarla más apagada dice dos cosas a la vez: «esto también lo escuché» y «esto no lo dijiste
    /// para mí». Sin ese matiz, recuperar el búfer se siente como espiar.
    /// </remarks>
    Recuperado,

    /// <summary>Lo que el reconocedor ya dio por firme. Se dibuja pleno.</summary>
    Confirmado,

    /// <summary>La palabra que se está formando. Se dibuja al 60 % y en itálica.</summary>
    /// <remarks>
    /// Es <b>una sola</b> —la última— y sólo mientras la transcripción está viva. En el fuente es
    /// literal: <c>prov = D.live &amp;&amp; i === W.length - 1</c>. No es una cola provisoria entera,
    /// que es lo que sale si uno lo implementa de memoria y se ve completamente distinto: con una
    /// cola, media frase tiembla todo el tiempo; con una sola palabra, el texto se va asentando
    /// detrás del cursor y sólo el borde se mueve.
    /// </remarks>
    Provisorio
}

/// <summary>Una palabra de la transcripción en curso, con qué tan firme es.</summary>
/// <param name="Text">La palabra, sin el espacio.</param>
/// <param name="Quality">Qué tan firme es.</param>
public readonly record struct DictationWord(string Text, DictationQuality Quality);

/// <summary>
/// Arma la secuencia de palabras que se muestra mientras se dicta.
/// </summary>
/// <remarks>
/// Vive acá y no en la interfaz por una razón concreta: la regla de qué palabra es provisoria es
/// exactamente el tipo de cosa que se reimplementa distinto en cada lugar que la necesita, y este
/// repo ya pagó eso —el umbral del micrófono estuvo escrito dos veces y el banco de medición pasó
/// semanas informando contra una copia vieja de la fórmula—. Una sola implementación, con pruebas.
/// </remarks>
public static class DictationLine
{
    /// <summary>
    /// Junta lo recuperado del búfer con lo que se está diciendo.
    /// </summary>
    /// <param name="recovered">
    /// Lo dicho antes del nombre, ya cortado. Puede venir vacío: la mayoría de las veces la persona
    /// arranca nombrándola.
    /// </param>
    /// <param name="spoken">Lo que se lleva transcripto de esta tanda.</param>
    /// <param name="live">
    /// Si la transcripción sigue viva. Con <c>false</c> —el reconocedor cerró la frase— <b>no queda
    /// ninguna palabra provisoria</b>: la última pasa a firme. Si no, la frase termina temblando.
    /// </param>
    public static IReadOnlyList<DictationWord> Build(
        IReadOnlyList<string>? recovered,
        IReadOnlyList<string>? spoken,
        bool live)
    {
        var words = new List<DictationWord>();

        if (recovered is not null)
        {
            foreach (var word in recovered)
            {
                if (!string.IsNullOrWhiteSpace(word))
                {
                    words.Add(new DictationWord(word, DictationQuality.Recuperado));
                }
            }
        }

        if (spoken is null)
        {
            return words;
        }

        // El índice de la última palabra se calcula sobre la lista ORIGINAL y no sobre lo que se
        // agregó: si se filtraran vacíos primero, una palabra en blanco al final —que el reconocedor
        // manda más seguido de lo que parece— dejaría la anterior marcada como provisoria para
        // siempre, porque nunca llegaría una que la reemplace.
        var last = spoken.Count - 1;

        for (var index = 0; index < spoken.Count; index++)
        {
            var word = spoken[index];
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            words.Add(new DictationWord(
                word,
                live && index == last ? DictationQuality.Provisorio : DictationQuality.Confirmado));
        }

        return words;
    }

    /// <summary>El texto plano de la línea, para leerlo o para mandarlo al modelo.</summary>
    public static string Flatten(IReadOnlyList<DictationWord> words) =>
        string.Join(' ', words.Select(word => word.Text));
}
