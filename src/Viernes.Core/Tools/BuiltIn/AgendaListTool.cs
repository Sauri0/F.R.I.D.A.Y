using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

public sealed class AgendaListTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "agenda_list";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Lista la agenda local de Viernes.",
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

        var items = await dataStore.GetAgendaItemsAsync(cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            items.Count == 0 ? "La agenda está vacía." : $"Hay {items.Count} eventos.",
            items);
    }
}
