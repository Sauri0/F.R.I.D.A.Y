using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Viernes.Core.Tools;

namespace Viernes.Core.Live;

/// <summary>
/// Los mensajes que se le mandan al servidor, escritos a mano.
/// </summary>
/// <remarks>
/// Se escriben con <see cref="Utf8JsonWriter"/> y no con un serializador de objetos por dos razones
/// concretas de este protocolo:
/// <list type="number">
///   <item>
///     Los enteros de 64 bits viajan <b>como cadena entre comillas</b>. Es la regla de proto3 para
///     <c>int64</c>, y ningún serializador de C# la aplica solo: escribe un número y el servidor
///     rechaza el setup entero. Acá se ve escrito, y hay una prueba que lo fija.
///   </item>
///   <item>
///     Los objetos vacíos son significativos. <c>"sessionResumption": {}</c> quiere decir «activada,
///     sin handle previo»; omitir la propiedad quiere decir «desactivada». Un serializador que
///     descarta nulos borra la diferencia y la sesión se muere sola a los diez minutos sin que nadie
///     entienda por qué.
///   </item>
/// </list>
/// </remarks>
public static class LiveClientMessages
{
    /// <summary>
    /// Arma el primer mensaje, el único que puede mandarse antes del <c>setupComplete</c>.
    /// </summary>
    /// <param name="options">Voz, modelo, silencio de fin de turno y compresión de contexto.</param>
    /// <param name="resumptionHandle">
    /// El handle de la sesión anterior cuando se está reconectando, o <c>null</c> en la primera
    /// conexión. Es lo que hace que reconectar no sea empezar de cero.
    /// </param>
    /// <param name="tools">
    /// Las herramientas que la sesión hablada va a poder usar. Es el <b>único</b> momento en que se
    /// declaran: el setup se manda una vez y no hay forma de agregar una con la sesión abierta.
    /// </param>
    /// <returns>El JSON del mensaje <c>setup</c>, listo para mandar.</returns>
    public static string BuildSetup(
        GeminiLiveOptions options,
        string? resumptionHandle = null,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("setup");

            // El servidor quiere el nombre completo del recurso. Mandar «gemini-…» pelado devuelve
            // un 400 que habla de modelo no encontrado y manda a buscar el nombre del modelo, que
            // estaba bien: lo que faltaba era el prefijo.
            writer.WriteString("model", $"models/{options.Model}");

            writer.WriteStartObject("generationConfig");
            writer.WriteStartArray("responseModalities");
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();
            writer.WriteStartObject("speechConfig");
            writer.WriteStartObject("voiceConfig");
            writer.WriteStartObject("prebuiltVoiceConfig");
            writer.WriteString("voiceName", options.VoiceName);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (options.SystemInstruction is { Length: > 0 } instruction)
            {
                writer.WriteStartObject("systemInstruction");
                writer.WriteStartArray("parts");
                writer.WriteStartObject();
                writer.WriteString("text", instruction);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            // La busqueda va aparte de las funciones y en el mismo arreglo. Se escribe aunque no
            // haya ninguna funcion declarada: sin esto, hablando no puede buscar NADA -- la busqueda
            // web del proyecto la inyecta el proveedor del camino escrito, y este camino no pasa por
            // ahi. Medido con la misma clave y el mismo modelo: sin la herramienta contesta «no
            // tengo acceso a noticias en tiempo real»; con ella, contesta la noticia.
            var declaraBusqueda = options.WebSearch;
            var declaraFunciones = tools is { Count: > 0 };


            if (declaraBusqueda || declaraFunciones)
            {
                writer.WriteStartArray("tools");

                if (declaraBusqueda)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("googleSearch");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                if (!declaraFunciones)
                {
                    writer.WriteEndArray();
                }
            }

            if (declaraFunciones)
            {
                writer.WriteStartObject();
                writer.WriteStartArray("functionDeclarations");
                foreach (var tool in tools!)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    if (tool.Parameters.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("parameters");
                        WriteSchema(writer, tool.Parameters);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteStartObject("realtimeInputConfig");
            writer.WriteStartObject("automaticActivityDetection");
            // Nunca se manda disabled=true: el detector de voz es del servidor, viene prendido, y es
            // lo que permite interrumpirla. Sólo se le ajusta cuánto silencio espera.
            writer.WriteNumber("silenceDurationMs", options.SilenceDurationMs);
            writer.WriteEndObject();
            writer.WriteString(
                "activityHandling",
                options.InterruptOnUserSpeech ? "START_OF_ACTIVITY_INTERRUPTS" : "NO_INTERRUPTION");
            writer.WriteEndObject();

            // Objeto vacío = reanudación activada sin sesión previa. Ver el comentario de la clase:
            // acá la ausencia y el vacío significan cosas distintas.
            writer.WriteStartObject("sessionResumption");
            if (!string.IsNullOrWhiteSpace(resumptionHandle))
            {
                writer.WriteString("handle", resumptionHandle.Trim());
            }

            writer.WriteEndObject();

            writer.WriteStartObject("contextWindowCompression");
            WriteInt64AsString(writer, "triggerTokens", options.TriggerTokens);
            writer.WriteStartObject("slidingWindow");
            WriteInt64AsString(writer, "targetTokens", options.TargetTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (options.TranscribeInput)
            {
                writer.WriteStartObject("inputAudioTranscription");
                writer.WriteEndObject();
            }

            if (options.TranscribeOutput)
            {
                writer.WriteStartObject("outputAudioTranscription");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Arma un fragmento de audio del micrófono.
    /// </summary>
    /// <remarks>
    /// Va por <c>realtimeInput.audio</c> y no por <c>clientContent</c>: <c>clientContent</c> cierra
    /// el turno cada vez que se manda, así que usarlo para el micrófono equivale a decirle «terminé
    /// de hablar» veinte veces por segundo.
    /// </remarks>
    public static string BuildAudioChunk(ReadOnlySpan<byte> pcm16k)
    {
        var buffer = new ArrayBufferWriter<byte>(pcm16k.Length * 2);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("realtimeInput");
            writer.WriteStartObject("audio");
            writer.WriteString("mimeType", LiveAudioFormat.InputMimeType);
            writer.WriteBase64String("data", pcm16k);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Avisa que se cortó el micrófono, para que el servidor no espere más audio.</summary>
    public static string BuildAudioStreamEnd() =>
        """{"realtimeInput":{"audioStreamEnd":true}}""";

    /// <summary>
    /// Manda texto escrito y cierra el turno para que conteste.
    /// </summary>
    /// <remarks>
    /// Esto sí va por <c>clientContent</c> con <c>turnComplete</c>: cuando se escribe no hay detector
    /// de voz que decida cuándo terminó la frase, la terminó el Enter.
    /// </remarks>
    public static string BuildText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var buffer = new ArrayBufferWriter<byte>(text.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("clientContent");
            writer.WriteStartArray("turns");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("turnComplete", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Devuelve lo que dieron las herramientas que el servidor pidió ejecutar.
    /// </summary>
    /// <remarks>
    /// Va todo en un mensaje aunque sean varias llamadas, porque así viene el <c>toolCall</c>: el
    /// servidor manda el lote junto y espera el lote junto. Contestar de a una funciona igual, pero
    /// deja al modelo generando entre respuesta y respuesta con la mitad del trabajo hecho.
    /// <para>
    /// El <c>id</c> es lo único que aparea la respuesta con la llamada. Si se pierde, el servidor
    /// queda esperando una contestación que ya mandamos y la charla se cuelga sin error.
    /// </para>
    /// </remarks>
    public static string BuildToolResponse(IReadOnlyList<LiveFunctionResponse> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("toolResponse");
            writer.WriteStartArray("functionResponses");
            foreach (var response in responses)
            {
                writer.WriteStartObject();
                writer.WriteString("id", response.Id);
                writer.WriteString("name", response.Name);
                writer.WriteStartObject("response");
                writer.WriteString("status", response.Outcome.Status);
                writer.WriteString("message", response.Outcome.Message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Los campos del esquema que este servidor entiende.
    /// </summary>
    /// <remarks>
    /// El resto se descarta a propósito. Las herramientas describen sus argumentos en JSON Schema
    /// completo —y el que arma los esquemas de este proyecto escribe <c>additionalProperties</c>—,
    /// pero acá el esquema no es JSON Schema sino el <c>Schema</c> del protocolo, que es un
    /// subconjunto. Un campo de más no da un error de campo: rebota el setup entero, y rebotar el
    /// setup es caerse al camino de siempre sin que nadie entienda por qué.
    /// </remarks>
    private static readonly string[] SchemaFields =
        ["type", "description", "format", "nullable", "enum", "items", "properties", "required"];

    /// <summary>
    /// Copia un esquema quedándose sólo con lo que el protocolo acepta.
    /// </summary>
    /// <remarks>
    /// Los nombres de tipo se copian tal cual llegan, en minúscula: es lo que muestran los ejemplos
    /// REST de la documentación de esta API, y es lo que ya escriben las herramientas.
    /// </remarks>
    private static void WriteSchema(Utf8JsonWriter writer, JsonElement schema)
    {
        writer.WriteStartObject();

        foreach (var field in schema.EnumerateObject())
        {
            if (Array.IndexOf(SchemaFields, field.Name) < 0)
            {
                continue;
            }

            switch (field.Name)
            {
                case "properties":
                    if (field.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    writer.WriteStartObject("properties");
                    foreach (var property in field.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteSchema(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;

                case "items":
                    if (field.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    writer.WritePropertyName("items");
                    WriteSchema(writer, field.Value);
                    break;

                case "required":
                    // Una lista vacía de obligatorios no se manda: no dice nada y es un campo más
                    // que el servidor tiene que aceptar.
                    if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    field.WriteTo(writer);
                    break;

                default:
                    field.WriteTo(writer);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteInt64AsString(Utf8JsonWriter writer, string name, long value) =>
        writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture));
}
