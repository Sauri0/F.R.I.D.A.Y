using System.Globalization;
using System.Text;
using Viernes.Core.Missions;
using Viernes.Core.Projects;
using Viernes.Core.Usage;
using Viernes.Memory;
using Viernes.Memory.Models;
using Viernes.Memory.Privacy;

namespace Viernes.Mcp;

/// <summary>
/// Todo lo que el conector sabe contestar, sin saber nada de MCP.
/// </summary>
/// <remarks>
/// Acá no hay lógica propia: misiones, memoria, permisos y vigilancia de Claude Code ya existen en
/// Viernes y leen los archivos de <c>%LOCALAPPDATA%\Viernes</c>. Esta clase los junta y escribe la
/// respuesta. La separación importa por dos motivos: las pruebas pueden llamar cada herramienta con
/// rutas de mentira y sin levantar un proceso, y el día que MCP cambie de forma no hay que volver a
/// escribir el comportamiento.
/// <para>
/// Toda acción que escribe algo pasa antes por <see cref="ConnectorBoundary"/>. Las que sólo leen no
/// consultan nada: pedir permiso para leer convierte al conector en un trámite, y ésa es la misma
/// regla que ya sigue la política de autonomía de la aplicación.
/// </para>
/// </remarks>
public sealed class ViernesConnector
{
    /// <summary>
    /// Los nombres con los que cada acción se presenta ante la política de autonomía.
    /// </summary>
    /// <remarks>
    /// Están juntos y no repartidos en cada método porque son la bisagra con los permisos: la
    /// política compara por contenido, así que una regla del usuario sobre «mision» alcanza a las
    /// cuatro, y una sobre «mision cerrar» sólo a ésa. <see cref="ChatWrite"/> empieza con «enviar»
    /// a propósito: es una de las palabras que la política considera consecuentes, así que escribirle
    /// a otra sesión pide autorización desde el primer día, sin que nadie configure nada.
    /// </remarks>
    private const string MissionCreate = "mision crear";
    private const string MissionAdvance = "mision avanzar";
    private const string MissionClose = "mision cerrar";
    private const string MissionAsk = "mision preguntar";
    private const string MemoryPropose = "memoria proponer";
    private const string ChatWrite = "enviar mensaje a claude code";

    /// <summary>Cuántos recuerdos se devuelven de una. Más que esto no se lee, se hojea.</summary>
    private const int MaximumMemories = 25;

    private readonly MissionBook _missions;
    private readonly IPersonalMemoryStore _memory;
    private readonly ClaudeSessionWatcher _sessions;
    private readonly ClaudeSessionWriter _writer;
    private readonly UsageLedger _usage;
    private readonly ConnectorBoundary _boundary;
    private readonly TimeProvider _time;

