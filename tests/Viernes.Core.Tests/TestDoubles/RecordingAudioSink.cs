using Viernes.Core.Live;

namespace Viernes.Core.Tests.TestDoubles;

/// <summary>
/// Unos parlantes de mentira que además se acuerdan de lo que les tiraron.
/// </summary>
/// <remarks>
/// <see cref="Queued"/> se vacía de verdad en <see cref="Flush"/>. Si sólo contara las llamadas, la
/// prueba pasaría con una implementación que anota la interrupción y sigue reproduciendo, que es
/// exactamente el bug que hay que impedir.
/// </remarks>
public sealed class RecordingAudioSink : ILiveAudioSink
{
    private readonly Lock _gate = new();
    private readonly List<byte> _queued = [];

    /// <summary>Bytes que hay encolados ahora.</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    /// <summary>Total de bytes que pasaron por la cola desde siempre.</summary>
    public int TotalEnqueuedBytes { get; private set; }

    /// <summary>Cuántas veces la mandaron a callar.</summary>
    public int FlushCount { get; private set; }

    /// <summary>Cuántos turnos se dieron por terminados.</summary>
    public int CompletedTurns { get; private set; }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(ReadOnlyMemory<byte> pcm24k, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _queued.AddRange(pcm24k.Span);
            TotalEnqueuedBytes += pcm24k.Length;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_gate)
        {
            _queued.Clear();
            FlushCount++;
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteTurnAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CompletedTurns++;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Lo que quedó encolado, en bytes.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            return _queued.ToArray();
        }
    }
}
