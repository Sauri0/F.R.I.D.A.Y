using System.Threading.Channels;
using Viernes.Core.Live;

namespace Viernes.Core.Tests.TestDoubles;

/// <summary>
/// Un servidor de mentira que dice exactamente lo que la prueba quiere y cuándo lo quiere.
/// </summary>
/// <remarks>
/// Es la única forma de verificar lo que importa. «Cuando llega <c>interrupted</c> se vacía la cola»
/// no se puede probar contra Google: habría que hablarle encima en el momento justo, pagar el turno
/// y confiar en que el servidor haga lo mismo la próxima vez.
/// </remarks>
public sealed class FakeLiveTransport : ILiveTransport
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    /// <summary>Todo lo que el cliente mandó, en orden.</summary>
    public List<string> Sent { get; } = [];

    /// <summary>La dirección con la que lo llamaron. Lleva la clave: no se escribe en ningún lado.</summary>
    public Uri? Endpoint { get; private set; }

    /// <inheritdoc />
    public bool IsOpen { get; private set; }

    /// <summary>Si ya lo cerraron.</summary>
    public bool WasClosed { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        Endpoint = endpoint;
        IsOpen = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) =>
        await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        WasClosed = true;
        IsOpen = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        WasClosed = true;
        IsOpen = false;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Le hace decir esto al servidor.</summary>
    public void Deliver(string message) => _incoming.Writer.TryWrite(message);

    /// <summary>Simula que el servidor cortó la conexión.</summary>
    public void DeliverClose() => _incoming.Writer.TryWrite(null);

    /// <summary>Copia de lo mandado, para poder revisarla sin carreras.</summary>
    public IReadOnlyList<string> SentSnapshot()
    {
        lock (Sent)
        {
            return Sent.ToArray();
        }
    }
}
