using System.Globalization;
using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

internal static class JsonToolArguments
{
    public static string RequiredString(JsonElement arguments, string propertyName, int maximumLength)
    {
        EnsureObject(arguments);
        if (!arguments.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Falta el argumento '{propertyName}'.", propertyName);
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"El argumento '{propertyName}' no puede estar vacío.", propertyName);
        }

        if (text.Length > maximumLength)
        {
            throw new ArgumentException($"El argumento '{propertyName}' es demasiado largo.", propertyName);
        }

        return text;
    }

    public static string? OptionalString(JsonElement arguments, string propertyName, int maximumLength)
    {
        EnsureObject(arguments);
        if (!arguments.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"El argumento '{propertyName}' debe ser texto.", propertyName);
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > maximumLength)
        {
            throw new ArgumentException($"El argumento '{propertyName}' es demasiado largo.", propertyName);
        }

        return text;
    }

    public static DateTimeOffset RequiredDateTimeOffset(JsonElement arguments, string propertyName)
    {
        var text = RequiredString(arguments, propertyName, 64);
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var result))
        {
            throw new ArgumentException($"El argumento '{propertyName}' no contiene una fecha válida.", propertyName);
        }

        return result;
    }

    public static DateTimeOffset? OptionalDateTimeOffset(JsonElement arguments, string propertyName)
    {
        var text = OptionalString(arguments, propertyName, 64);
        if (text is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var result))
        {
            throw new ArgumentException($"El argumento '{propertyName}' no contiene una fecha válida.", propertyName);
        }

        return result;
    }

    private static void EnsureObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Los argumentos deben ser un objeto JSON.", nameof(arguments));
        }
    }
}
