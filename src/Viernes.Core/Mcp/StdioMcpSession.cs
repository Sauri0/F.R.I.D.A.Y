using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Viernes.Core.Mcp;

/// <summary>
/// La sesión de verdad: un proceso hijo hablando MCP por entrada y salida estándar.
/// </summary>
/// <remarks>
/// Es el único archivo que conoce el SDK. Todo lo de arriba —supervisión, espera creciente,
/// herramientas puenteadas— trabaja contra <see cref="IMcpSession"/> y no se entera de si del otro
/// lado hay un <c>npx</c>, una prueba o cualquier otra cosa.
/// </remarks>
public sealed class StdioMcpSession : IMcpSession
{
    private readonly McpClient _client;

    private StdioMcpSession(McpClient client) => _client = client;

    /// <summary>
    /// Levanta el proceso del servidor y completa el saludo inicial de MCP.
    /// </summary>
    /// <remarks>
    /// El tiempo máximo lo pone quien llama, con el token. Antes no había ninguno: un ejecutable que
    /// arrancaba y se quedaba mudo dejaba colgado el arranque entero del asistente sin decir nada.
    /// </remarks>
    public static async Task<IMcpSession> StartAsync(
        McpServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = [.. server.Arguments],
            EnvironmentVariables = ResolveEnvironment(server)
        });

        var client = await McpClient
            .CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new StdioMcpSession(client);
    }

    /// <summary>
    /// Resuelve los secretos desde el entorno del proceso.
    /// </summary>
    /// <remarks>
    /// La configuración guarda el <em>nombre</em> de la variable, nunca su valor: el archivo de
    /// servidores se puede leer, versionar y compartir sin que salga ninguna credencial, igual que
    /// ya pasa con la clave de OpenRouter.
    /// </remarks>
    private static Dictionary<string, string?> ResolveEnvironment(McpServerDefinition server)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, variableName) in server.Environment)
        {
            resolved[key] = Environment.GetEnvironmentVariable(variableName);
        }

        return resolved;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. tools.Select(tool => new McpToolDescriptor(
            tool.Name,
            tool.Description ?? string.Empty,
            tool.JsonSchema))];
    }

    public async Task<McpToolCallOutcome> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var response = await _client
            .CallToolAsync(toolName, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var text = string.Join(
            Environment.NewLine,
            response.Content
                .OfType<TextContentBlock>()
                .Select(block => block.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return new McpToolCallOutcome(response.IsError == true, text);
    }

    public async Task PingAsync(CancellationToken cancellationToken = default) =>
        await _client.PingAsync(options: null, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync() =>
        await _client.DisposeAsync().ConfigureAwait(false);
}
