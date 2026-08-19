using System.Text;
using System.Text.Json;

namespace Viernes.Core.Projects;

/// <summary>En qué anda una sesión de Claude Code.</summary>
public enum SessionActivity
{
    /// <summary>Está usando herramientas: trabajando.</summary>
    Trabajando,

    /// <summary>Terminó su turno y nadie le contestó: esperando al usuario.</summary>
    Esperando,

    /// <summary>Hace rato que no pasa nada.</summary>
    Quieta
}

/// <summary>Foto de una sesión de Claude Code en un momento dado.</summary>
/// <param name="Project">Carpeta del proyecto sobre la que corre.</param>
/// <param name="SessionId">Identificador de la sesión.</param>
/// <param name="Branch">Rama de git, si la sesión la registró.</param>
/// <param name="Activity">Si está trabajando, esperando o quieta.</param>
/// <param name="LastActivity">Cuándo escribió algo por última vez.</param>
/// <param name="LastSaid">Lo último que dijo en texto, recortado.</param>
/// <param name="ToolsSinceYouSpoke">Cuántas herramientas usó desde el último mensaje del usuario.</param>
/// <param name="Path">Ruta del archivo de sesión.</param>
public sealed record SessionSnapshot(
    string Project,
    string SessionId,
    string? Branch,
    SessionActivity Activity,
    DateTimeOffset LastActivity,
    string? LastSaid,
    int ToolsSinceYouSpoke,
    string Path);

/// <summary>
/// Mira lo que está haciendo Claude Code sin meterse en el medio.
/// </summary>
/// <remarks>
/// Claude Code deja cada sesión en un archivo JSONL bajo <c>%USERPROFILE%\.claude\projects</c>, una
/// línea por mensaje. Leerlo es la única forma de saber en qué anda sin robarle el foco ni tocarle la
/// ventana: se lee el archivo, no la pantalla.
/// <para>
/// La señal que importa está en la última línea. Un mensaje del asistente que termina con
/// <c>stop_reason: "tool_use"</c> significa que sigue trabajando; uno que cierra el turno sin que
/// venga nada del usuario después significa que <em>está esperando una respuesta</em> — que es
/// exactamente el momento en que conviene avisarle al usuario, porque hasta que no conteste el
/// proyecto no avanza.
/// </para>
/// </remarks>
public sealed class ClaudeSessionWatcher
{
    /// <summary>Sin novedades por más tiempo que esto, una sesión se considera quieta.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(20);

    /// <summary>Cuánto texto de la última respuesta se conserva para resumir.</summary>
    private const int MaximumSaid = 600;

    private readonly string _root;

    public ClaudeSessionWatcher(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");

    public string Root => _root;

    /// <summary>Las sesiones más recientes, la que se movió último primero.</summary>
    /// <remarks>
    /// Se excluyen las sesiones cuyo proyecto es el propio Viernes: mirarse a sí misma trabajando
    /// produce un lazo —cada resumen genera actividad que a su vez merece resumen— y además no es
    /// lo que el usuario quiere seguir.
    /// </remarks>
    /// <param name="now">Contra qué momento se calcula hace cuánto que no pasa nada.</param>
    /// <param name="maximum">Cuántas devolver como mucho.</param>
    /// <param name="excludeProjectContaining">Carpeta que no se quiere ver. Ver arriba.</param>
    /// <param name="onlyProjectContaining">
    /// Si se pasa, sólo las sesiones cuya carpeta contenga esto. Filtra <b>acá adentro</b> y no
    /// después, porque el corte por <paramref name="maximum"/> se aplica sobre lo que quedó: filtrar
    /// afuera devolvería las ocho más recientes de la máquina y recién ahí buscaría, que para un
    /// proyecto que hace rato no se toca es no encontrarlo nunca.
    /// </param>
    public IReadOnlyList<SessionSnapshot> Recent(
        DateTimeOffset now,
        int maximum = 8,
        string? excludeProjectContaining = null,
        string? onlyProjectContaining = null)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        // Cuando se pide un proyecto, las sesiones cuyo CAMINO ya lo nombra van primero, y recién
        // después se corta.
        //
        // El corte por recencia estaba antes del filtro, así que acotar a un proyecto sólo miraba
        // las sesiones más recientes de toda la máquina: si la que se buscaba era la número
        // veinticinco no aparecía nunca, que es justo el caso de un proyecto que hace rato no se
        // toca —el único en el que uno pregunta «¿quedó algo esperando ahí?»—.
        //
        // Es un preorden y no un filtro a propósito: el nombre del proyecto que devuelve una sesión
        // sale de adentro del archivo y no de la carpeta, así que un filtro por camino podría dejar
        // afuera algo que sí correspondía. Ordenar no descarta nada: lo que el camino no nombra
        // sigue entrando después, con su recencia intacta.
        var wanted = Normalize(onlyProjectContaining);

        var files = Directory
            .EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Length > 0)
            .OrderByDescending(file => wanted.Length > 0 && Normalize(file.FullName).Contains(wanted, StringComparison.Ordinal))
            .ThenByDescending(file => file.LastWriteTimeUtc)
            .Take(maximum * 3);

