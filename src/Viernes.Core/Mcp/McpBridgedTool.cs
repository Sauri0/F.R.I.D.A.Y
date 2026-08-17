using System.Text.Json;
using ModelContextProtocol.Client;
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
/// </remarks>
public sealed class McpBridgedTool : IAssistantTool
{
    private readonly McpClient _client;
    private readonly string _remoteName;

    public McpBridgedTool(McpClient client, McpClientTool tool, string serverName, bool confirmActions = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        _client = client;
        _remoteName = tool.Name;

        // El nombre se prefija con el del servidor: dos servidores pueden traer una herramienta
        // llamada «search» y el modelo tiene que poder distinguirlas.
        Definition = ToolDefinition.Create(
            Sanitize($"{serverName}_{tool.Name}"),
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"Herramienta «{tool.Name}» del servidor {serverName}."
                : tool.Description,
            ConvertSchema(tool.JsonSchema),
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
            var response = await _client
                .CallToolAsync(_remoteName, payload, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = string.Join(
                Environment.NewLine,
                response.Content
                    .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                    .Select(block => block.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (string.IsNullOrWhiteSpace(text))
            {
                text = response.IsError == true
                    ? "El servidor informó un error sin detalle."
                    : "Listo.";
            }

            return response.IsError == true
                ? ToolExecutionResult.Failure(context.ToolCallId, Definition.Name, text)
                : ToolExecutionResult.Success(context.ToolCallId, Definition.Name, text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
