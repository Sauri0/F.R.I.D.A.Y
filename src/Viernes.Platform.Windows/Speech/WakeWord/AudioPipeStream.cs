namespace Viernes.Platform.Windows.Speech.WakeWord;

/// <summary>
/// Caño de audio de un solo sentido: la captura escribe de un lado y SAPI lee del otro.
/// </summary>
/// <remarks>
/// Esto es lo que permite que el micrófono lo abra <em>una sola</em> aplicación. Antes SAPI abría el
/// dispositivo por su cuenta para escuchar el nombre y la captura de Whisper lo abría de nuevo
/// después; de ahí salía toda la coreografía de detener uno, esperar 220 ms a que el driver soltara
/// y recién ahí abrir el otro. Y sobre todo: mientras SAPI tenía el micrófono, el audio anterior al
/// nombre no lo tenía nadie, así que decir «Viernes creame una carpeta» de un tirón era imposible.
/// <para>
/// Con esto la captura es dueña única del dispositivo y a SAPI se le pasa el mismo PCM por
/// <c>SetInputToAudioStream</c>. El audio se reparte a la ventana rodante y al reconocedor de
/// nombre en el mismo instante.
/// </para>
/// <para>
/// Dos detalles que no son opcionales. Uno: <see cref="Read(byte[], int, int)"/> bloquea en vez de
/// devolver cero, porque para SAPI un cero es fin de audio y apaga el reconocimiento — un silencio
/// de medio segundo le terminaría la sesión. Dos: cuando el consumidor se atrasa se tira lo más
/// viejo en lugar de crecer sin límite; un proceso que arranca con Windows y escucha todo el día no
/// puede tener una cola que sólo crece.
/// </para>
/// </remarks>
public sealed class AudioPipeStream : Stream
{
    private readonly object _sync = new();
    private readonly byte[] _ring;
    private int _start;
    private int _length;
    private bool _completed;
    private long _droppedBytes;

    /// <summary>
    /// Crea el caño con capacidad para <paramref name="capacity"/> de audio del formato dado.
    /// </summary>
    public AudioPipeStream(TimeSpan capacity, int bytesPerSecond)
    {
        if (capacity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (bytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        }

        var bytes = (long)Math.Ceiling(bytesPerSecond * capacity.TotalSeconds);
        if (bytes is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ring = new byte[(int)bytes];
    }

    /// <summary>Bytes que se tiraron por atraso del lector. Si crece, SAPI no está dando abasto.</summary>
    public long DroppedBytes
    {
        get
        {
            lock (_sync)
            {
                return _droppedBytes;
            }
        }
    }

    /// <summary>Bytes esperando ser leídos.</summary>
    public int Available
    {
        get
        {
            lock (_sync)
            {
                return _length;
            }
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // No hay nada que vaciar: el caño vive en memoria y el lector ya está esperando.
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            while (_length == 0 && !_completed)
            {
                Monitor.Wait(_sync);
            }

            if (_length == 0)
            {
                // Sólo acá se devuelve cero, y sólo cuando el caño se cerró a propósito: es la única
                // forma de que SAPI termine su bucle en vez de quedarse trabado adentro de Read.
                return 0;
            }

            var take = Math.Min(buffer.Length, _length);
            var first = Math.Min(take, _ring.Length - _start);
            _ring.AsSpan(_start, first).CopyTo(buffer);
            if (first < take)
            {
                _ring.AsSpan(0, take - first).CopyTo(buffer[first..]);
            }

            _start = (_start + take) % _ring.Length;
            _length -= take;
            return take;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            var data = buffer;
            if (data.Length >= _ring.Length)
            {
                _droppedBytes += _length + data.Length - _ring.Length;
                data = data[^_ring.Length..];
                data.CopyTo(_ring);
                _start = 0;
                _length = _ring.Length;
                Monitor.PulseAll(_sync);
                return;
            }

            var writeAt = (_start + _length) % _ring.Length;
            var first = Math.Min(data.Length, _ring.Length - writeAt);
            data[..first].CopyTo(_ring.AsSpan(writeAt));
            if (first < data.Length)
            {
                data[first..].CopyTo(_ring);
            }

            var overflow = _length + data.Length - _ring.Length;
            if (overflow > 0)
            {
                _droppedBytes += overflow;
                _start = (_start + overflow) % _ring.Length;
                _length = _ring.Length;
            }
            else
            {
                _length += data.Length;
            }

            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    /// Cierra el caño: el lector que estaba esperando se despierta y recibe fin de audio.
    /// </summary>
    public void Complete()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            Monitor.PulseAll(_sync);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Complete();
        }

        base.Dispose(disposing);
    }
}
