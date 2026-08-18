namespace Viernes.Core.Live;

/// <summary>
/// El caño por donde van y vienen los mensajes.
/// </summary>
/// <remarks>
/// Está separado del cliente por una sola razón, y es que sin esto no hay forma de probar nada: el
/// comportamiento que importa —vaciar la cola cuando la interrumpen, reconectar cuando llega el
/// aviso de cierre— sólo se puede verificar contra un servidor que hace exactamente eso cuando uno
/// quiere, y un websocket real contra Google no es eso.
/// </remarks>
public interface ILiveTransport : IAsyncDisposable
{
    /// <summary>Si el caño está abierto.</summary>
    bool IsOpen { get; }

    /// <summary>Abre la conexión.</summary>
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Manda un mensaje de texto.
    /// </summary>
    /// <remarks>
    /// La implementación tiene que soportar que la llamen desde varios lados a la vez: el micrófono
    /// manda fragmentos cincuenta veces por segundo mientras la interfaz puede mandar texto en
    /// cualquier momento.
    /// </remarks>
    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>
    /// Espera el próximo mensaje. Devuelve <c>null</c> cuando la conexión se cerró.
    /// </summary>
    /// <remarks>
    /// El cierre vuelve como <c>null</c> y no como excepción porque cerrar es normal acá: la
    /// conexión dura unos diez minutos por diseño y reconectar es parte del funcionamiento, no un
    /// caso de error.
    /// </remarks>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Cierra ordenadamente. No tiene que lanzar si ya estaba cerrado.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
