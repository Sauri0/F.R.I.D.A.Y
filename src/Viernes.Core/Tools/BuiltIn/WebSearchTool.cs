using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Con la búsqueda web habilitada, el proveedor ya inyecta resultados frescos en el contexto del
/// turno: el modelo no necesita llamar a ninguna herramienta para buscar. Esta queda para decirlo
/// con todas las letras en vez de devolver una simulación que parece un resultado.
/// </summary>
public sealed class WebSearchTool : IAssistantTool
{
    public const string ToolName = "web_search";

    private readonly bool _providerSearchEnabled;

    public WebSearchTool(bool providerSearchEnabled = false)
    {
        _providerSearchEnabled = providerSearchEnabled;
        Definition = ToolDefinition.Create(
            ToolName,
            providerSearchEnabled
                ? "No hace falta: ya tenés resultados web actualizados en el contexto de este turno."
                : "Prepara una búsqueda web. La búsqueda real está desactivada; devuelve modo simulado.",
            ToolSchemas.Object(
                new Dictionary<string, object>
                {
                    ["query"] = ToolSchemas.String("Consulta de búsqueda, sin credenciales ni secretos.")
                },
                ["query"]));
    }

    public ToolDefinition Definition { get; }

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = JsonToolArguments.RequiredString(arguments, "query", 500);

        return Task.FromResult(_providerSearchEnabled
            ? ToolExecutionResult.Success(
                context.ToolCallId,
                ToolName,
                "La búsqueda web ya viene resuelta en este turno; respondé con esos resultados.",
                new { query, providerSearch = true })
            : ToolExecutionResult.Success(
                context.ToolCallId,
                ToolName,
                "La búsqueda web está desactivada; no se hizo ninguna solicitud de red.",
                new { query, simulated = true }));
    }
}
