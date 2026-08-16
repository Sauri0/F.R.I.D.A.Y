using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// PC capability preview. It never invokes the operating system. Benign previews require consent;
/// sensitive/destructive requests stay permanently pending under <see cref="SafeToolPolicy"/>.
/// </summary>
public sealed class PcActionTool : IAssistantTool
{
    public const string ToolName = "pc_action";

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

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "action", 80);
        var target = JsonToolArguments.OptionalString(arguments, "target", 260);

        // There is deliberately no Process.Start, shell, filesystem mutation, or Win32 call here.
        return Task.FromResult(ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            "Acción aprobada y simulada; no se modificó la PC.",
            new { action, target, simulated = true }));
    }
}
