using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Viernes.Mcp;

/// <summary>
/// Las herramientas tal como las ve Claude, y nada más que eso.
/// </summary>
/// <remarks>
/// Es la única parte que conoce el SDK de MCP. Cada herramienta es un nombre, una explicación de
/// cuándo usarla y una llamada a <see cref="ViernesConnector"/>; si acá adentro apareciera lógica,
/// dejaría de poder probarse sin levantar un servidor.
/// <para>
/// Los nombres van con prefijo <c>viernes_</c> porque conviven en la misma lista con las
/// herramientas de todos los demás conectores del usuario: sin prefijo, «listar» no le dice a nadie
/// qué se va a listar.
/// </para>
/// </remarks>
public static class ConnectorTools
{
    /// <summary>Arma las diez herramientas contra un conector ya construido.</summary>
    public static IReadOnlyList<McpServerTool> Build(ViernesConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        return
        [
            McpServerTool.Create(
                async ([Description("Incluir también las terminadas y canceladas.")] bool cerradas = false,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.ListMissionsAsync(cerradas, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_misiones_listar",
                    Title = "Misiones de Viernes",
                    Description =
                        "Las misiones de Viernes: encargos que duran hasta cumplirse y sobreviven a " +
                        "que se cierre la conversación. Devuelve en qué estado está cada una, qué " +
                        "cuenta como cumplida, desde cuándo está abierta, su último avance y —lo " +
                        "más importante— si alguna quedó frenada esperando que el usuario conteste " +
                        "algo. Usala antes de proponer trabajo nuevo: puede que ya haya algo a medias.",
                    ReadOnly = true,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Cómo se la nombra en una línea.")] string titulo,
                       [Description("Qué cuenta como cumplida. Sin esto no se sabe cuándo cerrarla.")] string? objetivo = null,
                       [Description("A qué se refiere: una carpeta de proyecto, una cuenta, un cliente.")] string? contexto = null,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.CreateMissionAsync(titulo, objetivo, contexto, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_mision_crear",
                    Title = "Crear una misión",
                    Description =
                        "Le encarga a Viernes algo que no se resuelve en un rato y que tiene que " +
                        "seguir vivo entre charlas: seguir un proyecto, revisar algo todos los días, " +
                        "avisar cuando pase X. NO la uses para pedidos puntuales que se resuelven " +
                        "ahora. Si los permisos del usuario marcan esto como «preguntar», no se crea " +
                        "nada y te lo digo.",
                    ReadOnly = false,
                    Destructive = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Identificador corto (m1, m2) o parte del título.")] string id,
                       [Description("Qué se hizo, en una línea.")] string texto,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.AdvanceMissionAsync(id, texto, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_mision_avanzar",
                    Title = "Anotar un avance",
                    Description =
                        "Anota en la bitácora de una misión lo que se avanzó. Es lo único que va a " +
                        "quedar de este trabajo cuando la conversación termine, así que anotá cada " +
                        "vez que pase algo real, no al final.",
                    ReadOnly = false,
                    Destructive = false,
                    Idempotent = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Identificador corto (m1, m2) o parte del título.")] string id,
                       [Description("Cómo terminó, o por qué se abandona. Queda en la bitácora.")] string? motivo = null,
                       [Description("true si se abandona sin cumplirse; false si se cumplió.")] bool cancelada = false,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.CloseMissionAsync(id, motivo, cancelada, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_mision_cerrar",
                    Title = "Cerrar una misión",
                    Description =
                        "Cierra una misión: terminada si se cumplió el objetivo, cancelada si el " +
                        "usuario la deja. El motivo queda escrito en la bitácora. No borra nada: la " +
                        "misión sigue estando, cerrada.",
                    ReadOnly = false,
                    Destructive = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Identificador corto (m1, m2) o parte del título.")] string id,
                       [Description("La pregunta, tal como se la va a leer el usuario.")] string pregunta,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.AskInMissionAsync(id, pregunta, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_mision_preguntar",
                    Title = "Dejarle una pregunta al usuario",
                    Description =
                        "Deja una pregunta pendiente dentro de una misión. La misión queda frenada y " +
                        "la pregunta sobrevive a que se cierre todo y se apague la máquina: el " +
                        "usuario la ve en Viernes cuando vuelva. Es la forma de dejarle algo dicho " +
                        "sin interrumpirlo. No manda notificaciones ni le habla por otro lado.",
                    ReadOnly = false,
                    Destructive = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Qué buscar. Vacío devuelve lo último que hay.")] string? texto = null,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.SearchMemoryAsync(texto, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_memoria_buscar",
                    Title = "Buscar en la memoria",
                    Description =
                        "Busca en lo que Viernes sabe del usuario. Distingue lo confirmado por él de " +
                        "lo que Viernes supone y todavía nadie aprobó: lo supuesto no lo des por " +
                        "cierto ni lo repitas como un hecho.",
                    ReadOnly = true,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("El hecho, en una línea. Nada de secretos ni conversaciones enteras.")] string dato,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.ProposeMemoryAsync(dato, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_memoria_proponer",
                    Title = "Proponer algo para la memoria",
                    Description =
                        "Deja un dato sobre el usuario esperando que él lo apruebe en Viernes. NO lo " +
                        "guarda como cierto: aprobar es del usuario y este conector no puede hacerlo, " +
                        "a propósito. Si nadie decide nada, la propuesta vence sola.",
                    ReadOnly = false,
                    Destructive = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                ([Description("Cuántas sesiones devolver, entre 1 y 20.")] int maximo = 8,
                 [Description("Parte del nombre de la carpeta, para ver un solo proyecto en vez de toda la máquina.")] string? proyecto = null,
                 [Description("Incluir lo último que dijo el asistente en cada sesión. Es conversación de otros proyectos del usuario: pedilo sólo si hace falta de verdad.")] bool ultimo_mensaje = false) =>
                    Say(connector.ListSessions(maximo, proyecto, ultimo_mensaje)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_proyectos_listar",
                    Title = "Sesiones de Claude Code",
                    Description =
                        "Las sesiones de Claude Code del usuario en este equipo: en qué carpeta " +
                        "corren, en qué rama, si están trabajando o si terminaron y quedaron " +
                        "esperando que les contesten, y desde hace cuánto. Es de sólo lectura: se lee " +
                        "el archivo de la sesión, no se le toca la ventana a nadie. Sin «proyecto» " +
                        "salen las de TODA la máquina; acotá con «proyecto» cuando sepas cuál mirás. " +
                        "Lo último que dijo cada sesión NO sale por omisión.",
                    ReadOnly = true,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async ([Description("Parte del nombre de la carpeta del proyecto, o el id de la sesión.")] string proyecto,
                       [Description("Lo que habría que decirle a esa sesión.")] string texto,
                       CancellationToken cancellationToken = default) =>
                    Say(await connector.WriteToSessionAsync(proyecto, texto, cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_proyecto_escribir",
                    Title = "Escribirle a una sesión de Claude Code",
                    Description =
                        "HOY NO FUNCIONA Y TE VA A DECIR POR QUÉ. No existe una forma soportada de " +
                        "meterle un mensaje a una sesión de Claude Code que está esperando input, y " +
                        "las que hay —escribirle el archivo .jsonl de la sesión, arrancar otro " +
                        "proceso con --resume— rompen cosas o gastan plata a espaldas del usuario. " +
                        "Lo que hace es identificar a qué sesión iba y devolver el mensaje armado " +
                        "para que lo pegue el usuario. Si querés que le quede algo anotado, usá " +
                        "viernes_mision_preguntar.",
                    ReadOnly = true,
                    Destructive = false,
                    OpenWorld = false
                }),

            McpServerTool.Create(
                async (CancellationToken cancellationToken = default) =>
                    Say(await connector.DescribeStateAsync(cancellationToken)),
                new McpServerToolCreateOptions
                {
                    Name = "viernes_estado",
                    Title = "Estado de Viernes",
                    Description =
                        "La foto de ahora: cuántas misiones hay abiertas, qué está esperando una " +
                        "respuesta del usuario, cuánto tiene en memoria sin confirmar, qué sesiones " +
                        "de Claude Code hay y cuánto va gastado hoy y en el mes. Empezá por acá " +
                        "cuando el usuario pregunte «cómo venimos».",
                    ReadOnly = true,
                    OpenWorld = false
                })
        ];
    }

    /// <summary>
    /// Convierte la respuesta del conector en lo que espera MCP, marcando los fallos como tales.
    /// </summary>
    /// <remarks>
    /// Si esto devolviera siempre texto plano, una negativa por permisos llegaría del otro lado como
    /// una llamada exitosa y el modelo tendría que deducir leyendo la prosa que no se hizo nada.
    /// </remarks>
    private static CallToolResult Say(ConnectorReply reply) => new()
    {
        IsError = !reply.Ok,
        Content = [new TextContentBlock { Text = reply.Text }]
    };
}
