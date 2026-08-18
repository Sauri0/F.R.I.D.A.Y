using System.Text.Json;
using Viernes.Core.Tools;

namespace Viernes.Core.Mcp;

/// <summary>
/// Presenta una herramienta de un servidor MCP como una herramienta más de Viernes.
/// </summary>
/// <remarks>
/// Todo lo que llega por MCP entra por la misma puerta que lo local: el <c>ToolExecutor</c> y su
/// política. Eso es deliberado. Un servidor MCP es un proceso ajeno que puede hacer cosas reales
/// —escribir, clickear, mandar mensajes— y lo que Viernes lee de la web puede intentar dispararlo.
/// Si estas herramientas esquivaran la política, conectar un servidor equivaldría a entregar la
/// máquina; pasando por ella, conectar un servidor sólo agrega capacidades que se siguen confirmando.
/// <para>
/// Guarda la <see cref="McpServerConnection"/> y no una sesión: la sesión cambia cada vez que el
/// servidor se cae y vuelve, y la herramienta tiene que sobrevivir a eso. Antes guardaba el cliente
/// que le tocó al arrancar, así que cuando ese proceso moría la herramienta quedaba apuntando a un
/// muerto para siempre.
/// </para>
/// </remarks>
public sealed class McpBridgedTool : IAssistantTool
{
    private readonly McpServerConnection _connection;
    private readonly string _remoteName;

    public McpBridgedTool(
        McpServerConnection connection,
        McpToolDescriptor tool,
        bool confirmActions = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tool);

        _connection = connection;
        _remoteName = tool.Name;

        // El nombre se prefija con el del servidor: dos servidores pueden traer una herramienta
        // llamada «search» y el modelo tiene que poder distinguirlas.
        Definition = ToolDefinition.Create(
            Sanitize($"{connection.Name}_{tool.Name}"),
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"Herramienta «{tool.Name}» del servidor {connection.Name}."
                : tool.Description,
            ConvertSchema(tool.Schema),
            confirmActions ? ToolRiskLevel.RequiresConfirmation : ToolRiskLevel.Safe);
    }

    public ToolDefinition Definition { get; }

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = ToArgumentDictionary(arguments);
            var response = await _connection
                .CallToolAsync(_remoteName, payload, cancellationToken)
                .ConfigureAwait(false);

            var text = response.Text;

            // Muchos servidores MCP no marcan IsError y devuelven el fallo escrito adentro del texto.
            // Confiar sólo en la bandera hacía que un «NO_ACTIVE_DEVICE» de Spotify entrara como
            // éxito, y de ahí al recetario: la próxima vez se le sugería al modelo ese mismo camino
            // muerto como algo que había funcionado.
            var falloEnElTexto = LooksLikeAnError(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                text = response.IsError
                    ? "El servidor informó un error sin detalle."
                    : "Listo.";
            }

            return response.IsError || falloEnElTexto
                ? ToolExecutionResult.Failure(context.ToolCallId, Definition.Name, text)
                : ToolExecutionResult.Success(context.ToolCallId, Definition.Name, text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpServerUnavailableException exception)
        {
            // Decir cuál se cayó y que se está reintentando le da al modelo algo que contestar que
            // es cierto. «No respondió» a secas se parece demasiado a «no sé hacer eso», y de ahí
            // sale el asistente que se disculpa por una capacidad que en realidad tiene.
            return ToolExecutionResult.Failure(
                context.ToolCallId,
                Definition.Name,
                $"{exception.Message} Decile al usuario que ese servicio está caído y que se " +
                "reconecta solo; no le pidas que reinicie nada.");
        }
        catch (Exception exception)
        {
            // Un servidor caído no puede tumbar el turno; se informa y la conversación sigue.
            return ToolExecutionResult.Failure(
                context.ToolCallId,
                Definition.Name,
                $"El servidor MCP no respondió: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// Reconoce un fallo escrito en la respuesta cuando el servidor no lo marcó como tal.
    /// </summary>
    /// <remarks>
    /// Deliberadamente conservador: sólo marcas que aparecen al principio del texto o códigos en
    /// mayúsculas con guiones bajos, que es como los servidores reales devuelven sus errores. Buscar
    /// la palabra «error» en cualquier lado convertiría en fallo a cualquier respuesta que hable de
    /// errores —un archivo de registro, una canción que se llame así—, y equivocarse para este lado
    /// es peor: descarta trabajo que sí se hizo.
    /// </remarks>
    private static bool LooksLikeAnError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var head = text.TrimStart();
        var firstLine = head.Split('\n', 2)[0].Trim();

        string[] prefixes = ["error:", "error ", "failed", "failure", "exception:", "unauthorized"];
        if (prefixes.Any(prefix => firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Códigos tipo NO_ACTIVE_DEVICE, PREMIUM_REQUIRED, RATE_LIMITED: mayúsculas y guión bajo.
        return firstLine.Length <= 80 &&
            firstLine.Contains('_') &&
            firstLine.Any(char.IsUpper) &&
            !firstLine.Any(char.IsLower);
    }

    /// <summary>Los nombres de herramienta viajan a la API con un alfabeto acotado.</summary>
    private static string Sanitize(string value) =>
        new(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());

    private static IReadOnlyDictionary<string, object> ConvertSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(schema.GetRawText()) ?? Empty()
            : Empty();

    private static Dictionary<string, object> Empty() => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>()
    };

    private static Dictionary<string, object?> ToArgumentDictionary(JsonElement arguments)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return payload;
        }

        foreach (var property in arguments.EnumerateObject())
        {
            payload[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var integer)
                    ? integer
                    : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText()
            };
        }

        return payload;
    }
}
