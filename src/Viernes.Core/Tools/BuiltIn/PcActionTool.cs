using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// PC capability preview. It never invokes the operating system. Benign previews require consent;
/// sensitive/destructive requests stay permanently pending under <see cref="SafeToolPolicy"/>.
/// </summary>
public sealed class PcActionTool : IAssistantTool
{
    public const string ToolName = "pc_action";

    private readonly IPcActionExecutor? _executor;

    /// <summary>
    /// Sin ejecutor la herramienta previsualiza y nada más. Con ejecutor, las acciones previsualizables
    /// se ejecutan de verdad —pero recién después de que la política las haya dejado pasar.
    /// </summary>
    public PcActionTool(IPcActionExecutor? executor = null)
    {
        _executor = executor;
        Definition = BuildDefinition();
    }

    private static readonly HashSet<string> DestructiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_file", "delete_folder", "format_disk", "wipe", "uninstall"
    };

    private static readonly HashSet<string> SensitiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "restart", "logoff", "lock", "kill_process", "run_command", "change_setting"
    };

    private static readonly HashSet<string> PreviewableActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "open_settings", "open_application", "show_desktop"
    };

    public ToolDefinition Definition { get; }

    /// <summary>
    /// La descripción tiene que decir la verdad de lo que la herramienta hace hoy: mientras dijo
    /// «previsualiza, no ejecuta», el modelo asumía que no podía actuar y ni siquiera la ofrecía.
    /// </summary>
    private ToolDefinition BuildDefinition() => ToolDefinition.Create(
        ToolName,
        _executor is null
            ? "Previsualiza una acción de PC. No ejecuta comandos ni cambios reales."
            : "Ejecuta acciones de Windows, tras confirmación del usuario. " +
              "open_settings abre Configuración —target: sonido, pantalla, bluetooth, red, wifi, " +
              "batería, micrófono, privacidad, aplicaciones, notificaciones, inicio—. " +
              "open_application abre CUALQUIER aplicación instalada: pasá su nombre común en " +
              "target (por ejemplo «spotify», «steam», «visual studio code», «discord»). " +
              "show_desktop muestra el escritorio. " +
              "No hay shell, comandos arbitrarios, borrado, apagado ni cambios de configuración.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["action"] = ToolSchemas.String("open_settings, open_application o show_desktop."),
                ["target"] = ToolSchemas.String("Destino, de la lista permitida en la descripción.")
            },
            ["action"]),
        ToolRiskLevel.RequiresConfirmation);

    public ToolRiskLevel AssessRisk(JsonElement arguments)
    {
        var action = JsonToolArguments.RequiredString(arguments, "action", 80);
        if (DestructiveActions.Contains(action))
        {
            return ToolRiskLevel.Destructive;
        }

        if (SensitiveActions.Contains(action) || !PreviewableActions.Contains(action))
        {
            return ToolRiskLevel.Sensitive;
        }

        return ToolRiskLevel.RequiresConfirmation;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "action", 80);
        var target = JsonToolArguments.OptionalString(arguments, "target", 260);

        // Doble llave: aunque llegue hasta acá, sólo se ejecuta lo previsualizable. Las acciones
        // sensibles y destructivas ya fueron detenidas por la política y nunca alcanzan esta línea.
        if (_executor is null ||
            !PreviewableActions.Contains(action) ||
            !_executor.SupportedActions.Contains(action.ToLowerInvariant()))
        {
            return ToolExecutionResult.Success(
                context.ToolCallId,
                ToolName,
                "Acción aprobada y simulada; no se modificó la PC.",
                new { action, target, simulated = true });
        }

        var outcome = await _executor.ExecuteAsync(action, target, cancellationToken).ConfigureAwait(false);
        return outcome.Executed
            ? ToolExecutionResult.Success(
                context.ToolCallId,
                ToolName,
                outcome.Message,
                new { action, target, simulated = false })
            : ToolExecutionResult.Failure(context.ToolCallId, ToolName, outcome.Message);
    }
}
