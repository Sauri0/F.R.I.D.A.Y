using System.Text.Json;
using Viernes.Core.Goals;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Abre, avanza y cierra objetivos que duran más que la conversación.
/// </summary>
/// <remarks>
/// Sin esto, «seguí con eso» al día siguiente no tiene referente: cada charla arrancaba de cero y lo
/// anterior se perdía al cerrarla. Los objetivos son lo que convierte una serie de pedidos sueltos
/// en algo que se retoma.
/// <para>
/// Se abren sólo cuando el usuario lo pide. Inferirlos de la conversación produce un asistente que
/// persigue metas que nadie declaró — y que después te las recuerda.
/// </para>
/// </remarks>
public sealed class GoalTool : IAssistantTool
{
    public const string ToolName = "objetivo";

    private readonly GoalBook _goals;

    public GoalTool(GoalBook goals) => _goals = goals;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Maneja objetivos que sobreviven a que se cierre la conversación. " +
        "accion=«abrir» cuando el usuario declara algo en lo que va a trabajar en el tiempo " +
        "—«quiero terminar X», «estoy armando Y», «acordate que estoy con Z»—: pasá qué en " +
        "«texto» y opcionalmente el próximo paso en «paso». " +
        "accion=«avanzar» para anotar novedades de uno existente; sin «id» cae sobre el último. " +
        "accion=«cerrar» cuando lo terminó, o «abandonar» si lo deja. " +
        "accion=«listar» para contarle qué tiene abierto. " +
        "NO abras objetivos por pedidos puntuales —abrir una app, poner música— sólo por cosas " +
        "que continúan en el tiempo. " +
        "OJO, no confundir con «mision»: un OBJETIVO es algo que trabaja EL USUARIO y vos recordás " +
        "—«estoy armando el presupuesto»—; una MISIÓN es algo que hacés VOS y le reportás —«seguí " +
        "este proyecto y avisame»—. Si el trabajo es suyo, objetivo. Si el trabajo es tuyo, misión.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String("abrir, avanzar, cerrar, abandonar o listar."),
                ["texto"] = ToolSchemas.String("El objetivo, o la novedad al avanzar."),
                ["paso"] = ToolSchemas.String("Próximo paso concreto."),
                ["id"] = ToolSchemas.String("Identificador corto, si se refiere a uno en particular.")
            },
            ["accion"]),
        ToolRiskLevel.Safe);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var action = JsonToolArguments.RequiredString(arguments, "accion", 20).ToLowerInvariant();
        var text = JsonToolArguments.OptionalString(arguments, "texto", 200);
        var step = JsonToolArguments.OptionalString(arguments, "paso", 200);
        var id = JsonToolArguments.OptionalString(arguments, "id", 20);

        return action switch
        {
            "abrir" when !string.IsNullOrWhiteSpace(text) =>
                Ok(context, $"Anotado como objetivo [{(await _goals.OpenAsync(text, step, cancellationToken).ConfigureAwait(false)).Id}]: {text}."),

            "abrir" => Fail(context, "Necesito saber cuál es el objetivo."),

            "avanzar" when !string.IsNullOrWhiteSpace(text) =>
                await ProgressAsync(context, text, id, step, cancellationToken).ConfigureAwait(false),

            "avanzar" => Fail(context, "Necesito saber qué anotar."),

            "cerrar" or "abandonar" =>
                await CloseAsync(context, id, action == "abandonar", cancellationToken).ConfigureAwait(false),

            "listar" =>
                Ok(context, await _goals.DescribeOpenAsync(cancellationToken).ConfigureAwait(false)
                    ?? "No tenés ningún objetivo abierto."),

            _ => Fail(context, "Acción no reconocida.")
        };
    }

    private async Task<ToolExecutionResult> ProgressAsync(
        ToolExecutionContext context,
        string note,
        string? id,
        string? step,
        CancellationToken cancellationToken)
    {
        var goal = await _goals.RecordProgressAsync(note, id, step, cancellationToken).ConfigureAwait(false);
        return goal is null
            ? Fail(context, "No encontré ese objetivo abierto.")
            : Ok(context, $"Anotado en [{goal.Id}] {goal.Description}.");
    }

    private async Task<ToolExecutionResult> CloseAsync(
        ToolExecutionContext context,
        string? id,
        bool abandoned,
        CancellationToken cancellationToken)
    {
        var goal = await _goals.CloseAsync(id, abandoned, cancellationToken).ConfigureAwait(false);
        return goal is null
            ? Fail(context, "No encontré ese objetivo abierto.")
            : Ok(context, abandoned
                ? $"Dejé de seguir «{goal.Description}»."
                : $"Cerrado: {goal.Description}.");
    }

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
