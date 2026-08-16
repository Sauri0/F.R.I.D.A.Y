using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public sealed class ReminderCreateTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "reminder_create";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Crea un recordatorio local pedido por el usuario.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["title"] = ToolSchemas.String("Texto breve del recordatorio."),
                ["due_at"] = ToolSchemas.String("Fecha y hora ISO 8601 con zona horaria.", "date-time")
            },
            ["title", "due_at"]));

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var title = JsonToolArguments.RequiredString(arguments, "title", 200);
        SensitiveContentGuard.RejectCredentialLikeContent(title, "title");
        var dueAt = JsonToolArguments.RequiredDateTimeOffset(arguments, "due_at");
        var reminder = await dataStore.AddReminderAsync(title, dueAt, cancellationToken).ConfigureAwait(false);

        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            "Recordatorio guardado localmente.",
            new { reminder.Id, reminder.Title, reminder.DueAt });
    }
}
