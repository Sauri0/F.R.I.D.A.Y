using System.Text.RegularExpressions;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Evita que las tools persistentes guarden cadenas con forma de credencial.
/// Es una defensa de último recurso, no un detector infalible de secretos.
/// </summary>
internal static class SensitiveContentGuard
{
    private static readonly Regex CredentialPattern = new(
        @"\b(?:sk-or-v1-[a-z0-9_-]{8,}|sk-[a-z0-9_-]{16,}|AIza[a-z0-9_-]{20,}|gh[pousr]_[a-z0-9]{20,}|xox[baprs]-[a-z0-9-]{10,}|bearer\s+[a-z0-9._~+/=-]{8,})\b|(?:api[\s_-]*key|token|password|contrase(?:ñ|n)a|clave\s+secreta)\s*(?::|=|\bes\b)\s*\S{6,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    public static void RejectCredentialLikeContent(string? value, string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(value) && CredentialPattern.IsMatch(value))
        {
            throw new ArgumentException(
                "No guardé el dato porque parece contener una credencial o un secreto.",
                parameterName);
        }
    }
}
