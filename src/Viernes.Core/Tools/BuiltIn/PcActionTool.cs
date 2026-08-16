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
    public PcActionTool(IPcActionExecutor? executor = null) => _executor = executor;

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

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Previsualiza una acción de PC. No ejecuta comandos ni cambios reales en este MVP.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["action"] = ToolSchemas.String("Acción solicitada."),
                ["target"] = ToolSchemas.String("Destino opcional de la acción.")
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
