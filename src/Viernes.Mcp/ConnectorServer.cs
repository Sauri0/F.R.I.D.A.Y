using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Viernes.Mcp;

/// <summary>
/// Cómo se presenta el conector ante Claude: quién es, qué trae y qué no hace.
/// </summary>
/// <remarks>
/// Está separado de <c>Program</c> para que las pruebas puedan mirar exactamente la lista de
/// herramientas que va a publicar el proceso de verdad. Si esto viviera adentro del arranque, la
/// única forma de comprobar que las herramientas quedaron enganchadas sería levantar el ejecutable
/// —y entonces nadie lo comprobaría—.
/// </remarks>
public static class ConnectorServer
{
    /// <summary>Las instrucciones que lee Claude al conectarse.</summary>
    /// <remarks>
    /// Que la frontera esté acá y no sólo en el LEEME es lo que hace que Claude no pierda el tiempo
    /// intentando aprobar memoria ni buscando la forma de saltear un permiso: se entera al conectar.
    /// </remarks>
    public const string Instructions =
        "Viernes es el asistente de escritorio del usuario. Este conector te deja trabajar con lo " +
        "que Viernes ya sabe, sobre los mismos archivos que usa la aplicación: sus misiones, su " +
        "memoria, las sesiones de Claude Code que está mirando y cuánto va gastado. " +
        "Empezá por viernes_estado cuando quieras ubicarte. " +
        "Hay tres cosas que no hace y son a propósito, no funciones que falten: no aprueba memoria " +
        "—aprobar es del usuario, el conector sólo propone—, no pasa por encima de los permisos que " +
        "el usuario configuró —si una acción está en «preguntar», no la hace y te lo dice— y no " +
        "toca ninguna credencial. " +
        "Escribir en una sesión de Claude Code todavía no se puede: la herramienta existe para " +
        "explicarte por qué y devolverte el mensaje listo para que lo pegue el usuario. " +
        "Una cosa más, y conviene avisarla: si la aplicación de Viernes está abierta mientras vos " +
        "creás o movés una misión, el orbe no la va a ver hasta reiniciarse —tiene el archivo " +
        "cacheado— y si él guarda algo después, pisa lo que escribiste. Para mover misiones, mejor " +
        "con el orbe cerrado.";

    /// <summary>Arma la configuración del servidor con las herramientas ya enganchadas.</summary>
    public static McpServerOptions CreateOptions(ViernesConnector connector)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in ConnectorTools.Build(connector))
        {
            tools.Add(tool);
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "viernes",
                Title = "Viernes",
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
            },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = tools,
            ServerInstructions = Instructions
        };
    }
}
