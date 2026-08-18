namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Recorte de la ventana rodante: el PCM pedido y hasta qué byte del flujo llega.
/// </summary>
/// <remarks>
/// Lleva <see cref="EndPosition"/> porque el recorte y lo que se sigue grabando después ocurren en
/// hilos distintos. Sin un punto de corte explícito, la cola volvía a pegar los mismos milisegundos
/// que ya venían en el recorte y la frase quedaba con un tartamudeo en la juntura.
/// </remarks>
public sealed record AudioSnapshot(byte[] Pcm, long EndPosition, TimeSpan Duration);

/// <summary>
/// Lo último que entró por el micrófono, siempre en memoria y nunca en disco.
/// </summary>
/// <remarks>
/// Existe para contestar la pregunta que antes no tenía respuesta: «¿qué dijo <em>antes</em> de
/// nombrarme?». El detector de nombre avisa cuando la palabra ya pasó, así que sin una ventana
/// rodante todo lo anterior estaba perdido y lo único que quedaba era pedir que lo repita. Con diez
/// segundos guardados, «Viernes creame una carpeta» y «che, necesito que Viernes me abra Spotify»
/// llegan enteros al modelo.
/// <para>
/// Es un anillo de bytes de tamaño fijo: se reserva una vez y no crece nunca. Guardar PCM crudo en
/// memoria y no archivos temporales es deliberado — al cerrar el proceso no queda rastro de nada de
/// lo que se oyó, que es lo que hace aceptable tener el micrófono siempre abierto.
/// </para>
/// </remarks>
public sealed class RollingAudioBuffer
{
    private readonly object _sync = new();
    private readonly byte[] _ring;
    private readonly int _bytesPerSecond;
    private readonly int _blockAlign;
    private int _start;
    private int _length;
    private long _position;

    /// <summary>
    /// Crea la ventana para un formato PCM dado. La capacidad se redondea hacia arriba al bloque.
    /// </summary>
    public RollingAudioBuffer(TimeSpan capacity, int bytesPerSecond, int blockAlign)
    {
        if (capacity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (bytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        }

        if (blockAlign <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockAlign));
        }

        _bytesPerSecond = bytesPerSecond;
        _blockAlign = blockAlign;
        var bytes = (long)Math.Ceiling(bytesPerSecond * capacity.TotalSeconds);
        bytes += (blockAlign - (bytes % blockAlign)) % blockAlign;
        if (bytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _ring = new byte[(int)bytes];
        Capacity = TimeSpan.FromSeconds((double)bytes / bytesPerSecond);
    }

    /// <summary>Cuánto audio entra en la ventana antes de que lo viejo se pise.</summary>
    public TimeSpan Capacity { get; }

    /// <summary>Bytes que pasaron por acá desde siempre; sirve como reloj del flujo.</summary>
    public long Position
    {
        get
        {
            lock (_sync)
            {
                return _position;
            }
        }
    }

    /// <summary>Cuánto audio hay guardado ahora mismo.</summary>
    public TimeSpan Buffered
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromSeconds((double)_length / _bytesPerSecond);
            }
        }
    }

    /// <summary>
    /// Agrega audio al final y descarta el más viejo si hace falta.
    /// </summary>
    /// <remarks>
    /// Devuelve la posición del flujo <em>antes</em> de escribir: quien graba la cola de una frase
    /// necesita saber en qué byte empieza el trozo que acaba de llegar para no repetir lo que ya se
    /// había llevado el recorte.
    /// </remarks>
    public long Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            var before = _position;
            _position += data.Length;

            // Un bloque más grande que toda la ventana no tiene nada que guardar salvo su propia
            // cola: lo anterior ya quedó pisado por definición.
            if (data.Length >= _ring.Length)
            {
                data[^_ring.Length..].CopyTo(_ring);
                _start = 0;
                _length = _ring.Length;
                return before;
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
                _start = (_start + overflow) % _ring.Length;
                _length = _ring.Length;
            }
            else
            {
                _length += data.Length;
            }

            return before;
        }
    }

    /// <summary>
    /// Copia los últimos <paramref name="lookback"/> de audio guardado.
    /// </summary>
    /// <param name="lookback">Cuánto mirar hacia atrás; se recorta a lo que haya.</param>
    /// <returns>El PCM copiado y hasta qué byte del flujo llega.</returns>
    public AudioSnapshot Snapshot(TimeSpan lookback)
    {
        lock (_sync)
        {
            var wanted = lookback <= TimeSpan.Zero
                ? 0
                : (int)Math.Min(_length, (long)(_bytesPerSecond * lookback.TotalSeconds));

            // Cortar a mitad de muestra convierte el resto del audio en ruido: los bytes se
            // reinterpretan corridos y lo que sale es siseo, no voz.
            wanted -= wanted % _blockAlign;
            var result = new byte[wanted];
            var from = (_start + (_length - wanted)) % _ring.Length;
            var first = Math.Min(wanted, _ring.Length - from);
            _ring.AsSpan(from, first).CopyTo(result);
            if (first < wanted)
            {
                _ring.AsSpan(0, wanted - first).CopyTo(result.AsSpan(first));
            }

            return new AudioSnapshot(
                result,
                _position,
                TimeSpan.FromSeconds((double)wanted / _bytesPerSecond));
        }
    }

    /// <summary>Olvida todo lo guardado sin perder la cuenta del flujo.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_ring);
            _start = 0;
            _length = 0;
        }
    }
}
