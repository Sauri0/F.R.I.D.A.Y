using System.Text.Json;
using Viernes.Core.Persistence;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Cierra el ciclo de vida de un recordatorio: darlo por hecho o borrarlo.
/// </summary>
/// <remarks>
/// Hasta acá se podían crear y listar, y nada más. Un recordatorio cumplido se quedaba en la lista
/// para siempre, y uno anotado por error tampoco se podía sacar: la única salida era editar el JSON
/// a mano. Una lista que sólo crece deja de leerse, y una lista que no se lee no recuerda nada.
/// <para>
/// Va como herramienta aparte y no como un argumento de <see cref="ReminderCreateTool"/> porque el
/// nombre de una herramienta es lo que el modelo lee para decidir: <c>reminder_create</c> con un
/// campo «acción=borrar» adentro es una trampa esperando a que confunda crear con destruir.
/// </para>
/// </remarks>
public sealed class ReminderUpdateTool(IUserDataStore dataStore) : IAssistantTool
{
    public const string ToolName = "reminder_update";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Marca un recordatorio como hecho o lo borra. " +
        "action=«complete» lo da por cumplido y deja de aparecer en la lista y de avisar. " +
        "action=«delete» lo saca del archivo; usalo sólo si estaba mal anotado. " +
        "Identificá cuál con «id» —el que devuelve reminder_list— o, si no lo tenés, con «title», " +
        "que tiene que corresponder a uno solo. Si dudás de cuál es, listalos primero y preguntá.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["action"] = ToolSchemas.String(
                    "complete para darlo por hecho, delete para borrarlo.",
                    enumValues: ["complete", "delete"]),
                ["id"] = ToolSchemas.String("Identificador exacto devuelto por reminder_list."),
                ["title"] = ToolSchemas.String("Título del recordatorio, si no tenés el id.")
            },
            ["action"]),
        ToolRiskLevel.Safe);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var rawAction = JsonToolArguments.RequiredString(arguments, "action", 20).ToLowerInvariant();

        // Se aceptan las dos lenguas: el prompt del sistema está en castellano y el modelo manda
        // «borrar» tan seguido como «delete». Rechazar por el idioma del valor sería inventar un
        // error donde la intención estaba clarísima.
        var delete = rawAction switch
        {
            "complete" or "completar" or "hecho" or "listo" => false,
            "delete" or "borrar" or "eliminar" => true,
            _ => throw new ArgumentException(
                "El argumento 'action' tiene que ser 'complete' o 'delete'.",
                nameof(arguments))
        };

        var id = JsonToolArguments.OptionalString(arguments, "id", 64);
        var title = JsonToolArguments.OptionalString(arguments, "title", 200);
        var reminders = await dataStore.GetRemindersAsync(cancellationToken).ConfigureAwait(false);

        var resolved = Resolve(reminders, id, title);
        if (resolved.Reminder is null)
        {
            return ToolExecutionResult.Failure(context.ToolCallId, ToolName, resolved.Problem);
        }

        var target = resolved.Reminder;
        var changed = delete
            ? await dataStore.DeleteReminderAsync(target.Id, cancellationToken).ConfigureAwait(false)
            : await dataStore.CompleteReminderAsync(target.Id, cancellationToken).ConfigureAwait(false);

        // Nunca «listo» sin haber mirado. El store devuelve false cuando no había nada que cambiar
        // —ya estaba completado, o desapareció entre la lectura y la escritura— y decir que se hizo
        // en ese caso es exactamente la clase de mentira que después nadie puede rastrear.
        if (!changed)
        {
            return ToolExecutionResult.Failure(
                context.ToolCallId,
                ToolName,
                delete
                    ? $"No pude borrar «{target.Title}»: ya no estaba."
                    : $"«{target.Title}» ya figuraba como hecho.");
        }

        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            delete
                ? $"Borré «{target.Title}»."
                : $"Marqué «{target.Title}» como hecho.",
            // El recordatorio borrado viaja en el resultado a propósito: es lo único que permite
            // rehacerlo si el borrado fue un malentendido.
            new { target.Id, target.Title, target.DueAt, action = delete ? "delete" : "complete" });
    }

    /// <summary>
    /// Encuentra el recordatorio del que se está hablando, o explica por qué no puede.
    /// </summary>
    /// <remarks>
    /// Ante un título ambiguo no elige: dos recordatorios que se llaman igual y borrar el que no era
    /// es un error silencioso, porque el usuario ve «listo» y recién se entera cuando el que
    /// importaba no suena.
    /// </remarks>
    private static (Reminder? Reminder, string Problem) Resolve(
        IReadOnlyList<Reminder> reminders,
        string? id,
        string? title)
    {
        if (id is not null)
        {
            if (!Guid.TryParse(id, out var parsed))
            {
                return (null, "Ese id no tiene forma de identificador; listá los recordatorios de nuevo.");
            }

            var byId = reminders.FirstOrDefault(item => item.Id == parsed);
            return byId is null
                ? (null, "No encontré ningún recordatorio con ese id.")
                : (byId, string.Empty);
        }

        if (title is null)
        {
            return (null, "Decime cuál: hace falta el id o el título del recordatorio.");
        }

        var matches = reminders
            .Where(item => !item.IsCompleted &&
                           string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => (null, $"No encontré ningún recordatorio pendiente que se llame «{title}»."),
            1 => (matches[0], string.Empty),
            _ => (null, $"Hay {matches.Length} recordatorios que se llaman «{title}»; necesito el id.")
        };
    }
}
