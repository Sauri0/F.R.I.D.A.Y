namespace Viernes.Core.Projects;

/// <summary>
/// Qué pasó al querer hacerle llegar un mensaje a una sesión de Claude Code.
/// </summary>
/// <param name="Delivered">
/// Si el mensaje entró en la sesión. Hoy es siempre <see langword="false"/>, y el motivo está
/// escrito en <see cref="ClaudeSessionWriter"/>.
/// </param>
/// <param name="Explanation">Qué contarle al usuario, en una respuesta lista para leer.</param>
public sealed record SessionWriteOutcome(bool Delivered, string Explanation);

/// <summary>
/// El intento de escribirle a una sesión de Claude Code, y por qué todavía no se puede.
/// </summary>
/// <remarks>
/// El usuario pidió esto con todas las letras: «quiero que pueda ver la app de Claude Code, ver el
/// chat, mantenerme al tanto… yo le indico qué debe decirle en el chat para seguir». Lo primero ya
/// lo hace <see cref="ClaudeSessionWatcher"/>. Lo segundo se buscó y <b>no existe una forma limpia</b>,
/// así que esta clase entrega el mensaje armado y dice la verdad en vez de fingir que lo mandó.
/// <para>
/// Lo que se miró, y por qué cada camino se descartó:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>El archivo de la sesión</b> (<c>%USERPROFILE%\.claude\projects\&lt;proyecto&gt;\&lt;id&gt;.jsonl</c>):
/// es el registro que escribe el proceso vivo de Claude Code, no un buzón. Agregarle una línea a
/// mano es escribir en el archivo abierto de otra aplicación, y además no serviría: el proceso que
/// está esperando tiene la conversación en memoria y no vuelve a leer ese archivo.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b><c>claude -p --resume &lt;id&gt;</c></b>: existe y está soportado, pero no le habla a la sesión
/// que está esperando: <em>arranca otro proceso</em> sobre la misma conversación. Dos escritores
/// sobre el mismo registro, un turno que gasta plata del usuario sin que él lo vea, y herramientas
/// corriendo con los permisos que tenga ese proceso. Es exactamente lo que el conector no puede
/// hacer: conectar un servidor no puede volverse la forma de saltearse lo que el usuario configuró.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>El registro de sesiones vivas</b> (<c>%USERPROFILE%\.claude\sessions\&lt;pid&gt;.json</c>):
/// anota el proceso, el identificador de sesión y la carpeta, pero no publica ningún canal —ni
/// puerto, ni tubería con nombre—. Sirve para saber que la sesión está viva; no para hablarle. Es
/// además un archivo interno de otra aplicación, sin formato documentado.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Teclear en la ventana</b> con automatización de escritorio: escribe a ciegas en lo que tenga
/// el foco. Es la clase de cosa que anda en la demostración y arruina un trabajo real.
/// </description>
/// </item>
/// </list>
/// <para>
/// Mientras tanto lo útil que sí se puede hacer es lo que hace <see cref="Deliver"/>: decir a qué
/// sesión iba —proyecto, rama, carpeta— y devolver el texto listo para que el usuario lo pegue. Una
/// herramienta que dice honestamente «esto no se puede así» vale más que una que parece funcionar.
/// </para>
/// </remarks>
public sealed class ClaudeSessionWriter
{
    /// <summary>
    /// El motivo, escrito una sola vez.
    /// </summary>
    /// <remarks>
    /// Vive en una constante y no repetido en cada rama porque el día que aparezca un canal
    /// soportado hay que cambiarlo en un solo lugar —y porque en este proyecto una constante
    /// escrita en dos lados ya hizo que un banco de medición informara contra la copia vieja—.
    /// </remarks>
    public const string WhyItCannotWrite =
        "No puedo escribir en la sesión de Claude Code. No hay ninguna forma soportada de meterle " +
        "un mensaje a una sesión que está esperando: el archivo .jsonl del proyecto es el registro " +
        "que escribe el proceso vivo y no un buzón —tocarlo es corromper el archivo de otra " +
        "aplicación—, y «claude -p --resume» no le habla a esa ventana sino que arranca otro " +
        "proceso sobre la misma conversación, gastando plata y corriendo herramientas sin que vos " +
        "lo veas.";

    private readonly ClaudeSessionWatcher _watcher;

    public ClaudeSessionWriter(ClaudeSessionWatcher? watcher = null) =>
        _watcher = watcher ?? new ClaudeSessionWatcher();

    /// <summary>
    /// Identifica la sesión destino y devuelve el mensaje listo para pegar, sin escribir nada.
    /// </summary>
    /// <param name="project">Parte del nombre de la carpeta del proyecto, o el id de sesión.</param>
    /// <param name="text">Lo que habría que decirle a Claude Code.</param>
    /// <param name="now">Momento contra el que se calcula hace cuánto que no pasa nada.</param>
    public SessionWriteOutcome Deliver(string project, string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var sessions = _watcher.Recent(now, maximum: 12);
        if (sessions.Count == 0)
        {
            return new SessionWriteOutcome(
                false,
                $"{WhyItCannotWrite} Además no encontré ninguna sesión de Claude Code en este equipo.");
        }

        var target = Resolve(sessions, project);
        if (target is null)
        {
            var known = string.Join(
                ", ",
                sessions.Select(session => Path.GetFileName(session.Project.TrimEnd('\\', '/'))).Distinct());

            return new SessionWriteOutcome(
                false,
                $"{WhyItCannotWrite} Tampoco sé a cuál se lo dirías: no tengo ninguna sesión que " +
                $"coincida con «{project}». Las que veo son: {known}.");
        }

        return new SessionWriteOutcome(
            false,
            $"{WhyItCannotWrite}{Environment.NewLine}{Environment.NewLine}" +
            $"Va para: {ClaudeSessionWatcher.Describe(target, now)}{Environment.NewLine}" +
            $"Carpeta: {target.Project}" +
            (target.Branch is null ? string.Empty : $" · rama {target.Branch}") +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"Pegale esto vos en esa ventana:{Environment.NewLine}{text}");
    }

    /// <summary>A cuál se refiere: por id de sesión exacto, o por parte del nombre de la carpeta.</summary>
    private static SessionSnapshot? Resolve(IReadOnlyList<SessionSnapshot> sessions, string? project)
    {
        var wanted = project?.Trim();
        if (string.IsNullOrEmpty(wanted))
        {
            // Sin referencia, sólo se resuelve si no hay ambigüedad posible. Adivinar destinatario
            // en algo que el usuario va a pegar en una conversación ajena no es una comodidad.
            return sessions.Count == 1 ? sessions[0] : null;
        }

        return sessions.FirstOrDefault(session =>
                   session.SessionId.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? sessions.FirstOrDefault(session =>
                   session.Project.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }
}
