using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public sealed class AgendaCreateTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "agenda_create";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Agrega un evento a la agenda local de Viernes.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["title"] = ToolSchemas.String("Título del evento."),
                ["starts_at"] = ToolSchemas.String("Inicio ISO 8601 con zona horaria.", "date-time"),
                ["ends_at"] = ToolSchemas.String("Fin opcional ISO 8601 con zona horaria.", "date-time"),
                ["notes"] = ToolSchemas.String("Notas opcionales, sin información secreta.")
            },
            ["title", "starts_at"]));

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var title = JsonToolArguments.RequiredString(arguments, "title", 200);
        var startsAt = JsonToolArguments.RequiredDateTimeOffset(arguments, "starts_at");
        var endsAt = JsonToolArguments.OptionalDateTimeOffset(arguments, "ends_at");
        var notes = JsonToolArguments.OptionalString(arguments, "notes", 2_000);
        SensitiveContentGuard.RejectCredentialLikeContent(title, "title");
        SensitiveContentGuard.RejectCredentialLikeContent(notes, "notes");
        var item = await dataStore.AddAgendaItemAsync(title, startsAt, endsAt, notes, cancellationToken)
            .ConfigureAwait(false);

        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            "Evento guardado en la agenda local.",
            new { item.Id, item.Title, item.StartsAt, item.EndsAt });
    }
}
