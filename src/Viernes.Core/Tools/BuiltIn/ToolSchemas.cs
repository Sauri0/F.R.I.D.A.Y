namespace Viernes.Core.Tools.BuiltIn;

internal static class ToolSchemas
{
    public static object Object(
        IReadOnlyDictionary<string, object> properties,
        string[]? required = null,
        bool additionalProperties = false) => new
        {
            type = "object",
            properties,
            required = required ?? [],
            additionalProperties
        };

    public static object String(string description, string? format = null, string[]? enumValues = null)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description
        };
        if (!string.IsNullOrWhiteSpace(format))
        {
            schema["format"] = format;
        }

        if (enumValues is { Length: > 0 })
        {
            schema["enum"] = enumValues;
        }

        return schema;
    }
}
