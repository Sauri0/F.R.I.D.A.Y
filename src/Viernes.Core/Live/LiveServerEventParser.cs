using System.Globalization;
using System.Text.Json;

namespace Viernes.Core.Live;

/// <summary>
/// Lee los mensajes del servidor.
/// </summary>
/// <remarks>
/// Se lee con <see cref="JsonDocument"/> propiedad por propiedad, sin mapear a un tipo generado. La
/// razón es que este modelo es preview y el servidor agrega campos: un deserializador estricto se
/// rompe con un campo que no conoce, y romperse acá significa perder el <c>interrupted</c> que venía
/// en el mismo mensaje. Lo que no se reconoce se ignora y lo que sí, se lee.
/// </remarks>
public static class LiveServerEventParser
{
    /// <summary>
    /// Convierte el JSON crudo en un evento. Nunca lanza por contenido inesperado.
    /// </summary>
    /// <remarks>
    /// Un JSON roto vuelve como evento con <see cref="LiveServerEvent.Error"/> cargado, no como
    /// excepción: el bucle de lectura tiene que poder seguir leyendo el mensaje siguiente. Un
    /// mensaje ilegible es un mensaje perdido, no una sesión perdida.
    /// </remarks>
    public static LiveServerEvent Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return LiveServerEvent.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return new LiveServerEvent { Error = "El servidor mandó algo que no es JSON válido." };
        }
    }

    private static LiveServerEvent Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return LiveServerEvent.Empty;
        }

        var audio = new List<byte[]>();
        string? text = null;
        string? inputTranscript = null;
        string? outputTranscript = null;
        var interrupted = false;
        var generationComplete = false;
        var turnComplete = false;

        if (root.TryGetProperty("serverContent", out var serverContent) &&
            serverContent.ValueKind == JsonValueKind.Object)
        {
            interrupted = ReadBoolean(serverContent, "interrupted");
            generationComplete = ReadBoolean(serverContent, "generationComplete");
            turnComplete = ReadBoolean(serverContent, "turnComplete");
            inputTranscript = ReadTranscription(serverContent, "inputTranscription");
            outputTranscript = ReadTranscription(serverContent, "outputTranscription");

            if (serverContent.TryGetProperty("modelTurn", out var modelTurn) &&
                modelTurn.ValueKind == JsonValueKind.Object &&
                modelTurn.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                text = ReadParts(parts, audio);
            }
        }

        string? resumptionHandle = null;
        var resumable = false;
        if (root.TryGetProperty("sessionResumptionUpdate", out var resumption) &&
            resumption.ValueKind == JsonValueKind.Object)
        {
            if (resumption.TryGetProperty("newHandle", out var handle) &&
                handle.ValueKind == JsonValueKind.String)
            {
                var value = handle.GetString();
                resumptionHandle = string.IsNullOrWhiteSpace(value) ? null : value;
            }

            resumable = ReadBoolean(resumption, "resumable");
        }

        TimeSpan? goAway = null;
        if (root.TryGetProperty("goAway", out var away) && away.ValueKind == JsonValueKind.Object)
        {
            // Puede llegar sin timeLeft. Que no diga cuánto falta no lo hace menos un aviso de
            // cierre, así que se registra igual con cero: lo que dispara la reconexión es el goAway,
            // no el número.
            goAway = away.TryGetProperty("timeLeft", out var timeLeft)
                ? ParseDuration(timeLeft) ?? TimeSpan.Zero
                : TimeSpan.Zero;
        }

        return new LiveServerEvent
        {
            SetupComplete = root.TryGetProperty("setupComplete", out var setup) &&
                setup.ValueKind is JsonValueKind.Object or JsonValueKind.True,
            Audio = audio,
            Text = text,
            InputTranscript = inputTranscript,
            OutputTranscript = outputTranscript,
            Interrupted = interrupted,
            GenerationComplete = generationComplete,
            TurnComplete = turnComplete,
            FunctionCalls = ReadFunctionCalls(root),
            CancelledToolCalls = ReadCancelledToolCalls(root),
            ResumptionHandle = resumptionHandle,
            ResumptionHandleIsResumable = resumable,
            GoAwayTimeLeft = goAway,
            Usage = ReadUsage(root),
            Error = ReadError(root)
        };
    }

    /// <summary>
    /// Saca el audio y el texto de las partes, sin asumir dónde vienen ni cuántas hay.
    /// </summary>
    /// <remarks>
    /// El audio se identifica por el tipo MIME y no por la posición: en el cliente de voz por HTTP
    /// ya pasó que la parte de audio no fuera la primera, y acá el costo de equivocarse es un hueco
    /// en la voz en el medio de una frase.
    /// </remarks>
    private static string? ReadParts(JsonElement parts, List<byte[]> audio)
    {
        string? text = null;

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.TryGetProperty("inlineData", out var inline) &&
                inline.ValueKind == JsonValueKind.Object &&
                inline.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.String)
            {
                var mime = inline.TryGetProperty("mimeType", out var mimeType) && mimeType.ValueKind == JsonValueKind.String
                    ? mimeType.GetString()
                    : null;

                if (mime is null || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.TryGetBytesFromBase64(out var bytes) && bytes.Length > 0)
                    {
                        audio.Add(bytes);
                    }
                }
            }

            if (part.TryGetProperty("text", out var partText) &&
                partText.ValueKind == JsonValueKind.String)
            {
                var value = partText.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    text = text is null ? value : text + value;
                }
            }
        }

        return text;
    }

    /// <summary>
    /// Un objeto de argumentos vacío, para las llamadas sin parámetros.
    /// </summary>
    /// <remarks>
    /// Se arma una sola vez y se reparte: un <see cref="JsonElement"/> clonado no tiene dueño y es
    /// de sólo lectura, así que compartirlo es seguro y evita crear un documento por llamada.
    /// </remarks>
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Lee las herramientas que el servidor pide ejecutar.
    /// </summary>
    /// <remarks>
    /// Los argumentos se <b>clonan</b>. El documento que los trajo se cierra al terminar de leer el
    /// mensaje y la herramienta se ejecuta después —abrir una aplicación tarda un segundo largo—,
    /// así que sin clonar lo que le llega a la herramienta es memoria ya devuelta.
    /// <para>
    /// Una llamada sin <c>name</c> se descarta entera: no hay nada que ejecutar y contestarle al
    /// servidor con un nombre inventado es peor que ignorarla.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<LiveFunctionCall> ReadFunctionCalls(JsonElement root)
    {
        if (!root.TryGetProperty("toolCall", out var toolCall) ||
            toolCall.ValueKind != JsonValueKind.Object ||
            !toolCall.TryGetProperty("functionCalls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<LiveFunctionCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var toolName = name.GetString();
            if (string.IsNullOrWhiteSpace(toolName))
            {
                continue;
            }

            var id = call.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String
                ? identifier.GetString() ?? string.Empty
                : string.Empty;

            var arguments = call.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.Clone()
                : EmptyArguments;

            result.Add(new LiveFunctionCall(id, toolName, arguments));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadCancelledToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("toolCallCancellation", out var cancellation) ||
            cancellation.ValueKind != JsonValueKind.Object ||
            !cancellation.TryGetProperty("ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = id.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadTranscription(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) ||
            node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("text", out var text) ||
            text.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = text.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Lee una duración de protobuf, que viaja como <c>"9.5s"</c> y no como número.
    /// </summary>
    /// <remarks>
    /// Con punto decimal siempre, venga de donde venga la máquina: por eso el parseo es invariante y
    /// no con la cultura local. En un Windows en español, <c>double.Parse("9.5")</c> con la cultura
    /// del sistema devuelve noventa y cinco, y noventa y cinco segundos de margen para reconectar
    /// cuando quedaban nueve es la clase de error que se descubre cuando la sesión se corta sola.
    /// </remarks>
    private static TimeSpan? ParseDuration(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var raw)
                ? TimeSpan.FromSeconds(raw)
                : null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith('s'))
        {
            trimmed = trimmed[..^1];
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static LiveTokenUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new LiveTokenUsage(
            ReadInt(usage, "promptTokenCount"),
            ReadInt(usage, "responseTokenCount"),
            ReadInt(usage, "totalTokenCount"));
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Devuelve el mensaje de error del servidor, sin el resto del cuerpo.
    /// </summary>
    /// <remarks>
    /// Se copia sólo <c>message</c> y <c>code</c> a propósito. Volcar el error completo es cómodo
    /// mientras se depura y es una forma de escribir la clave en un archivo de registro el día que
    /// el servidor la devuelva reflejada en algún campo.
    /// </remarks>
    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetRawText()
            : null;

        if (string.IsNullOrWhiteSpace(message))
        {
            return code is null ? "El servidor devolvió un error sin detalle." : $"El servidor devolvió el error {code}.";
        }

        return code is null ? message : $"{message} (código {code})";
    }
}
