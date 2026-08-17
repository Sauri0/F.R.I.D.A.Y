using System.Text;
using System.Text.Json;
using Viernes.Core.Projects;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Mira en qué anda Claude Code, sin meterse en el medio.
/// </summary>
/// <remarks>
/// El rol es de supervisora, no de reemplazo: el usuario sigue conduciendo su sesión de Claude Code:
/// lo que aporta esto es que alguien esté mirando cuando él no está. Saber que una sesión terminó y
/// quedó esperando una respuesta es la diferencia entre un proyecto parado toda la tarde y uno que
/// avanza.
/// <para>
/// Es de sólo lectura a propósito. Escribir en la sesión de otro agente desde acá significaría dos
/// cabezas dictando sobre la misma conversación sin que ninguna vea a la otra, que es la forma más
/// rápida de arruinar un trabajo que iba bien. Lo que el usuario quiera contestar, lo contesta él.
/// </para>
/// </remarks>
public sealed class ProjectTool : IAssistantTool
{
    public const string ToolName = "proyectos";

    private readonly ClaudeSessionWatcher _watcher;
    private readonly TimeProvider _time;

    public ProjectTool(ClaudeSessionWatcher? watcher = null, TimeProvider? timeProvider = null)
    {
        _watcher = watcher ?? new ClaudeSessionWatcher();
        _time = timeProvider ?? TimeProvider.System;
    }

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Mira las sesiones de Claude Code del usuario: en qué proyecto anda, si está trabajando o " +
        "si terminó y quedó esperando que le contesten, qué fue lo último que dijo y hace cuánto. " +
        "Usala cuando el usuario pregunte «cómo va el proyecto», «qué está haciendo Claude», «hay " +
        "algo esperándome», o cuando estés siguiendo una misión sobre un proyecto. " +
        "accion=«estado» lista las sesiones recientes. " +
        "accion=«detalle» cuenta una en particular: pasá parte del nombre del proyecto en «proyecto». " +
        "Es de sólo lectura: no escribe en la sesión ni le contesta a Claude. Si hay algo que " +
        "responder, contáselo al usuario para que conteste él.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String("estado o detalle."),
                ["proyecto"] = ToolSchemas.String("Parte del nombre de la carpeta del proyecto.")
            },
            ["accion"]),
        ToolRiskLevel.Safe);

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "accion", 20).ToLowerInvariant();
        var project = JsonToolArguments.OptionalString(arguments, "proyecto", 200);
        var now = _time.GetLocalNow();

        // El propio Viernes queda afuera: mirarse trabajando genera actividad que a su vez merece
        // resumen, y además no es lo que el usuario quiere seguir.
        var sessions = _watcher.Recent(now, maximum: 8, excludeProjectContaining: "Viernes");

        if (sessions.Count == 0)
        {
            return Task.FromResult(Ok(
                context,
                "No encontré ninguna sesión de Claude Code. Si nunca abriste una en este equipo, " +
                "no hay nada que mirar todavía."));
        }

        if (action == "detalle" && !string.IsNullOrWhiteSpace(project))
        {
            var match = sessions.FirstOrDefault(session =>
                session.Project.Contains(project, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(match is null
                ? Fail(context, $"No tengo ninguna sesión abierta sobre «{project}».")
                : Ok(context, Detail(match, now)));
        }

        var builder = new StringBuilder();
        foreach (var session in sessions.Take(5))
        {
            builder.AppendLine();
            builder.Append("· ").Append(ClaudeSessionWatcher.Describe(session, now));
        }

        var esperando = sessions.Count(session => session.Activity == SessionActivity.Esperando);
        var encabezado = esperando > 0
            ? $"{esperando} {(esperando == 1 ? "proyecto está esperándote" : "proyectos están esperándote")}:"
            : "Ninguno está esperándote ahora:";

        return Task.FromResult(Ok(context, encabezado + builder));
    }

    private static string Detail(SessionSnapshot session, DateTimeOffset now)
    {
        var builder = new StringBuilder(ClaudeSessionWatcher.Describe(session, now));
        builder.AppendLine();
        builder.Append("Carpeta: ").Append(session.Project);
        if (session.Branch is not null)
        {
            builder.Append(" · rama ").Append(session.Branch);
        }

        if (session.Activity == SessionActivity.Esperando && session.LastSaid is not null)
        {
            builder.AppendLine();
            builder.Append(
                "Está frenado hasta que le contesten. Contale al usuario qué dijo y preguntale " +
                "qué quiere responder; después él lo escribe en la sesión.");
        }

        return builder.ToString();
    }

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
