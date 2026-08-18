using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Lista los recordatorios locales.
/// </summary>
/// <remarks>
/// Los cumplidos quedan fuera salvo que se pidan. Desde que existe <c>reminder_update</c> hay una
/// forma de darlos por hechos, y seguir mostrándolos haría que completarlos no se notara en ningún
/// lado: la lista se leería igual de larga que antes y el usuario concluiría, con razón, que
/// marcarlos como listos no sirve para nada.
/// </remarks>
public sealed class ReminderListTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "reminder_list";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Lista los recordatorios pendientes almacenados localmente, con su id para poder " +
        "completarlos o borrarlos después. include_completed=true suma los que ya están hechos.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["include_completed"] = new Dictionary<string, object>
                {
                    ["type"] = "boolean",
                    ["description"] = "true para incluir también los recordatorios ya cumplidos."
                }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Los argumentos deben ser un objeto JSON.", nameof(arguments));
        }

        var includeCompleted = arguments.TryGetProperty("include_completed", out var flag) &&
                               flag.ValueKind == JsonValueKind.True;

        var stored = await dataStore.GetRemindersAsync(cancellationToken).ConfigureAwait(false);
        var reminders = includeCompleted
            ? stored
            : stored.Where(item => !item.IsCompleted).ToArray();

        var completed = stored.Count - reminders.Count;
        var message = reminders.Count == 0
            ? completed == 0 ? "No hay recordatorios." : "No queda ninguno pendiente."
            : $"Hay {reminders.Count} recordatorios.";

        return ToolExecutionResult.Success(context.ToolCallId, ToolName, message, reminders);
    }
}
