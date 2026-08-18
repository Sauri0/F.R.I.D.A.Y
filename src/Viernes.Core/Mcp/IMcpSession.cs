using System.Text.Json;

namespace Viernes.Core.Mcp;

/// <summary>
/// Una sesión viva contra un servidor MCP: lo poco que Viernes necesita pedirle.
/// </summary>
/// <remarks>
/// Existe para que la reconexión se pueda probar de verdad. El cliente del SDK levanta un proceso
/// real, así que una prueba de «se cayó y volvió» contra él tendría que matar un <c>npx</c> en el
/// medio y esperar a que el sistema operativo acompañe. Con esta costura, la lógica de caída, espera
/// creciente y vuelta —que es donde estaba el problema— se prueba sola y en milisegundos.
/// <para>
/// El único que habla con el SDK es <see cref="StdioMcpSession"/>.
/// </para>
/// </remarks>
public interface IMcpSession : IAsyncDisposable
{
    /// <summary>Qué sabe hacer el servidor.</summary>
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Ejecuta una herramienta del servidor.</summary>
    Task<McpToolCallOutcome> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Comprueba que del otro lado siga habiendo alguien. Tira si el servidor se murió.
    /// </summary>
    /// <remarks>
    /// Es lo que permite enterarse de una caída <em>antes</em> de que el usuario pida algo. Sin
    /// latido, la primera noticia de que Spotify se cayó es un pedido del usuario que falla.
    /// </remarks>
    Task PingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Una herramienta tal como la declaró el servidor, ya despegada de los tipos del SDK.
/// </summary>
/// <param name="Name">Nombre remoto, el que hay que mandar al llamarla.</param>
/// <param name="Description">Para qué sirve, en las palabras del servidor.</param>
/// <param name="Schema">Esquema JSON de sus argumentos.</param>
public sealed record McpToolDescriptor(string Name, string Description, JsonElement Schema);

/// <summary>
/// Lo que devolvió una herramienta remota: el texto y si el servidor lo marcó como error.
/// </summary>
/// <param name="IsError">Lo que declaró el servidor. Muchos mienten, por eso no alcanza solo.</param>
/// <param name="Text">Todo el contenido de texto de la respuesta, concatenado.</param>
public sealed record McpToolCallOutcome(bool IsError, string Text);
