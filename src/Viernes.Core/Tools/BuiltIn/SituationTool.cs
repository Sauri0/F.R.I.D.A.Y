using System.Text.Json;
using Viernes.Core.Awareness;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Responde por el estado del equipo: dónde venías trabajando y cómo anda de carga.
/// </summary>
/// <remarks>
/// Lo barato —qué ventana tenés adelante— se inyecta en cada turno y no pasa por acá. Esto es para
/// lo que cuesta: recorrer procesos y medir memoria. Separarlo es la diferencia entre un asistente
/// que sabe dónde estás y uno que audita la máquina entera cada vez que le decís hola.
/// </remarks>
public sealed class SituationTool : IAssistantTool
{
    public const string ToolName = "estado_equipo";

    private readonly IEnvironmentObserver? _observer;

    public SituationTool(IEnvironmentObserver? observer = null) => _observer = observer;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Consulta el estado de la computadora. aspecto=«actividad» cuenta en qué venías trabajando " +
        "y por qué ventanas pasaste —usalo para «¿dónde estaba?», «seguí con lo de antes»—. " +
        "aspecto=«carga» informa procesos y memoria —usalo para «¿por qué anda lenta?»—. " +
        "No cambia nada: sólo mira.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["aspecto"] = ToolSchemas.String("actividad o carga.")
            },
            ["aspecto"]),
        ToolRiskLevel.Safe);

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var aspect = JsonToolArguments.RequiredString(arguments, "aspecto", 40).ToLowerInvariant();

        if (_observer is null)
        {
            return Task.FromResult(ToolExecutionResult.Failure(
                context.ToolCallId,
                ToolName,
                "No tengo forma de observar el equipo en este host."));
        }

        var message = aspect.StartsWith("carg", StringComparison.Ordinal) ||
            aspect.Contains("lent", StringComparison.Ordinal) ||
            aspect.Contains("rendim", StringComparison.Ordinal)
            ? _observer.DescribeSystemLoad()
            : _observer.DescribeRecentActivity();

        return Task.FromResult(ToolExecutionResult.Success(context.ToolCallId, ToolName, message));
    }
}
