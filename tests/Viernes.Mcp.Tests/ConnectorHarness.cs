using System.Text;
using System.Text.Json;
using Viernes.Core.Autonomy;
using Viernes.Core.Missions;
using Viernes.Core.Projects;
using Viernes.Core.Usage;
using Viernes.Memory;
using Viernes.Memory.Persistence;
using Viernes.Mcp;

namespace Viernes.Mcp.Tests;

/// <summary>
/// Un conector entero sobre archivos de mentira.
/// </summary>
/// <remarks>
/// Todas las rutas se inyectan y viven en una carpeta temporal que se borra al terminar: ninguna
/// prueba puede tocar las misiones, la memoria ni los permisos reales del usuario. El libro de
/// gastos va directamente en memoria, que ya sabe hacerlo.
/// </remarks>
internal sealed class ConnectorHarness : IDisposable
{
    private readonly string _root;

    public ConnectorHarness()
    {
        _root = Path.Combine(Path.GetTempPath(), $"viernes-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(SessionsRoot);

        Now = DateTimeOffset.Now;
        Missions = new MissionBook(Path.Combine(_root, "misiones.json"));
        Memory = new JsonPersonalMemoryStore(Path.Combine(_root, "memoria.json"));
        Autonomy = new AutonomyPolicy(Path.Combine(_root, "autonomia.json"));
        Sessions = new ClaudeSessionWatcher(SessionsRoot);
        Usage = UsageLedger.CreateInMemory(new UsageBudgetConfiguration());

        Connector = new ViernesConnector(
            Missions,
            Memory,
            Sessions,
            new ClaudeSessionWriter(Sessions),
            Usage,
            new ConnectorBoundary(Autonomy),
            new FixedClock(Now));
    }

    public DateTimeOffset Now { get; }

    public string SessionsRoot => Path.Combine(_root, "claude-projects");

    public MissionBook Missions { get; }

    public IPersonalMemoryStore Memory { get; }

    public AutonomyPolicy Autonomy { get; }

    public ClaudeSessionWatcher Sessions { get; }

    public UsageLedger Usage { get; }

    public ViernesConnector Connector { get; }

    /// <summary>
    /// Fabrica un archivo de sesión de Claude Code como el que deja la aplicación de verdad.
    /// </summary>
    /// <param name="project">Carpeta sobre la que correría la sesión.</param>
    /// <param name="sessionId">Identificador de la sesión.</param>
    /// <param name="working">
    /// <see langword="true"/> deja el último mensaje a mitad de una herramienta —o sea, trabajando—;
    /// <see langword="false"/> lo deja cerrando el turno, que es lo que se lee como «te espera».
    /// </param>
    /// <param name="said">Lo último que dijo en texto.</param>
    /// <param name="ago">Hace cuánto fue esa última línea.</param>
    public void WriteSession(
        string project,
        string sessionId,
        bool working,
        string said = "listo",
        TimeSpan? ago = null)
    {
        var moment = Now - (ago ?? TimeSpan.FromMinutes(1));
        var folder = Path.Combine(SessionsRoot, sessionId);
        Directory.CreateDirectory(folder);

        var user = new
        {
            type = "user",
            cwd = project,
            sessionId,
            gitBranch = "main",
            timestamp = moment.AddMinutes(-2).ToString("O"),
            message = new { role = "user", content = "seguí con eso" }
        };

        object assistantContent = working
            ? new object[] { new { type = "tool_use", name = "Read" } }
            : new object[] { new { type = "text", text = said } };

        var assistant = new
        {
            type = "assistant",
            cwd = project,
            sessionId,
            gitBranch = "main",
            timestamp = moment.ToString("O"),
            message = new
            {
                role = "assistant",
                stop_reason = working ? "tool_use" : "end_turn",
                content = assistantContent
            }
        };

        var lines = new StringBuilder();
        lines.AppendLine(JsonSerializer.Serialize(user));
        lines.AppendLine(JsonSerializer.Serialize(assistant));
        File.WriteAllText(Path.Combine(folder, $"{sessionId}.jsonl"), lines.ToString(), Encoding.UTF8);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Una carpeta temporal que no se pudo borrar no puede hacer fallar una prueba que pasó.
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
