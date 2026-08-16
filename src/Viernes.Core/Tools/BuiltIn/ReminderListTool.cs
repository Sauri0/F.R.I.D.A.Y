using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public sealed class ReminderListTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "reminder_list";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Lista los recordatorios almacenados localmente.",
        ToolSchemas.Object(new Dictionary<string, object>()));

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Los argumentos deben ser un objeto JSON.", nameof(arguments));
        }

        var reminders = await dataStore.GetRemindersAsync(cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            reminders.Count == 0 ? "No hay recordatorios." : $"Hay {reminders.Count} recordatorios.",
            reminders);
    }
}
