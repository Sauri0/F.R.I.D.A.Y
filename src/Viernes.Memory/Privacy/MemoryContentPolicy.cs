using System.Text.RegularExpressions;

namespace Viernes.Memory.Privacy;

internal static partial class MemoryContentPolicy
{
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

    [GeneratedRegex(
        @"\b(?:sk-or-v1-[a-z0-9_-]{8,}|sk-[a-z0-9_-]{16,}|AIza[a-z0-9_-]{20,}|gh[pousr]_[a-z0-9]{20,}|xox[baprs]-[a-z0-9-]{10,}|bearer\s+[a-z0-9._~+/=-]{8,}|(?:api[\s_-]*key|token|password|contrase(?:ñ|n)a|clave\s+secreta)\s*(?::|=|\bes\b)\s*\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();
}
