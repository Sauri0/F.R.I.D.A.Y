using Viernes.Core.Live;

namespace Viernes.Core.Tests.TestDoubles;

/// <summary>
/// Unos parlantes que <em>tardan</em> en sonar, que es lo que hace un parlante de verdad.
/// </summary>
/// <remarks>
/// <see cref="RecordingAudioSink"/> no sirve para esto: guarda los bytes pero nunca dice que le
/// queda algo por salir, así que con él «terminó el turno» y «se calló» son el mismo instante — y
/// justamente lo que hay que poder probar es que no lo son. El servidor despacha la respuesta más
/// rápido que tiempo real: cuando llega el <c>turnComplete</c> quedan segundos de voz de este lado.
/// <para>
/// El vaciado es a mano, con <see cref="Drain"/>, y no por reloj: una prueba que dependa de que
/// pasen milisegundos de verdad falla sola en una máquina cargada, y este repositorio ya tiene esa
/// clase de prueba en la lista de cosas a no repetir.
/// </para>
/// </remarks>
public sealed class DrainingAudioSink : ILiveAudioSink
{
    private readonly Lock _gate = new();
    private int _queuedBytes;

    /// <summary>Bytes que todavía no salieron.</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    /// <summary>Cuántas veces la mandaron a callar.</summary>
    public int FlushCount { get; private set; }

    /// <summary>Cuántos turnos se dieron por terminados.</summary>
    public int CompletedTurns { get; private set; }

    /// <inheritdoc />
    public TimeSpan Pending => LiveAudioFormat.OutputDurationOf(QueuedBytes);

    /// <inheritdoc />
    public ValueTask EnqueueAsync(ReadOnlyMemory<byte> pcm24k, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _queuedBytes += pcm24k.Length;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_gate)
        {
            _queuedBytes = 0;
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

    /// <summary>Terminó de sonar todo lo encolado.</summary>
    public void Drain()
    {
        lock (_gate)
        {
            _queuedBytes = 0;
        }
    }
}
