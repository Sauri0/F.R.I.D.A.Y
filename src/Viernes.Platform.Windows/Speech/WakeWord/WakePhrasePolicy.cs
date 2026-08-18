using System.Globalization;
using System.Text;

namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// Qué cuenta como que la nombraron, y qué se hace cuando no está claro.
/// </summary>
/// <remarks>
/// Acá está el cambio de criterio más importante del oído, y conviene dejar escrito el porqué,
/// porque contradice una decisión anterior que parecía razonable.
/// <para>
/// Antes se exigían dos palabras —«Hola Viernes»— porque «viernes» solo se dispara con «el viernes
/// tengo turno». Está medido en este equipo: esos falsos positivos entran con confianza 0,69, que es
/// <em>más alta</em> que casi todas las detecciones reales (0,61–0,72). O sea: ningún umbral los
/// separa. Subirlo deja afuera a la persona antes que al calendario.
/// </para>
/// <para>
/// Pero el problema nunca fue la detección: era lo que pasaba después. Al dispararse, el asistente
/// saludaba —«¿sí?»— y eso, en medio de una conversación ajena, es una interrupción. Si en cambio al
/// dispararse manda la frase entera al modelo, «el viernes tengo turno» llega como lo que es, el
/// modelo ve que nadie le pidió nada y no hace nada. El falso positivo deja de costar.
/// </para>
/// <para>
/// Por eso el modo predeterminado acepta el nombre solo, en cualquier posición. La exigencia de dos
/// palabras queda disponible por configuración para quien la prefiera, no como valor de fábrica.
/// </para>
/// </remarks>
public static class WakePhrasePolicy
{
    /// <summary>
    /// Deja una frase en su forma canónica: sin espacios de más y sin caracteres de control.
    /// </summary>
    /// <param name="phrase">La frase tal como vino.</param>
    /// <returns>La frase normalizada, o vacía si no quedaba nada.</returns>
    public static string Normalize(string? phrase) =>
        // Los caracteres de control se cambian por espacio y no se borran: borrar un tabulador entre
        // dos palabras las pega, y «Hola Viernes» pasaba a ser «HolaViernes» — que no coincide con
        // ninguna frase configurada y por lo tanto nunca despierta.
        string.Join(' ', new string([.. (phrase ?? string.Empty)
                .Select(character => char.IsControl(character) ? ' ' : character)])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Si la frase tiene dos palabras o más.</summary>
    /// <param name="phrase">La frase a mirar.</param>
    /// <returns><c>true</c> si son al menos dos palabras.</returns>
    public static bool IsCompound(string? phrase) =>
        Normalize(phrase).Count(character => character == ' ') >= 1;

    /// <summary>
    /// Si una detección se acepta como activación con la configuración dada.
    /// </summary>
    /// <param name="phrase">La frase que detectó el reconocedor.</param>
    /// <param name="requireCompoundPhrase">Si se exigen dos palabras.</param>
    /// <returns><c>true</c> si hay que despertar.</returns>
    public static bool Accepts(string? phrase, bool requireCompoundPhrase)
    {
        var normalized = Normalize(phrase);
        if (normalized.Length == 0)
        {
            return false;
        }

        return !requireCompoundPhrase || IsCompound(normalized);
    }

    /// <summary>
    /// Busca el nombre dentro de una transcripción, en cualquier posición.
    /// </summary>
    /// <remarks>
    /// Compara sin acentos y sin mayúsculas porque el reconocedor devuelve las dos formas según el
    /// contexto, y respetando límites de palabra: «adviernes» o «viernesito» no son nombrarla. Sirve
    /// para confirmar después de transcribir lo que el reconocedor de nombre creyó oír antes.
    /// </remarks>
    /// <param name="transcript">El texto transcripto.</param>
    /// <param name="names">Los nombres a buscar.</param>
    /// <returns><c>true</c> si alguno aparece como palabra suelta.</returns>
    public static bool MentionsName(string? transcript, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var haystack = Fold(transcript);
        if (haystack.Length == 0)
        {
            return false;
        }

        foreach (var name in names)
        {
            foreach (var word in Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = haystack.IndexOf(word, StringComparison.Ordinal);
                while (index >= 0)
                {
                    var beforeIsBoundary = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
                    var end = index + word.Length;
                    var afterIsBoundary = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                    if (beforeIsBoundary && afterIsBoundary)
                    {
                        return true;
                    }

                    index = haystack.IndexOf(word, index + 1, StringComparison.Ordinal);
                }
            }
        }

        return false;
    }

    /// <summary>Minúsculas y sin acentos, para poder comparar «Viernes» con «viernes».</summary>
    private static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.ToLower(CultureInfo.GetCultureInfo("es-AR"));
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