        var snapshots = new List<SessionSnapshot>();
        foreach (var file in files)
        {
            var snapshot = Read(file.FullName, now);
            if (snapshot is null)
            {
                continue;
            }

            if (excludeProjectContaining is not null &&
                snapshot.Project.Contains(excludeProjectContaining, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(onlyProjectContaining) &&
                !snapshot.Project.Contains(onlyProjectContaining, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            snapshots.Add(snapshot);
            if (snapshots.Count >= maximum)
            {
                break;
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Deja sólo letras y números, en minúscula, para comparar un camino contra un nombre.
    /// </summary>
    /// <remarks>
    /// Claude Code guarda cada sesión en una carpeta cuyo nombre es el camino del proyecto con los
    /// separadores cambiados por guiones: <c>N:\Viernes</c> queda como <c>N--Viernes</c>. Sacando
    /// todo lo que no es letra ni número, «Viernes», «N:\Viernes» y «n--viernes» caen en la misma
    /// cadena, y quien escribe el filtro no tiene que adivinar cómo se codificó.
    /// </remarks>
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Lee una sesión, o <c>null</c> si el archivo no dice nada útil.</summary>
    public SessionSnapshot? Read(string path, DateTimeOffset now)
    {
        try
        {
            // Sólo el final: una sesión larga pesa cientos de kilobytes y lo único que define el
            // estado son los últimos mensajes.
            var tail = ReadTail(path, 40);
            if (tail.Count == 0)
            {
                return null;
            }

            string? project = null;
            string? sessionId = null;
            string? branch = null;
            string? lastSaid = null;
            DateTimeOffset lastActivity = default;
            var toolsSinceUser = 0;
            var lastWasUnfinished = false;
            var sawAssistantAfterUser = false;

            foreach (var line in tail)
            {
                if (!TryParse(line, out var entry))
                {
                    continue;
                }

                project ??= entry.Cwd;
                sessionId ??= entry.SessionId;
                branch ??= entry.Branch;
                if (entry.Timestamp > lastActivity)
                {
                    lastActivity = entry.Timestamp;
                }

                if (entry.IsRealUserMessage)
                {
                    // Un mensaje escrito por la persona reinicia la cuenta: lo que interesa es
                    // cuánto trabajó desde la última vez que el usuario dijo algo.
                    toolsSinceUser = 0;
                    sawAssistantAfterUser = false;
                    lastWasUnfinished = false;
                    continue;
                }

                if (!entry.IsAssistant)
                {
                    continue;
                }

                sawAssistantAfterUser = true;
                toolsSinceUser += entry.ToolUses;
                lastWasUnfinished = entry.IsMidTask;
                if (entry.Text is { Length: > 0 })
                {
                    lastSaid = entry.Text;
                }
            }

            if (project is null)
            {
                return null;
            }

            var idle = now - lastActivity;
            var activity = lastWasUnfinished && idle < Stale
                ? SessionActivity.Trabajando
                : idle >= Stale
                    ? SessionActivity.Quieta
                    : sawAssistantAfterUser
                        ? SessionActivity.Esperando
                        : SessionActivity.Quieta;

            return new SessionSnapshot(
                project,
                sessionId ?? Path.GetFileNameWithoutExtension(path),
                branch,
                activity,
                lastActivity,
                Shorten(lastSaid),
                toolsSinceUser,
                path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // Claude Code está escribiendo el archivo mientras se lee: se reintenta en el próximo
            // barrido. Fallar acá apagaría la vigilancia entera por un archivo a medio escribir.
            return null;
        }
    }

    /// <summary>Cuenta en una línea qué está pasando en una sesión.</summary>
    /// <param name="snapshot">La sesión.</param>
    /// <param name="now">Contra qué momento se calcula hace cuánto.</param>
    /// <param name="includeLastSaid">
    /// Si se incluye lo último que dijo el asistente en esa sesión. Va en <see langword="true"/> por
    /// omisión porque adentro de Viernes es lo que el usuario quiere ver de sus propios proyectos.
    /// Lo pone en <see langword="false"/> quien esté escribiendo para <b>afuera</b>: son cientos de
    /// caracteres de una conversación de otro proyecto, y de un vistazo a la lista entera salen los
    /// de todos.
    /// </param>
    public static string Describe(SessionSnapshot snapshot, DateTimeOffset now, bool includeLastSaid = true)
    {
        var project = Path.GetFileName(snapshot.Project.TrimEnd('\\', '/'));
        var hace = Humanize(now - snapshot.LastActivity);

        return snapshot.Activity switch
        {
            SessionActivity.Trabajando =>
                $"En {project} está trabajando —{snapshot.ToolsSinceYouSpoke} pasos desde tu último mensaje—, " +
                $"lo último hace {hace}.",
            SessionActivity.Esperando =>
                $"En {project} terminó y te está esperando desde hace {hace}" +
                (includeLastSaid && snapshot.LastSaid is not null ? $". Dijo: «{snapshot.LastSaid}»" : "."),
            _ => $"En {project} no pasa nada hace {hace}."
        };
    }

    private static string Humanize(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "menos de un minuto",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} minutos",
        { TotalHours: < 24 } => $"{(int)span.TotalHours} horas",
        _ => $"{(int)span.TotalDays} días"
    };

    private static string? Shorten(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var single = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaximumSaid ? single : single[..MaximumSaid] + "…";
    }

    /// <summary>
    /// Últimas <paramref name="count"/> líneas sin cargar el archivo entero.
    /// </summary>
    /// <remarks>
    /// <c>FileShare.ReadWrite</c> no es opcional: Claude Code tiene el archivo abierto para escribir
    /// mientras trabaja, y abrirlo en exclusiva fallaría exactamente cuando hay algo interesante que
    /// mirar.
    /// </remarks>
    private static List<string> ReadTail(string path, int count)
    {
        var lines = new LinkedList<string>();
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            lines.AddLast(line);
            if (lines.Count > count)
            {
                lines.RemoveFirst();
            }
        }

        return [.. lines];
    }

    private static bool TryParse(string line, out SessionEntry entry)
    {
        entry = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var type = Text(root, "type");
            var cwd = Text(root, "cwd");
            var sessionId = Text(root, "sessionId");
            var branch = Text(root, "gitBranch");
            var timestamp = root.TryGetProperty("timestamp", out var stamp) &&
                stamp.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(stamp.GetString(), out var parsed)
                    ? parsed
                    : default;

            var isAssistant = string.Equals(type, "assistant", StringComparison.Ordinal);
            var isUser = string.Equals(type, "user", StringComparison.Ordinal);

            var text = new StringBuilder();
            var toolUses = 0;
            var hasToolResult = false;

            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (Text(block, "type"))
                        {
                            case "text":
                                text.Append(Text(block, "text")).Append(' ');
                                break;
                            case "tool_use":
                                toolUses++;
                                break;
                            case "tool_result":
                                hasToolResult = true;
                                break;
                        }
                    }
                }
            }

            var stopReason = root.TryGetProperty("message", out var m2) &&
                m2.ValueKind == JsonValueKind.Object
                    ? Text(m2, "stop_reason")
                    : null;

            entry = new SessionEntry(
                cwd,
                sessionId,
                branch,
                timestamp,
                isAssistant,
                // Un `user` que sólo trae resultados de herramientas lo escribió el sistema, no la
                // persona: contarlo como mensaje del usuario haría ver «te está esperando» en medio
                // de una tanda de trabajo.
                IsRealUserMessage: isUser && !hasToolResult,
                text.ToString(),
                toolUses,
                IsMidTask: string.Equals(stopReason, "tool_use", StringComparison.Ordinal) || toolUses > 0);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record SessionEntry(
        string? Cwd,
        string? SessionId,
        string? Branch,
        DateTimeOffset Timestamp,
        bool IsAssistant,
        bool IsRealUserMessage,
        string? Text,
        int ToolUses,
        bool IsMidTask);
}
