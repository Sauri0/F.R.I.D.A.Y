namespace Viernes.Core.Mcp;

/// <summary>En qué momento de su vida está la conexión con un servidor.</summary>
public enum McpConnectionState
{
    /// <summary>Levantó por primera vez.</summary>
    Conectado,

    /// <summary>Se cayó: dejó de responder o falló una llamada.</summary>
    Caido,

    /// <summary>Volvió después de haberse caído.</summary>
    Recuperado,

    /// <summary>No pudo levantar; se sigue reintentando.</summary>
    NoLevanto
}

/// <summary>
/// Una entrada del registro de conexiones: qué le pasó a qué servidor y cuándo.
/// </summary>
/// <remarks>
/// Que quede anotado importa tanto como reconectar. Treinta herramientas de Spotify que desaparecen
/// sin aviso son indistinguibles de un asistente que se volvió tonto; con el registro, «no te
/// entiendo» pasa a ser «Spotify estuvo caído once minutos».
/// </remarks>
/// <param name="Server">Nombre del servidor, el mismo de la configuración.</param>
/// <param name="State">Qué le pasó.</param>
/// <param name="Detail">El motivo, cuando lo hay.</param>
/// <param name="At">Cuándo pasó, en hora local.</param>
/// <param name="Downtime">Cuánto estuvo caído; sólo viene al recuperarse.</param>
public sealed record McpConnectionEvent(
    string Server,
    McpConnectionState State,
    string Detail,
    DateTimeOffset At,
    TimeSpan? Downtime = null)
{
    /// <summary>Una línea lista para el registro de diagnóstico.</summary>
    public override string ToString() => State switch
    {
        McpConnectionState.Recuperado when Downtime is { } downtime =>
            $"{Server}: volvió después de {Describe(downtime)}",
        McpConnectionState.Recuperado => $"{Server}: volvió",
        McpConnectionState.Conectado => $"{Server}: conectado · {Detail}",
        McpConnectionState.Caido => $"{Server}: se cayó · {Detail}",
        _ => $"{Server}: no levantó · {Detail}"
    };

    private static string Describe(TimeSpan downtime) => downtime.TotalMinutes < 1
        ? $"{Math.Max(1, (int)downtime.TotalSeconds)} s"
        : $"{(int)downtime.TotalMinutes} min";
}
