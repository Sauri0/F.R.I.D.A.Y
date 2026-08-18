using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Viernes.Core.Live;

/// <summary>
/// El transporte de verdad, sobre <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// Dos detalles de <see cref="ClientWebSocket"/> que no perdonan y que están resueltos acá:
/// <list type="number">
///   <item>
///     <b>No soporta dos <c>SendAsync</c> a la vez.</b> No se queja con un error claro: entrelaza
///     los bytes de los dos mensajes y del otro lado llega JSON partido al medio. Con audio saliendo
///     cincuenta veces por segundo, que alguien mande texto justo en el medio no es una casualidad
///     rara, es cuestión de minutos. Por eso hay un semáforo y todos los envíos pasan por ahí.
///   </item>
///   <item>
///     <b>Un mensaje puede llegar en varios pedazos.</b> Los mensajes con audio son grandes y llegan
///     fragmentados; quedarse con el primer <c>ReceiveAsync</c> da JSON cortado. Se acumula hasta
///     que <c>EndOfMessage</c> diga que terminó.
///   </item>
/// </list>
/// </remarks>
public sealed class WebSocketLiveTransport : ILiveTransport
{
    private const int ReceiveBufferSize = 32 * 1024;

    /// <summary>Tope de un mensaje entero. Un turno de audio no llega ni cerca de esto.</summary>
    /// <remarks>
    /// Está para que un servidor que se vuelve loco no haga crecer un buffer hasta comerse la
    /// memoria del equipo. Si se supera, se corta la conexión: es preferible reconectar a quedarse
    /// sin RAM.
    /// </remarks>
    private const int MaximumMessageBytes = 24 * 1024 * 1024;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Func<ClientWebSocket> _factory;
    private ClientWebSocket? _socket;
    private int _disposed;

    /// <summary>Arma el transporte.</summary>
    /// <remarks>
    /// La fábrica existe para poder configurar el socket —proxy, tiempos— desde el anfitrión sin que
    /// este proyecto tenga que saber de eso.
    /// </remarks>
    public WebSocketLiveTransport(Func<ClientWebSocket>? socketFactory = null) =>
        _factory = socketFactory ?? (() => new ClientWebSocket());

    /// <inheritdoc />
    public bool IsOpen => _socket?.State == WebSocketState.Open;

    /// <inheritdoc />
    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var socket = _factory();
        _socket = socket;
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var socket = _socket ?? throw new InvalidOperationException("La sesión en vivo no está conectada.");
        var bytes = Encoding.UTF8.GetBytes(message);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("La sesión en vivo no está conectada.");

        var rented = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        MemoryStream? accumulated = null;

        try
        {
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(rented, cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Que se corte es normal en esta API: se informa como cierre y el cliente
                    // reconecta con el handle guardado.
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.EndOfMessage && accumulated is null)
                {
                    // El camino frecuente: entró entero en el buffer y no hace falta copiar nada.
                    return Encoding.UTF8.GetString(rented, 0, result.Count);
                }

                accumulated ??= new MemoryStream(ReceiveBufferSize * 2);
                if (accumulated.Length + result.Count > MaximumMessageBytes)
                {
                    return null;
                }

                accumulated.Write(rented, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
                }
            }
        }
        finally
        {
            accumulated?.Dispose();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Cerrar del todo bien es un lujo: si el otro lado ya se fue, insistir no aporta nada.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await CloseAsync(timeout.Token).ConfigureAwait(false);

        _socket?.Dispose();
        _socket = null;
        _sendGate.Dispose();
    }
}
