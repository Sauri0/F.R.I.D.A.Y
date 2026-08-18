namespace Viernes.Core.Mcp;

/// <summary>
/// El servidor MCP no está disponible en este momento y se lo está reintentando.
/// </summary>
/// <remarks>
/// Se distingue de un error cualquiera porque significa algo distinto para el usuario: no es que la
/// herramienta hizo mal su trabajo, es que del otro lado no hay nadie <em>todavía</em>. Con eso, la
/// respuesta puede ser «Spotify se cayó, estoy reconectando» en vez de un fallo genérico que suena a
/// que el pedido estuvo mal hecho.
/// </remarks>
public sealed class McpServerUnavailableException : Exception
{
    public McpServerUnavailableException(string serverName, TimeSpan retryIn)
        : base($"El servidor «{serverName}» está caído; reintento en {Describe(retryIn)}.")
    {
        ServerName = serverName;
        RetryIn = retryIn;
    }

    public McpServerUnavailableException(string message)
        : base(message)
    {
        ServerName = string.Empty;
        RetryIn = TimeSpan.Zero;
    }

    public McpServerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        ServerName = string.Empty;
        RetryIn = TimeSpan.Zero;
    }

    /// <summary>Nombre del servidor tal como figura en la configuración.</summary>
    public string ServerName { get; }

    /// <summary>Cuánto falta para el próximo intento.</summary>
    public TimeSpan RetryIn { get; }

    private static string Describe(TimeSpan delay) => delay.TotalSeconds < 90
        ? $"{Math.Max(1, (int)delay.TotalSeconds)} segundos"
        : $"{(int)delay.TotalMinutes} minutos";
}
