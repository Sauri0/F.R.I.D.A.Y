using System.Text;
using System.Text.Json;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;

namespace Viernes.Core.Learning;

/// <summary>
/// Deja que lo aprendido se apruebe hablando, que es la única forma en que iba a aprobarse.
/// </summary>
/// <remarks>
/// Sin esto, la destilación de cada charla escribía observaciones temporales que vencían a los siete
/// días sin que nadie pudiera confirmarlas: aprendía todas las noches y se olvidaba todas las
/// noches. La aprobación siempre tuvo que ser explícita —eso no cambia— pero antes no había ninguna
/// puerta por la que darla.
/// <para>
/// Se registra como herramienta extra, no de fábrica: la memoria personal es opcional y el host
/// decide si la enciende.
/// </para>
/// </remarks>
public sealed class MemoryTool : IAssistantTool
{
    public const string ToolName = "memoria";

    private readonly MemoryApprovals _approvals;

    public MemoryTool(MemoryApprovals approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        _approvals = approvals;
    }

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Lo que Viernes sabe del usuario y lo que cree haber notado y todavía no está confirmado. " +
        "accion=«pendientes» para ver lo que está esperando una decisión: son cosas que destilaste " +
        "al cerrar charlas anteriores y que vencen solas si nadie las confirma. " +
        "accion=«aprobar» cuando el usuario confirma una de ésas —«sí, es cierto», «acordate de " +
        "eso»—: pasá en «cual» el identificador corto o parte de lo que dice. Si hay una sola " +
        "pendiente podés no pasar nada. " +
        "accion=«rechazar» cuando dice que no es así. " +
        "accion=«recordar» cuando el usuario te pide explícitamente que guardes algo: pasá el hecho " +
        "en «texto», corto y en tercera persona. " +
        "accion=«olvidar» para borrar cualquier dato, pasando «cual». " +
        "accion=«listar» para contarle todo lo que tenés guardado de él. " +
        "NUNCA apruebes ni guardes nada por tu cuenta: sólo cuando el usuario lo dijo. Y no des por " +
        "cierto lo pendiente mientras siga pendiente.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String(
                    "pendientes, aprobar, rechazar, recordar, olvidar o listar."),
                ["cual"] = ToolSchemas.String("Identificador corto o parte de lo que dice el recuerdo."),
                ["texto"] = ToolSchemas.String("El hecho a guardar, al recordar.")
            },
            ["accion"]),
        ToolRiskLevel.Safe);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "accion", 20).ToLowerInvariant();
        var which = JsonToolArguments.OptionalString(arguments, "cual", 200);
        var text = JsonToolArguments.OptionalString(arguments, "texto", 400);

        try
        {
            return action switch
            {
                "pendientes" or "sugerencias" =>
                    await PendingAsync(context, cancellationToken).ConfigureAwait(false),

                "aprobar" or "confirmar" => Report(
                    context,
                    await _approvals.ApproveAsync(which, cancellationToken).ConfigureAwait(false)),

                "rechazar" or "descartar" => Report(
                    context,
                    await _approvals.RejectAsync(which, cancellationToken).ConfigureAwait(false)),

                "recordar" or "guardar" => Report(
                    context,
                    await _approvals.RememberAsync(text ?? string.Empty, cancellationToken)
                        .ConfigureAwait(false)),

                "olvidar" or "borrar" => Report(
                    context,
                    await _approvals.ForgetAsync(which ?? text, cancellationToken).ConfigureAwait(false)),

                "listar" or "revisar" =>
                    await ListAsync(context, cancellationToken).ConfigureAwait(false),

                _ => Fail(context, "No conozco esa acción sobre la memoria.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Se informa el fallo real. Contestar «guardado» sobre un archivo que no aceptó la
            // escritura es cómo un recuerdo desaparece sin que nadie se entere.
            return Fail(context, $"No pude tocar la memoria: {exception.Message}");
        }
    }

    private async Task<ToolExecutionResult> PendingAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var pending = await _approvals.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return Ok(context, "No tengo nada pendiente de confirmar.");
        }

        var builder = new StringBuilder(
            $"Tengo {pending.Count} cosa{(pending.Count == 1 ? "" : "s")} sin confirmar:");
        foreach (var item in pending.Take(10))
        {
            builder.AppendLine();
            builder.Append($"[{item.ShortId}] {item.Content}");
        }

        return Ok(context, builder.ToString());
    }

    private async Task<ToolExecutionResult> ListAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var described = await _approvals.DescribeForPromptAsync(cancellationToken).ConfigureAwait(false);
        return Ok(context, described ?? "Todavía no tengo nada guardado de vos.");
    }

    private static ToolExecutionResult Report(ToolExecutionContext context, MemoryApprovalOutcome outcome) =>
        outcome.Succeeded ? Ok(context, outcome.Message) : Fail(context, outcome.Message);

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
