using System.Text.Json;
using ModelContextProtocol.Client;
using Viernes.Core.Tools;

namespace Viernes.Core.Mcp;

/// <summary>
/// Levanta los servidores MCP declarados y convierte sus herramientas en herramientas de Viernes.
/// </summary>
/// <remarks>
/// Es el cambio de escala del asistente: hasta acá cada capacidad era código escrito a mano, una por
/// una. Con esto, conectar un servidor agrega todo lo que ese servidor sepa hacer —Spotify de
/// verdad, el escritorio, lo que sea— sin tocar Viernes. El modelo sigue siendo el de OpenRouter;
/// MCP no aporta inteligencia, aporta manos.
/// </remarks>
public sealed class McpToolProvider : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];

    /// <summary>Servidores que no arrancaron, con el motivo. Se muestran en vez de fallar en silencio.</summary>
    public IReadOnlyList<string> Failures => _failures;

    private readonly List<string> _failures = [];

    /// <summary>
    /// Conecta cada servidor habilitado y devuelve sus herramientas. Un servidor que no levanta no
    /// impide a los demás: se anota el motivo y se sigue.
    /// </summary>
    public async Task<IReadOnlyList<IAssistantTool>> ConnectAsync(
        IReadOnlyList<McpServerDefinition> servers,
        bool confirmActions = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var tools = new List<IAssistantTool>();

        foreach (var server in servers.Where(candidate => candidate.Enabled))
        {
            try
            {
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
                _clients.Add(client);

                foreach (var tool in await client.ListToolsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    tools.Add(new McpBridgedTool(client, tool, server.Name, confirmActions));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _failures.Add($"{server.Name}: {exception.Message}");
            }
        }

        return tools;
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

    /// <summary>
    /// Lee la lista de servidores. Si no existe el archivo, no hay servidores y no es un error:
    /// Viernes funciona igual, sólo que con las capacidades que trae de fábrica.
    /// </summary>
    public static async Task<IReadOnlyList<McpServerDefinition>> LoadAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        path ??= DefaultConfigurationPath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var servers = await JsonSerializer
                .DeserializeAsync<List<McpServerDefinition>>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken)
                .ConfigureAwait(false);
            return servers ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string DefaultConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "servidores-mcp.json");

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cerrar un servidor que ya murió no puede impedir cerrar los demás.
            }
        }

        _clients.Clear();
    }
}
