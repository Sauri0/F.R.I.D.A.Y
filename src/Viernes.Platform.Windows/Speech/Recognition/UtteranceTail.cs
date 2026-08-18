namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Lo que se sigue diciendo después de que el nombre ya sonó.
/// </summary>
/// <remarks>
/// La ventana rodante entrega lo anterior al nombre; esto junta lo posterior. Los dos vienen del
/// mismo flujo pero se toman en momentos distintos, así que la juntura tiene solapamiento: el
/// recorte se lleva todo hasta el byte N y el bloque que estaba llegando en ese instante arranca
/// antes de N. Sin recortar ese pedazo, la frase que le llega al modelo repite un cachito de sílaba
/// justo en el medio — y Whisper lo transcribe, porque está ahí.
/// </remarks>
public sealed class UtteranceTail
{
    private readonly MemoryStream _pcm = new();
    private readonly long _startPosition;

    /// <summary>
    /// Empieza a juntar audio a partir del byte <paramref name="startPosition"/> del flujo.
    /// </summary>
    public UtteranceTail(long startPosition)
    {
        if (startPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startPosition));
        }

        _startPosition = startPosition;
    }

    /// <summary>Bytes de audio juntados hasta ahora.</summary>
    public long Length => _pcm.Length;

    /// <summary>
    /// Suma un bloque del flujo, quedándose sólo con la parte que todavía no estaba tomada.
    /// </summary>
    /// <param name="chunkStartPosition">Posición del flujo donde empieza este bloque.</param>
    /// <param name="chunk">El bloque tal como lo entregó la captura.</param>
    public void Append(long chunkStartPosition, ReadOnlySpan<byte> chunk)
    {
        var chunkEnd = chunkStartPosition + chunk.Length;
        if (chunkEnd <= _startPosition)
        {
            return;
        }

        var skip = _startPosition > chunkStartPosition
            ? (int)(_startPosition - chunkStartPosition)
            : 0;
        _pcm.Write(chunk[skip..]);
    }

    /// <summary>Copia lo juntado.</summary>
    public byte[] ToArray() => _pcm.ToArray();
}
