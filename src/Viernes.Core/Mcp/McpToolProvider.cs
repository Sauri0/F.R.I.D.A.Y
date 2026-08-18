using System.Text.Json;
using Viernes.Core.Tools;

namespace Viernes.Core.Mcp;

/// <summary>
/// Levanta los servidores MCP declarados, convierte sus herramientas en herramientas de Viernes y
/// los mantiene levantados.
/// </summary>
/// <remarks>
/// Es el cambio de escala del asistente: hasta acá cada capacidad era código escrito a mano, una por
/// una. Con esto, conectar un servidor agrega todo lo que ese servidor sepa hacer —Spotify de
/// verdad, el escritorio, lo que sea— sin tocar Viernes. El modelo sigue siendo el de OpenRouter;
/// MCP no aporta inteligencia, aporta manos.
/// <para>
/// Y las manos se pueden caer. Antes conectaba una sola vez al arrancar: si el proceso de Spotify se
/// moría, sus treinta herramientas desaparecían hasta reiniciar la aplicación, sin aviso. Ahora
/// queda un vigía latiendo que se entera de la caída aunque nadie pida nada, reconecta con espera
/// creciente y deja anotado cuándo se cayó y cuándo volvió.
/// </para>
/// </remarks>
public sealed class McpToolProvider : IAsyncDisposable
{
    /// <summary>Cada cuánto late el vigía.</summary>
    /// <remarks>
    /// Quince segundos es barato —un ping por servidor— y acota la ventana en la que una caída pasa
    /// desapercibida a menos de lo que tarda el usuario en volver a pedir algo.
    /// </remarks>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>El registro no crece para siempre: interesa lo último, no todo.</summary>
    private const int MaximumHistory = 200;

    private readonly List<McpServerConnection> _connections = [];
    private readonly List<string> _failures = [];
    private readonly List<McpConnectionEvent> _history = [];
    private readonly List<IAssistantTool> _tools = [];
    private readonly Lock _stateGate = new();

    private CancellationTokenSource? _supervisorCancellation;
    private Task? _supervisor;
    private bool _confirmActions;
    private bool _disposed;

    /// <summary>Servidores que no arrancaron, con el motivo. Se muestran en vez de fallar en silencio.</summary>
    /// <remarks>
    /// Es la foto del arranque, no una sentencia: cada uno de estos se sigue reintentando por atrás,
    /// y cuando levanta avisa por <see cref="ToolsRecovered"/>.
    /// </remarks>
    public IReadOnlyList<string> Failures
    {
        get
        {
            lock (_stateGate)
            {
                return [.. _failures];
            }
        }
    }