    public ViernesConnector(
        MissionBook missions,
        IPersonalMemoryStore memory,
        ClaudeSessionWatcher sessions,
        ClaudeSessionWriter writer,
        UsageLedger usage,
        ConnectorBoundary boundary,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(missions);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(boundary);

        _missions = missions;
        _memory = memory;
        _sessions = sessions;
        _writer = writer;
        _usage = usage;
        _boundary = boundary;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------- misiones

    /// <summary>Las misiones abiertas: en qué estado están, qué falta y desde cuándo.</summary>
    public async Task<ConnectorReply> ListMissionsAsync(
        bool includeClosed = false,
        CancellationToken cancellationToken = default)
    {
        var missions = await _missions.ListAsync(!includeClosed, cancellationToken).ConfigureAwait(false);
        if (missions.Count == 0)
        {
            return ConnectorReply.Fine(includeClosed
                ? "No hay ninguna misión anotada."
                : "No hay ninguna misión abierta.");
        }

        var builder = new StringBuilder();
        foreach (var mission in missions.OrderBy(mission => mission.State == MissionState.Esperando ? 0 : 1))
        {
            builder.AppendLine();
            builder.Append($"[{mission.Id}] {mission.Title} — {mission.State}");
            builder.Append($" · abierta desde {Stamp(mission.Created)}");
            builder.Append($" · último movimiento {Stamp(mission.LastProgress)}");

            if (!string.IsNullOrWhiteSpace(mission.Goal))
            {
                builder.AppendLine();
                builder.Append($"    se cumple cuando: {mission.Goal}");
            }

            if (mission.Context is not null)
            {
                builder.AppendLine();
                builder.Append($"    contexto: {mission.Context}");
            }

            if (mission.Question is not null)
            {
                builder.AppendLine();
                builder.Append(
                    $"    ESPERA RESPUESTA DEL USUARIO desde {Stamp(mission.AskedAt ?? mission.LastProgress)}: " +
                    $"«{mission.Question}»");
            }

            var last = mission.Log.LastOrDefault();
            if (last is not null)
            {
                builder.AppendLine();
                builder.Append($"    último avance ({Stamp(last.At)}): {last.Text}");
            }
        }

        return ConnectorReply.Fine($"{missions.Count} misiones:{builder}");
    }

    /// <summary>Anota un encargo que dura hasta cumplirse.</summary>
    public async Task<ConnectorReply> CreateMissionAsync(
        string title,
        string? goal = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ConnectorReply.Nope("Necesito un título para la misión.");
        }

        if (await _boundary.WhyNotAsync(MissionCreate, title, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        return await GuardDiskAsync(async () =>
        {
            var mission = await _missions
                .CreateAsync(title, goal ?? title, context, cancellationToken)
                .ConfigureAwait(false);

            return ConnectorReply.Fine($"Anotada como [{mission.Id}] «{mission.Title}».");
        }).ConfigureAwait(false);
    }

    /// <summary>Suma una línea a la bitácora de una misión.</summary>
    public async Task<ConnectorReply> AdvanceMissionAsync(
        string id,
        string what,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(what))
        {
            return ConnectorReply.Nope("Necesito el avance para anotarlo.");
        }

        if (await _boundary.WhyNotAsync(MissionAdvance, id, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        return await GuardDiskAsync(async () =>
        {
            var mission = await _missions
                .AdvanceAsync(id ?? string.Empty, what, cancellationToken)
                .ConfigureAwait(false);

            return mission is null
                ? ConnectorReply.Nope(NotFound(id))
                : ConnectorReply.Fine($"Anotado en [{mission.Id}] «{mission.Title}».");
        }).ConfigureAwait(false);
    }

    /// <summary>Cierra una misión, terminada o cancelada, dejando el motivo en la bitácora.</summary>
    /// <remarks>
    /// Cancelar con motivo hace <b>dos</b> cosas: escribe una línea en la bitácora y después cierra.
    /// La línea la escribe <c>AdvanceAsync</c>, o sea «mision avanzar», y acá se consultaba nada más
    /// «mision cerrar»: con «mision avanzar = Nunca» configurado, la línea entraba igual. Cada acción
    /// tiene que preguntar por lo que <em>realmente</em> va a hacer, y si una de las dos está
    /// prohibida se hace la otra y se dice cuál faltó — callarlo sería el mismo permiso ignorado
    /// otra vez, con mejores modales.
    /// </remarks>
    public async Task<ConnectorReply> CloseMissionAsync(
        string id,
        string? reason = null,
        bool cancelled = false,
        CancellationToken cancellationToken = default)
    {
        if (await _boundary.WhyNotAsync(MissionClose, id, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        // Con motivo se escribe en la bitácora, se cancele o no: cancelar lo hace con AdvanceAsync y
        // cerrar lo hace adentro de CloseAsync. Son dos caminos y el mismo efecto, así que los dos
        // preguntan por «avanzar».
        //
        // Antes sólo preguntaba la rama de cancelar, y esa asimetría es exactamente el defecto que
        // se vino a arreglar por la puerta de al lado: cada acción tiene que preguntar por lo que
        // REALMENTE va a hacer, no por el nombre de la herramienta que la contiene. Con «misión
        // avanzar = nunca», la nota de cierre entraba igual.
        var writesLog = !string.IsNullOrWhiteSpace(reason);
        var logRefusal = writesLog
            ? await _boundary.WhyNotAsync(MissionAdvance, id, cancellationToken).ConfigureAwait(false)
            : null;

        return await GuardDiskAsync(async () =>
        {
            if (!cancelled)
            {
                // Sin permiso para escribir en la bitácora se cierra igual, pero sin la nota: el
                // cierre lo autorizó la persona y no se le puede negar; lo que se cae es el renglón.
                var done = await _missions
                    .CloseAsync(id ?? string.Empty, logRefusal is null ? reason : null, cancellationToken)
                    .ConfigureAwait(false);

                if (done is null)
                {
                    return ConnectorReply.Nope(NotFound(id));
                }

                return logRefusal is null
                    ? ConnectorReply.Fine($"Cerrada [{done.Id}] «{done.Title}».")
                    : ConnectorReply.Fine(
                        $"Cerrada [{done.Id}] «{done.Title}». El motivo no quedó anotado: {logRefusal}");
            }

            // Cancelar no guarda motivo por sí solo, así que primero se anota y después se cancela:
            // una misión abandonada sin decir por qué es una que el usuario va a volver a abrir.
            if (writesLog && logRefusal is null)
            {
                await _missions.AdvanceAsync(id ?? string.Empty, reason!, cancellationToken)
                    .ConfigureAwait(false);
            }

            var dropped = await _missions
                .CancelAsync(id ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (dropped is null)
            {
                return ConnectorReply.Nope(NotFound(id));
            }

            return ConnectorReply.Fine(
                $"Cancelada [{dropped.Id}] «{dropped.Title}»." +
                (logRefusal is null
                    ? string.Empty
                    : $" El motivo NO quedó escrito en la bitácora: {logRefusal}"));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deja una pregunta para el usuario que sobrevive al reinicio.
    /// </summary>
    /// <remarks>
    /// Es la única forma que tiene Claude de dejarle algo dicho al usuario a través de Viernes: la
    /// pregunta vive en la misión, así que aparece en el orbe y sigue ahí mañana. No manda ninguna
    /// notificación ni interrumpe nada.
    /// </remarks>
    public async Task<ConnectorReply> AskInMissionAsync(
        string id,
        string question,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return ConnectorReply.Nope("Necesito la pregunta.");
        }

        if (await _boundary.WhyNotAsync(MissionAsk, id, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        return await GuardDiskAsync(async () =>
        {
            var mission = await _missions
                .AskAsync(id ?? string.Empty, question, cancellationToken)
                .ConfigureAwait(false);

            return mission is null
                ? ConnectorReply.Nope(NotFound(id))
                : ConnectorReply.Fine(
                    $"[{mission.Id}] «{mission.Title}» queda esperando que el usuario conteste.");
        }).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- memoria

    /// <summary>Busca en lo que Viernes aprendió del usuario.</summary>
    public async Task<ConnectorReply> SearchMemoryAsync(
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var items = await _memory.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var needle = text?.Trim();
        var found = (string.IsNullOrEmpty(needle)
                ? items
                : items.Where(item => item.Content.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.RecordedAt)
            .Take(MaximumMemories)
            .ToArray();

        if (found.Length == 0)
        {
            return ConnectorReply.Fine(string.IsNullOrEmpty(needle)
                ? "La memoria está vacía."
                : $"No hay nada en la memoria que diga «{needle}».");
        }

        var builder = new StringBuilder();
        foreach (var item in found)
        {
            builder.AppendLine();
            builder.Append($"- [{Describe(item.Kind)}] {item.Content} ({Stamp(item.RecordedAt)})");
        }

        return ConnectorReply.Fine(
            $"{found.Length} de la memoria de Viernes:{builder}{Environment.NewLine}" +
            "Lo que dice «supuesto» todavía no lo confirmó el usuario: no lo des por cierto.");
    }

    /// <summary>
    /// Deja un dato pendiente de aprobación. No lo aprueba: aprobar es del usuario.
    /// </summary>
    public async Task<ConnectorReply> ProposeMemoryAsync(
        string fact,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fact))
        {
            return ConnectorReply.Nope("Necesito el dato para proponerlo.");
        }

        if (await _boundary.WhyNotAsync(MemoryPropose, fact, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        try
        {
            var suggestion = await _memory
                .SuggestAsync(fact, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return ConnectorReply.Fine(
                "Queda propuesto y esperando que el usuario lo apruebe en Viernes: " +
                $"«{suggestion.Content}». Yo no lo puedo aprobar, y vence solo el " +
                $"{Stamp(suggestion.ExpiresAt)} si nadie decide nada.");
        }
        catch (MemoryContentRejectedException)
        {
            return ConnectorReply.Nope(
                "La memoria lo rechazó. No acepta secretos, credenciales ni conversaciones enteras: " +
                MemoryPrivacy.Notice);
        }
        catch (MemoryCapacityExceededException)
        {
            // Va antes que InvalidOperationException: hereda de ella y al revés nunca se alcanzaría.
            return ConnectorReply.Nope("La memoria está llena. El usuario tiene que borrar algo desde Viernes.");
        }
        catch (InvalidOperationException)
        {
            return ConnectorReply.Fine("Eso ya lo tenía guardado como un hecho confirmado.");
        }
    }

    // --------------------------------------------------------------- proyectos

    /// <summary>Las sesiones de Claude Code: si trabajan o esperan, y desde cuándo.</summary>
    /// <param name="maximum">Cuántas devolver, entre 1 y 20.</param>
    /// <param name="project">
    /// Parte del nombre de la carpeta. Sin esto salen las sesiones de <b>toda la máquina</b>, que
    /// para la pregunta habitual —«¿este proyecto me está esperando?»— es más de lo que hace falta.
    /// </param>
    /// <param name="includeLastMessage">
    /// Si sale también lo último que dijo el asistente en cada sesión, hasta
    /// <c>ClaudeSessionWatcher.MaximumSaid</c> caracteres. <b>Va en <see langword="false"/> por
    /// omisión y no es una preferencia de estilo</b>: es contenido de conversaciones de otros
    /// proyectos del usuario saliendo hacia quien esté del otro lado del conector. Que haya que
    /// pedirlo hace dos cosas: no sale sin querer, y cuando sale queda en el registro de la llamada
    /// quién lo pidió.
    /// </param>
    public ConnectorReply ListSessions(
        int maximum = 8,
        string? project = null,
        bool includeLastMessage = false)
    {
        var now = _time.GetLocalNow();
        var sessions = _sessions.Recent(
            now,
            Math.Clamp(maximum, 1, 20),
            onlyProjectContaining: project);

        if (sessions.Count == 0)
        {
            return ConnectorReply.Fine(string.IsNullOrWhiteSpace(project)
                ? "No encontré ninguna sesión de Claude Code en este equipo."
                : $"No encontré ninguna sesión de Claude Code sobre «{project}».");
        }

        var builder = new StringBuilder();
        foreach (var session in sessions)
        {
            builder.AppendLine();
            builder.Append("· ").Append(ClaudeSessionWatcher.Describe(session, now, includeLastMessage));
            builder.AppendLine();
            builder.Append($"  {session.Project}");
            if (session.Branch is not null)
            {
                builder.Append($" · rama {session.Branch}");
            }

            builder.Append($" · sesión {session.SessionId}");
        }

        var waiting = sessions.Count(session => session.Activity == SessionActivity.Esperando);
        var header = waiting == 0
            ? "Ninguna está esperando al usuario ahora."
            : $"{waiting} de {sessions.Count} están esperando que el usuario conteste.";

        var footer = includeLastMessage
            ? string.Empty
            : $"{Environment.NewLine}(Lo último que dijo cada sesión no sale por omisión: es " +
              "conversación de otros proyectos del usuario. Si de verdad lo necesitás, pedí esta " +
              "misma herramienta con ultimo_mensaje=true.)";

        return ConnectorReply.Fine(header + builder + footer);
    }

    /// <summary>
    /// Lo que habría que decirle a una sesión de Claude Code. Hoy no lo puede mandar.
    /// </summary>
    /// <remarks>
    /// Se consulta igual el permiso antes de contestar que no se puede, y el orden no es un detalle:
    /// si mañana aparece un canal soportado, el permiso ya está siendo respetado y no hay que
    /// acordarse de agregarlo. El motivo de fondo lo tiene <see cref="ClaudeSessionWriter"/>.
    /// </remarks>
    public async Task<ConnectorReply> WriteToSessionAsync(
        string project,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ConnectorReply.Nope("Necesito el texto que habría que decirle.");
        }

        if (await _boundary.WhyNotAsync(ChatWrite, project, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return ConnectorReply.Nope(refusal);
        }

        var outcome = _writer.Deliver(project ?? string.Empty, text, _time.GetLocalNow());
        return new ConnectorReply(outcome.Delivered, outcome.Explanation);
    }

    // ------------------------------------------------------------------ estado

    /// <summary>En qué anda Viernes ahora, qué está esperando al usuario y cuánto va gastado.</summary>
    public async Task<ConnectorReply> DescribeStateAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetLocalNow();
        var open = await _missions.ListAsync(onlyOpen: true, cancellationToken).ConfigureAwait(false);
        var attention = await _missions.NeedingAttentionAsync(now, cancellationToken).ConfigureAwait(false);
        var review = await _memory.ReviewAsync(cancellationToken).ConfigureAwait(false);
        var sessions = _sessions.Recent(now, maximum: 8);
        var daily = await _usage.GetDailyTotalsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var monthly = await _usage.GetMonthlyTotalsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var builder = new StringBuilder("Viernes ahora:");

        builder.AppendLine();
        builder.Append(open.Count == 0
            ? "· Misiones: ninguna abierta."
            : $"· Misiones: {open.Count} abiertas, " +
              $"{open.Count(mission => mission.State == MissionState.EnCurso)} en curso.");

        builder.AppendLine();
        if (attention.Count == 0)
        {
            builder.Append("· Nada está esperando al usuario.");
        }
        else
        {
            builder.Append($"· ESPERANDO AL USUARIO ({attention.Count}):");
            foreach (var mission in attention)
            {
                builder.AppendLine();
                builder.Append($"    [{mission.Id}] {mission.Title}");
                builder.Append(mission.Question is null
                    ? " · venció la revisión"
                    : $" · le preguntó «{mission.Question}» el {Stamp(mission.AskedAt ?? mission.LastProgress)}");
            }
        }

        var pending = review.Suggestions.Count + review.TemporaryObservations.Count;
        builder.AppendLine();
        builder.Append($"· Memoria: {review.Explicit.Count} hechos confirmados, {pending} sin confirmar.");

        var waiting = sessions.Count(session => session.Activity == SessionActivity.Esperando);
        var working = sessions.Count(session => session.Activity == SessionActivity.Trabajando);
        builder.AppendLine();
        builder.Append(sessions.Count == 0
            ? "· Claude Code: no hay sesiones."
            : $"· Claude Code: {working} trabajando, {waiting} esperando respuesta, de {sessions.Count} vistas.");

        builder.AppendLine();
        builder.Append(
            $"· Gasto: US$ {Money(daily.EffectiveCostUsd)} hoy en {daily.RequestCount} pedidos; " +
            $"US$ {Money(monthly.EffectiveCostUsd)} en el mes en {monthly.RequestCount}.");

        if (!_usage.IsPersistent)
        {
            builder.Append(" (el registro de gasto no se está guardando en disco)");
        }

        return ConnectorReply.Fine(builder.ToString());
    }

    // ------------------------------------------------------------------ apoyos

    private static string NotFound(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? "No supe a qué misión te referís. Pedí la lista y pasame el identificador corto."
            : $"No encontré ninguna misión abierta que sea «{id}».";

    /// <summary>
    /// Corre algo que escribe en disco y cuenta el fallo real si no pudo.
    /// </summary>
    /// <remarks>
    /// <see cref="MissionBook"/> lanza a propósito cuando no puede guardar. Tragarse eso y contestar
    /// «anotado» es cómo una misión desaparece sin que nadie se entere hasta que se la necesita.
    /// </remarks>
    private static async Task<ConnectorReply> GuardDiskAsync(Func<Task<ConnectorReply>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ConnectorReply.Nope($"No pude guardarlo en disco: {exception.Message}");
        }
    }

    private static string Describe(PersonalMemoryKind kind) => kind switch
    {
        PersonalMemoryKind.Explicit => "confirmado",
        PersonalMemoryKind.Suggestion => "supuesto, propuesto",
        _ => "supuesto, sin proponer"
    };

    /// <summary>
    /// Fecha y hora locales, sin adornos.
    /// </summary>
    /// <remarks>
    /// Del otro lado hay un modelo, no una persona: «hace tres días» obliga a saber qué día es hoy y
    /// se presta a que lo repita mal. La fecha entera no tiene ese problema y además evita copiar acá
    /// el redondeo que ya vive en <see cref="ClaudeSessionWatcher"/>.
    /// </remarks>
    private static string Stamp(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) =>
        amount.ToString("0.00##", CultureInfo.InvariantCulture);
}
