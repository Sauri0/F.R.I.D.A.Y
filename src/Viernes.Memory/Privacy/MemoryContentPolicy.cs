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
    /// distintas. Ver <see cref="CredentialLikeText"/>.
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
            : CredentialLikeText.Redact(content);

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

        if (CredentialLikeText.Looks(normalized))
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

}
