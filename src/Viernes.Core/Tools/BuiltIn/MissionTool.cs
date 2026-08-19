using System.Text;
using System.Text.Json;
using Viernes.Core.Missions;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Encargos que duran hasta cumplirse, y que pueden frenarse a preguntar.
/// </summary>
/// <remarks>
/// La diferencia con <see cref="GoalTool"/> es de naturaleza, no de tamaño: un objetivo es algo en lo
/// que el usuario trabaja y que el asistente acompaña; una misión es algo que el asistente <em>hace</em>
/// y de lo que tiene que rendir cuentas. Por eso ésta tiene bitácora y pregunta pendiente, y aquélla no.
/// </remarks>
public sealed class MissionTool : IAssistantTool
{
    public const string ToolName = "mision";

    private readonly MissionBook _missions;

    public MissionTool(MissionBook missions) => _missions = missions;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Encargos que duran hasta cumplirse y sobreviven a que se cierre la conversación, se apague " +
        "la máquina y pasen días. " +
        "accion=«crear» cuando el usuario te encarga algo que no se resuelve en este turno " +
        "—«seguí el proyecto X», «revisá los mails todos los días», «avisame cuando Y»—: pasá " +
        "«titulo», «objetivo» (qué cuenta como cumplida) y opcionalmente «contexto» (una carpeta, " +
        "una cuenta). " +
        "accion=«avanzar» para anotar lo que hiciste: pasá «texto». Anotá SIEMPRE que avances; es " +
        "lo único que va a quedar cuando esta charla termine. " +
        "accion=«preguntar» cuando necesitás una decisión del usuario para poder seguir: pasá " +
        "«texto» con la pregunta. La misión queda frenada y la pregunta sobrevive hasta que te " +
        "conteste, aunque sea mañana. " +
        "accion=«responder» cuando el usuario contesta algo que destraba una misión frenada: pasá " +
        "«texto» con lo que dijo. " +
        "accion=«cerrar» cuando se cumplió el objetivo, «cancelar» si el usuario la deja. " +
        "accion=«listar» para contarle qué tiene abierto. " +
        "En «id» va el identificador corto (m1, m2) o parte del título; si no lo ponés y hay una " +
        "sola candidata, cae sobre ésa. " +
        "NO crees misiones para pedidos puntuales que resolvés ahora —abrir una app, poner música, " +
        "crear una carpeta—: sólo para lo que continúa después de este turno. " +
        "OJO, no confundir con «objetivo»: una MISIÓN es trabajo TUYO, con bitácora de lo que fuiste " +
        "haciendo y la posibilidad de frenarte a preguntarle algo; un OBJETIVO es algo que trabaja " +
        "ÉL y vos sólo recordás. Si vas a hacer algo y reportarlo, misión. Si sólo tenés que " +
        "acordarte de en qué anda, objetivo.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String(
                    "crear, avanzar, preguntar, responder, cerrar, cancelar o listar."),
                ["titulo"] = ToolSchemas.String("Cómo se la nombra en una línea, al crearla."),
                ["objetivo"] = ToolSchemas.String("Qué cuenta como cumplida."),
                ["texto"] = ToolSchemas.String("El avance, la pregunta o la respuesta, según la acción."),
                ["contexto"] = ToolSchemas.String("A qué se refiere: carpeta de proyecto, cuenta, etc."),
                ["id"] = ToolSchemas.String("Identificador corto o parte del título.")
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
        var id = JsonToolArguments.OptionalString(arguments, "id", 80);
        var text = JsonToolArguments.OptionalString(arguments, "texto", 400);

        try
        {
            return action switch
            {
                "crear" or "abrir" => await CreateAsync(arguments, context, cancellationToken)
                    .ConfigureAwait(false),
                "avanzar" => await ApplyAsync(
                    context, id, text, "un avance",
                    (bookId, value) => _missions.AdvanceAsync(bookId, value, cancellationToken),
                    mission => $"Anotado en «{mission.Title}».").ConfigureAwait(false),
                "preguntar" => await ApplyAsync(
                    context, id, text, "la pregunta",
                    (bookId, value) => _missions.AskAsync(bookId, value, cancellationToken),
                    mission => $"«{mission.Title}» queda esperando tu respuesta.").ConfigureAwait(false),
                "responder" => await ApplyAsync(
                    context, id, text, "la respuesta",
                    (bookId, value) => _missions.AnswerAsync(bookId, value, cancellationToken),
                    mission => $"Listo, sigo con «{mission.Title}».").ConfigureAwait(false),
                "cerrar" => await CloseAsync(context, id, text, cancellationToken).ConfigureAwait(false),
                "cancelar" or "abandonar" => await CancelAsync(context, id, cancellationToken)
                    .ConfigureAwait(false),
                "listar" => await ListAsync(context, cancellationToken).ConfigureAwait(false),
                _ => Fail(context, "No conozco esa acción sobre misiones.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Se informa el fallo real. Contestar «anotado» sobre un disco que no aceptó la escritura
            // es cómo una misión desaparece sin que nadie se entere hasta que se la necesita.
            return Fail(context, $"No pude guardar la misión: {exception.Message}");
        }
    }

    private async Task<ToolExecutionResult> CreateAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var title = JsonToolArguments.OptionalString(arguments, "titulo", 120);
        if (string.IsNullOrWhiteSpace(title))
        {
            return Fail(context, "Necesito un título para la misión.");
        }

        var mission = await _missions.CreateAsync(
            title,
            JsonToolArguments.OptionalString(arguments, "objetivo", 400) ?? title,
            JsonToolArguments.OptionalString(arguments, "contexto", 400),
            cancellationToken).ConfigureAwait(false);

        return Ok(context, $"Anotada como [{mission.Id}] «{mission.Title}». La sigo yo.");
    }

    private async Task<ToolExecutionResult> ApplyAsync(
        ToolExecutionContext context,
        string? id,
        string? text,
        string what,
        Func<string, string, Task<Mission?>> apply,
        Func<Mission, string> describe)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail(context, $"Necesito {what}.");
        }

        var mission = await apply(id ?? string.Empty, text).ConfigureAwait(false);
        return mission is null
            ? Fail(context, "No encontré esa misión. Pedí accion=listar para ver cuáles hay.")
            : Ok(context, describe(mission));
    }

    private async Task<ToolExecutionResult> CloseAsync(
        ToolExecutionContext context,
        string? id,
        string? how,
        CancellationToken cancellationToken)
    {
        var mission = await _missions.CloseAsync(id ?? string.Empty, how, cancellationToken)
            .ConfigureAwait(false);
        return mission is null
            ? Fail(context, "No encontré esa misión.")
            : Ok(context, $"Cerrada «{mission.Title}».");
    }

    private async Task<ToolExecutionResult> CancelAsync(
        ToolExecutionContext context,
        string? id,
        CancellationToken cancellationToken)
    {
        var mission = await _missions.CancelAsync(id ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return mission is null
            ? Fail(context, "No encontré esa misión.")
            : Ok(context, $"La dejo: «{mission.Title}».");
    }

    private async Task<ToolExecutionResult> ListAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var missions = await _missions.ListAsync(onlyOpen: true, cancellationToken).ConfigureAwait(false);
        if (missions.Count == 0)
        {
            return Ok(context, "No tenés ninguna misión abierta.");
        }

        var builder = new StringBuilder();
        foreach (var mission in missions)
        {
            builder.AppendLine();
            builder.Append($"[{mission.Id}] {mission.Title}");
            if (mission.State == MissionState.Esperando && mission.Question is not null)
            {
                builder.Append($" — esperando que me contestes: {mission.Question}");
            }
            else
            {
                builder.Append($" — {mission.State.ToString().ToLowerInvariant()}");
            }
        }

        return Ok(context, $"Tenés {missions.Count} abiertas:{builder}");
    }

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
