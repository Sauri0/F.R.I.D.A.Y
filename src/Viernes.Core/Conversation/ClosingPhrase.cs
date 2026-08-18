using System.Globalization;
using System.Text;

namespace Viernes.Core.Conversation;

/// <summary>
/// Decide si lo que dijo el usuario es una despedida.
/// </summary>
/// <remarks>
/// Es el inverso de la palabra de activación: la frase que lo despide. Sin esto, un asistente que no
/// deja de escuchar es un micrófono abierto sin salida.
/// <para>
/// Vive acá y no en la aplicación porque es lógica de texto pura, sin nada de Windows, y porque la
/// prueba que la cubría <em>reimplementaba la regla adentro del propio archivo de prueba</em> —el
/// shell no es referenciable desde ahí—. Esa copia pasaba en verde mientras el runtime hacía otra
/// cosa: afirmaba que «basta de recordatorios a la mañana, moveme todo a la tarde» no cortaba la
/// conversación, y en el equipo del usuario la cortaba. Un test que reimplementa lo que prueba mide
/// su propia expectativa.
/// </para>
/// </remarks>
public static class ClosingPhrase
{
    /// <summary>Despedidas ambiguas: son cierre sólo si la frase entera es corta.</summary>
    private static readonly string[] Farewells =
    [
        "listo", "gracias", "chau", "nada mas", "deja", "ya esta",
        "sali", "adios", "hasta luego", "nos vemos", "fin"
    ];

    /// <summary>
    /// Órdenes inequívocas de callarse. Se reconocen aunque vengan en medio de una frase larga.
    /// </summary>
    /// <remarks>
    /// Las despedidas exigen frases cortas, porque «gracias por todo esto que hiciste» no puede
    /// cortar una conversación. Pero esa misma exigencia dejaba pasar «no, no, no, dejá de oír»
    /// —seis palabras— que es exactamente cómo suena alguien pidiendo que pare. Estas no son
    /// ambiguas: nadie dice «dejá de escuchar» en el medio de un pedido queriendo que siga.
    /// </remarks>
    private static readonly string[] Explicit =
    [
        "deja de escuchar", "deja de escucharme", "deja de oir", "deja de oirme",
        "no escuches mas", "dejate de escuchar", "basta de escuchar",
        "callate", "para ya", "cerra la conversacion", "dejalo ahi",

        // Mandarla a dormir es la forma más natural de despedirla. Van sin acento porque Normalize
        // los pliega: «descansá» y «andá» llegan acá ya como «descansa» y «anda».
        "descansa un rato", "tomate un descanso", "anda a dormir",
        "andate a dormir", "a dormir", "a descansar",
        "quedate tranquila", "quedate quieta", "chau por ahora", "hasta despues"
    ];

    /// <summary>
    /// Órdenes de callarse que también son palabras corrientes.
    /// </summary>
    /// <remarks>
    /// Estaban mezcladas con las inequívocas, que valen en cualquier posición y a cualquier largo.
    /// Como resultado «basta de recordatorios a la mañana, moveme todo a la tarde» cerraba la
    /// conversación: la palabra estaba, aunque el pedido fuera justo lo contrario de terminar. Lo
    /// mismo con «cortá el video por la mitad» o «terminá de escribir eso».
    /// </remarks>
    private static readonly string[] Ambiguous =
    [
        "silencio", "pare", "basta", "suficiente", "terminamos", "termina",
        "corta", "cortala", "apagate", "andate", "olvidate", "descansa", "dormi", "dormite",

        // «desactivate» faltaba y el usuario lo usa. Se agrega, pero agregar palabras de a una no
        // es la solución: la lista siempre va a ir atrás del idioma. Lo que cubre el resto es la
        // herramienta descansar, donde el modelo entiende la intención en vez de reconocer la
        // palabra. Esto queda sólo para que lo literal corte al instante, sin ida y vuelta.
        "desactivate", "desactivarte", "apagarte", "apaga", "desconectate",

        // «pará» pierde el acento al normalizar y queda «para», que no estaba: la lista tenía
        // «pare» y «para ya», así que la forma más natural de decirlo caía afuera de la vía rápida.
        "para", "para la", "frena", "frenate"
    ];

    /// <summary>Cuántas palabras puede tener una frase para que lo ambiguo cuente como despedida.</summary>
    private const int ShortEnough = 4;

    /// <summary>Dice si la frase cierra la conversación.</summary>
    public static bool IsClosing(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (Explicit.Any(command => ContainsWholePhrase(normalized, command)))
        {
            return true;
        }

        // Cortar de más es peor que cortar de menos: deja al usuario hablando solo.
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > ShortEnough)
        {
            return false;
        }

        return Ambiguous.Any(command => ContainsWholePhrase(normalized, command)) ||
            Farewells.Any(phrase => ContainsWholePhrase(normalized, phrase));
    }

    /// <summary>
    /// Baja el texto a minúsculas sin acentos ni puntuación, y sin el nombre del asistente.
    /// </summary>
    /// <remarks>
    /// Los acentos se pliegan porque la transcripción los pone según le parece —«recordame» o
    /// «recórdame», «deja» o «dejá»—: comparar con acentos es comparar contra una moneda al aire.
    /// </remarks>
    public static string Normalize(string text)
    {
        var stripped = new string(text
            .ToLowerInvariant()
            .Where(character => !char.IsPunctuation(character))
            .ToArray())
            .Replace("viernes", string.Empty, StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder(stripped.Length);
        foreach (var character in stripped)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        // Los «no, no, no» del habla real dejan espacios dobles al caer la puntuación.
        return string.Join(
            ' ',
            builder.ToString()
                .Normalize(NormalizationForm.FormC)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Coincidencia por palabras completas: sin esto «para» encontraría a «parece» y «fin» a
    /// «finalmente», y una charla se cortaría sola a mitad de una frase cualquiera.
    /// </summary>
    private static bool ContainsWholePhrase(string normalized, string phrase) =>
        normalized.Equals(phrase, StringComparison.Ordinal) ||
        normalized.StartsWith(phrase + " ", StringComparison.Ordinal) ||
        normalized.EndsWith(" " + phrase, StringComparison.Ordinal) ||
        normalized.Contains(" " + phrase + " ", StringComparison.Ordinal);
}
