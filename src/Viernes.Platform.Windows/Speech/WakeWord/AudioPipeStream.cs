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
    private long _read;

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

    /// <summary>
    /// Cuánto audio va a entregar. Es un caño abierto: no tiene final.
    /// </summary>
    /// <remarks>
    /// <b>Acá vivía el motivo por el que el oído continuo nunca se oyó funcionar.</b> Esto lanzaba
    /// <c>NotSupportedException</c>, que es lo correcto para un stream sin largo, y SAPI pide
    /// <c>Length</c> adentro del constructor de su envoltorio —<c>SpStreamWrapper</c>— apenas se le
    /// pasa el caño por <c>SetInputToAudioStream</c>. Esa excepción no es ninguna de las que el oído
    /// da por esperadas, así que se iba para arriba desde el arranque del servicio y la
    /// inicialización del asistente quedaba a medio hacer, sin un solo renglón en la bitácora.
    /// <para>
    /// Devolver <see cref="long.MaxValue"/> es lo que corresponde y no un parche: SAPI usa este
    /// número sólo para saber cuándo dejar de pedir, y de este caño nunca hay que dejar de pedir. El
    /// fin de audio lo da <see cref="Complete"/>, con un <see cref="Read(System.Span{byte})"/> que
    /// devuelve cero.
    /// </para>
    /// </remarks>
    public override long Length => long.MaxValue;

    /// <summary>Cuántos bytes se entregaron. No se puede mover: es un caño de un solo sentido.</summary>
    public override long Position
    {
        get
        {
            lock (_sync)
            {
                return _read;
            }
        }

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

    /// <summary>
    /// Entrega exactamente lo que le pidieron, esperando lo que haga falta.
    /// </summary>
    /// <remarks>
    /// <b>Llenar el búfer entero no es cortesía: es la única forma de que SAPI siga leyendo.</b>
    /// Medido con un espía entre el caño y el reconocedor: devolviendo menos de lo pedido —960 bytes
    /// donde pidió 3040— SAPI hizo <em>una sola</em> lectura y no volvió a pedir nunca más. Con el
    /// búfer lleno siguió pidiendo hasta el final del audio. Un byte de menos se lee igual que un
    /// cero, y un cero es fin de audio.
    /// <para>
    /// Se devuelve menos únicamente cuando el caño se cerró con <see cref="Complete"/>, que es el fin
    /// de audio de verdad y la única forma de que el hilo de SAPI salga de acá.
    /// </para>
    /// </remarks>
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var total = 0;
        lock (_sync)
        {
            while (total < buffer.Length)
            {
                while (_length == 0 && !_completed)
                {
                    Monitor.Wait(_sync);
                }

                if (_length == 0)
                {
                    // El caño cerró: se entrega lo que se juntó, y si no se juntó nada, cero.
                    break;
                }

                var take = Math.Min(buffer.Length - total, _length);
                var first = Math.Min(take, _ring.Length - _start);
                _ring.AsSpan(_start, first).CopyTo(buffer[total..]);
                if (first < take)
                {
                    _ring.AsSpan(0, take - first).CopyTo(buffer[(total + first)..]);
                }

                _start = (_start + take) % _ring.Length;
                _length -= take;
                _read += take;
                total += take;
            }
        }

        return total;
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

    /// <summary>
    /// Un caño no se mueve. Lo único que se contesta es «¿dónde estoy?».
    /// </summary>
    /// <remarks>
    /// Lo pregunta SAPI antes de leer un solo byte, con el modismo de siempre —desplazamiento cero
    /// desde la posición actual—, que no es moverse: es preguntar. Lanzando ahí, la excepción cruza
    /// el borde COM, se convierte en un HRESULT de error y SAPI abandona la entrada <b>sin decir
    /// nada</b>: no lee nunca, no reconoce nunca y no falla nunca. Medido: con esto lanzando, el caño
    /// entregaba cero bytes en cinco segundos de audio empujado y tiraba el resto por atraso.
    /// <para>
    /// Cualquier pedido que sí implique moverse sigue lanzando: mentir ahí sería peor, porque el
    /// audio saldría corrido y sonaría a ruido en vez de fallar.
    /// </para>
    /// </remarks>
    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            if ((origin == SeekOrigin.Current && offset == 0) ||
                (origin == SeekOrigin.Begin && offset == _read))
            {
                return _read;
            }
        }

        throw new NotSupportedException();
    }

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