    /// <summary>Caídas y vueltas, en orden. Lo último primero de leer, lo viejo se descarta.</summary>
    public IReadOnlyList<McpConnectionEvent> History
    {
        get
        {
            lock (_stateGate)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>Todas las herramientas conocidas hasta ahora, incluidas las de servidores caídos.</summary>
    /// <remarks>
    /// Las de un servidor caído se mantienen declaradas a propósito: si desaparecieran, el modelo
    /// dejaría de saber que existen y contestaría «no puedo hacer eso» en vez de «Spotify está
    /// caído». Cuando el servidor vuelve, la misma herramienta funciona sin tocar nada.
    /// </remarks>
    public IReadOnlyList<IAssistantTool> Tools
    {
        get
        {
            lock (_stateGate)
            {
                return [.. _tools];
            }
        }
    }

    /// <summary>Cada caída y cada vuelta, apenas pasa.</summary>
    /// <remarks>
    /// Se dispara desde el hilo del vigía y mientras la conexión que lo produjo está tomada, así que
    /// el que escuche tiene que anotar y salir: un host con interfaz lo marshaliza, y nadie debería
    /// llamar de vuelta a la misma conexión desde acá adentro.
    /// </remarks>
    public event EventHandler<McpConnectionEvent>? ConnectionChanged;

    /// <summary>
    /// Aparecieron herramientas que antes no estaban: un servidor que no había levantado al
    /// arrancar, o uno que volvió trayendo más de las que se le conocían.
    /// </summary>
    /// <remarks>
    /// El host que quiera aprovecharlas tiene que rehacer el orquestador con
    /// <see cref="Tools"/>; el que no lo haga, no pierde nada de lo que ya tenía.
    /// </remarks>
    public event EventHandler<McpToolsRecoveredEventArgs>? ToolsRecovered;

    /// <summary>
    /// Conecta cada servidor habilitado, devuelve sus herramientas y deja andando al vigía. Un
    /// servidor que no levanta no impide a los demás: se anota el motivo y se sigue.
    /// </summary>
    public async Task<IReadOnlyList<IAssistantTool>> ConnectAsync(
        IReadOnlyList<McpServerDefinition> servers,
        bool confirmActions = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _confirmActions = confirmActions;

        foreach (var server in servers.Where(candidate => candidate.Enabled))
        {
            var definition = server;
            var connection = new McpServerConnection(
                definition.Name,
                token => StdioMcpSession.StartAsync(definition, token),
                report: Record);

            _connections.Add(connection);

            var descriptors = await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            RegisterTools(connection, descriptors);
        }

        StartSupervisor();
        return Tools;
    }

    /// <summary>
    /// Conecta contra sesiones ya armadas. Es el camino que usan las pruebas para poder matar y
    /// revivir un servidor sin levantar procesos de verdad.
    /// </summary>
    public async Task<IReadOnlyList<IAssistantTool>> ConnectSessionsAsync(
        IReadOnlyList<(string Name, Func<CancellationToken, Task<IMcpSession>> Connect)> sessions,
        bool confirmActions = false,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _confirmActions = confirmActions;

        foreach (var (name, connect) in sessions)
        {
            var connection = new McpServerConnection(name, connect, timeProvider, Record);
            _connections.Add(connection);

            var descriptors = await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            RegisterTools(connection, descriptors);
        }

        return Tools;
    }

    /// <summary>
    /// Un latido manual del vigía, para hosts que prefieran manejar ellos el reloj y para las
    /// pruebas. Devuelve <see langword="true"/> si aparecieron herramientas nuevas.
    /// </summary>
    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var recovered = false;
        foreach (var connection in _connections)
        {
            IReadOnlyList<McpToolDescriptor> descriptors;
            try
            {
                descriptors = await connection.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // El vigía no puede morirse por un servidor. Ya quedó anotado como caída.
                continue;
            }

            if (descriptors.Count == 0)
            {
                continue;
            }

            var fresh = RegisterTools(connection, descriptors);
            if (fresh.Count == 0)
            {
                continue;
            }

            recovered = true;
            ToolsRecovered?.Invoke(this, new McpToolsRecoveredEventArgs(connection.Name, fresh));
        }

        return recovered;
    }

    private void StartSupervisor()
    {
        if (_connections.Count == 0 || _supervisor is not null)
        {
            return;
        }

        _supervisorCancellation = new CancellationTokenSource();
        _supervisor = SuperviseAsync(_supervisorCancellation.Token);
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await HeartbeatAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Se está cerrando. Es la salida normal del bucle.
        }
    }

    private IReadOnlyList<IAssistantTool> RegisterTools(
        McpServerConnection connection,
        IReadOnlyList<McpToolDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return [];
        }

        var created = descriptors
            .Select(descriptor => (IAssistantTool)new McpBridgedTool(connection, descriptor, _confirmActions))
            .ToArray();

        lock (_stateGate)
        {
            _tools.AddRange(created);
        }

        return created;
    }

    private void Record(McpConnectionEvent connectionEvent)
    {
        lock (_stateGate)
        {
            _history.Add(connectionEvent);
            if (_history.Count > MaximumHistory)
            {
                _history.RemoveRange(0, _history.Count - MaximumHistory);
            }

            if (connectionEvent.State == McpConnectionState.NoLevanto)
            {
                _failures.Add($"{connectionEvent.Server}: {connectionEvent.Detail}");
            }
        }

        ConnectionChanged?.Invoke(this, connectionEvent);
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_supervisorCancellation is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_supervisor is { } supervisor)
        {
            try
            {
                await supervisor.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cerrar el vigía no puede impedir cerrar los servidores.
            }
        }

        _supervisorCancellation?.Dispose();

        foreach (var connection in _connections)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cerrar un servidor que ya murió no puede impedir cerrar los demás.
            }
        }

        _connections.Clear();
    }
}

/// <summary>Aparecieron herramientas de un servidor que antes no las estaba dando.</summary>
public sealed class McpToolsRecoveredEventArgs : EventArgs
{
    public McpToolsRecoveredEventArgs(string server, IReadOnlyList<IAssistantTool> tools)
    {
        Server = server;
        Tools = tools;
    }

    /// <summary>Qué servidor las trajo.</summary>
    public string Server { get; }

    /// <summary>Sólo las nuevas; las que ya se conocían no se repiten.</summary>
    public IReadOnlyList<IAssistantTool> Tools { get; }
}
