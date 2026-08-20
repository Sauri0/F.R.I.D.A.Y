using System.Text.RegularExpressions;

namespace Viernes.Memory.Privacy;

internal static partial class MemoryContentPolicy
{
    /// <summary>
    /// Lo que la charla dijo, con lo que parecía una credencial tapado.
    /// </summary>
    /// <remarks>
    /// <b>Guardar charlas obliga a esto.</b> Una nota de memoria que contiene una clave se rechaza
    /// entera y el usuario se entera; un transcripto no se puede rechazar —es lo que se dijo— así
    /// que lo único que queda es taparlo antes de que toque el disco. Si alguna vez le dictás una
    /// clave en voz alta, o se la pegás por el chat, sin esto quedaría en texto plano en su carpeta
    /// para siempre.
    /// <para>
    /// Usa el <em>mismo</em> reconocedor que el rechazo de memorias y no una copia: son la misma
    /// decisión —qué parece una credencial— y dos copias de una decisión terminan diciendo cosas
    /// distintas. Ver <see cref="CredentialRegex"/>.
    /// </para>
    /// <para>
    /// No pretende ser perfecto y no puede serlo: reconoce las formas conocidas —las claves de
    /// Google, OpenRouter, GitHub, Slack, un «bearer», y las frases del tipo «la clave es …»—. Lo que
    /// no reconozca pasa. Por eso esto es una red y no una garantía, y por eso lo que se guarda
    /// sigue siendo local y borrable a mano.
    /// </para>
    /// </remarks>
    public static string Redact(string? content) =>
        string.IsNullOrEmpty(content)
            ? string.Empty
            : CredentialRegex().Replace(content, "«algo que parecía una credencial, no se guardó»");

    public static string NormalizeAndValidate(string content, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new MemoryContentRejectedException(
                MemoryContentRejectionReason.Empty,
                "La memoria debe contener un hecho concreto.",
                parameterName);
        }

        var normalized = content.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new MemoryContentRejectedException(
                MemoryContentRejectionReason.TooLong,
                $"Una memoria no puede superar {maximumLength} caracteres.",
                parameterName);
        }

        if (normalized.Contains('\r') || normalized.Contains('\n') ||
            RolePrefixRegex().IsMatch(normalized) || JsonConversationRegex().IsMatch(normalized))
        {
            throw new MemoryContentRejectedException(
                MemoryContentRejectionReason.ConversationLike,
                "Guardá un hecho breve, no una conversación ni una transcripción.",
                parameterName);
        }

        if (normalized.Any(character => char.IsControl(character)))
        {
            throw new MemoryContentRejectedException(
                MemoryContentRejectionReason.ContainsControlCharacters,
                "La memoria contiene caracteres de control no permitidos.",
                parameterName);
        }

        if (CredentialRegex().IsMatch(normalized))
        {
            throw new MemoryContentRejectedException(
                MemoryContentRejectionReason.CredentialLike,
                "Viernes no guarda contraseñas, tokens ni claves de API en su memoria.",
                parameterName);
        }

        return normalized;
    }

    [GeneratedRegex(
        @"^\s*(?:user|assistant|system|usuario|asistente|viernes)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RolePrefixRegex();

    [GeneratedRegex(
        "\\\"role\\\"\\s*:\\s*\\\"(?:user|assistant|system)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonConversationRegex();

    /// <summary>
    /// Lo que parece una credencial.
    /// </summary>
    /// <remarks>
    /// <b>El <c>\b</c> que estaba al principio tapaba media red.</b> Anclaba TODA la alternancia a un
    /// borde de palabra, así que las formas que no empiezan con letra —un token en la consulta de una
    /// dirección— no podían coincidir nunca. Ahora el borde va adentro de cada rama que lo necesita.
    /// <para>
    /// Lo que se agregó, y por qué cada cosa: <c>clave</c> a secas, porque exigir «clave secreta» es
    /// pedirle a alguien que dicte una que hable como un manual, y era justo la forma que el propio
    /// comentario prometía cubrir; <c>sk_</c> con guión bajo, que es como las escribe Stripe;
    /// <c>AKIA…</c>, que es una de Amazon; <c>pwd</c> y <c>secret</c>, que son las que aparecen en
    /// una cadena de conexión; y el token en la consulta de una dirección, que es como se filtra una
    /// credencial pegando un enlace.
    /// </para>
    /// <para>
    /// <b>Prefiere tapar de más.</b> «La clave es entender el problema» se va a tapar, y eso es un
    /// renglón menos legible en una charla guardada. Al lado de una credencial en texto plano en el
    /// disco, no se compara.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?:\bsk-or-v1-[a-z0-9_-]{8,}|\bsk[-_][a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{20,}|\bAKIA[0-9A-Z]{16}|\bgh[pousr]_[a-z0-9]{20,}|\bxox[baprs]-[a-z0-9-]{10,}|\bbearer\s+[a-z0-9._~+/=-]{8,}|[?&](?:access_|api_|auth_|id_)?token=[^\s&]+|\b(?:api[\s_-]*key|token|password|passwd|pwd|secret|contrase(?:ñ|n)a|clave(?:\s+secreta)?|pin)\s*(?::|=|\bes\b)\s*\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();
}
