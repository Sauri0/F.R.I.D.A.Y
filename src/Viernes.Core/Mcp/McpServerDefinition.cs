namespace Viernes.Core.Mcp;

/// <summary>
/// Un servidor MCP declarado en la configuración: qué proceso levantar y con qué argumentos.
/// </summary>
/// <param name="Name">Nombre corto; prefija las herramientas para que no choquen entre servidores.</param>
/// <param name="Command">Ejecutable, por ejemplo <c>npx</c>, <c>uvx</c> o una ruta.</param>
/// <param name="Arguments">Argumentos del proceso.</param>
/// <param name="Environment">
/// Variables para el proceso hijo. Acá van las credenciales de cada servicio —Spotify, Google— y por
/// eso el valor se toma del entorno de Viernes, nunca del archivo: el archivo guarda el
/// <em>nombre</em> de la variable, no el secreto.
/// </param>
/// <param name="Enabled">Permite dejarlo declarado y apagado sin borrar la configuración.</param>
public sealed record McpServerDefinition(
    string Name,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    bool Enabled = true);
