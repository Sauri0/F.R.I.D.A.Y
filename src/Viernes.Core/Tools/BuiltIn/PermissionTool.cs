using System.Text.Json;
using Viernes.Core.Autonomy;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Guarda cuánta libertad le dio el usuario para cada cosa y con cada persona.
/// </summary>
/// <remarks>
/// Es lo que hace que «a este contestale sola» valga mañana también. Sin esto, cada permiso dura lo
/// que dura la conversación y el usuario termina repitiendo la misma autorización todos los días
/// —que es exactamente el trámite que un asistente tiene que sacarle de encima—.
/// </remarks>
public sealed class PermissionTool : IAssistantTool
{
    public const string ToolName = "permiso";

    private readonly AutonomyPolicy _policy;

    public PermissionTool(AutonomyPolicy policy) => _policy = policy;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Guarda hasta dónde podés llegar sola con cada acción y cada persona, para que valga de acá " +
        "en adelante. Usala apenas el usuario te diga cómo quiere que manejes algo: «a Ana " +
        "contestale sola», «los mails de facturación siempre preguntame», «nunca le escribas a mi " +
        "jefe sin mostrarme». " +
        "accion=«guardar»: pasá «que» (la acción: enviar, responder, publicar, borrar), «quien» (a " +
        "quién o sobre qué: un mail, un dominio, un nombre; vacío = cualquiera) y «nivel» " +
        "(automatico, preguntar o nunca). " +
        "accion=«listar» para contarle qué permisos tiene guardados. " +
        "Recordá cómo funciona por defecto: leer, buscar, clasificar y dejar borradores no se " +
        "pregunta nunca; mandar, publicar, borrar y pagar se preguntan siempre salvo que haya un " +
        "permiso guardado que diga otra cosa.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String("guardar o listar."),
                ["que"] = ToolSchemas.String("La acción: enviar, responder, publicar, borrar…"),
                ["quien"] = ToolSchemas.String("A quién o sobre qué. Vacío significa cualquiera."),
                ["nivel"] = ToolSchemas.String("automatico, preguntar o nunca."),
                ["porque"] = ToolSchemas.String("Con qué palabras lo dijo el usuario.")
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

        try
        {
            if (action == "listar")
            {
                var described = await _policy.DescribeAsync(cancellationToken).ConfigureAwait(false);
                return Ok(context, described ?? "Todavía no me diste ningún permiso especial.");
            }

            if (action != "guardar")
            {
                return Fail(context, "No conozco esa acción sobre permisos.");
            }

            var what = JsonToolArguments.OptionalString(arguments, "que", 80);
            if (string.IsNullOrWhiteSpace(what))
            {
                return Fail(context, "Necesito saber sobre qué acción.");
            }

            var rawLevel = JsonToolArguments.OptionalString(arguments, "nivel", 20)?.ToLowerInvariant();
            var level = rawLevel switch
            {
                "automatico" or "automático" or "solo" or "sola" or "si" => AutonomyLevel.Automatico,
                "nunca" or "jamas" or "jamás" or "no" => AutonomyLevel.Nunca,
                "preguntar" or "consultar" => AutonomyLevel.Preguntar,

                // Un nivel que no se entiende cae del lado seguro. Interpretar mal un permiso hacia
                // «hacelo solo» es la única forma de equivocarse que le cuesta algo al usuario.
                _ => AutonomyLevel.Preguntar
            };

            var who = JsonToolArguments.OptionalString(arguments, "quien", 200);
            await _policy.LearnAsync(
                what,
                who,
                level,
                JsonToolArguments.OptionalString(arguments, "porque", 200),
                cancellationToken).ConfigureAwait(false);

            var conQuien = string.IsNullOrWhiteSpace(who) ? "con cualquiera" : $"con «{who}»";
            return Ok(context, level switch
            {
                AutonomyLevel.Automatico => $"Listo: {what} {conQuien} lo hago sola de acá en adelante.",
                AutonomyLevel.Nunca => $"Anotado: {what} {conQuien} no lo hago nunca.",
                _ => $"Anotado: {what} {conQuien} te pregunto siempre."
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(context, $"No pude guardar el permiso: {exception.Message}");
        }
    }

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
