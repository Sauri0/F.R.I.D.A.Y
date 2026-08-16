using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>MVP placeholder: reports the planned query but performs no network request.</summary>
public sealed class WebSearchTool : IAssistantTool
{
    public const string ToolName = "web_search";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Prepara una búsqueda web segura. En el MVP funciona en modo simulado.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["query"] = ToolSchemas.String("Consulta de búsqueda, sin credenciales ni secretos.")
            },
            ["query"]));

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = JsonToolArguments.RequiredString(arguments, "query", 500);
        return Task.FromResult(ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            "Búsqueda preparada en modo simulado; no se realizó ninguna solicitud de red.",
            new { query, simulated = true }));
    }
}
