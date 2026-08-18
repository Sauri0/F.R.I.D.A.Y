using System.Buffers.Binary;

namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Arma un WAV en memoria a partir de PCM suelto.
/// </summary>
/// <remarks>
/// Whisper recibe un WAV, no muestras sueltas, y la frase completa sale de pegar dos pedazos que se
/// grabaron en momentos distintos: lo que había en la ventana rodante antes de que sonara el nombre
/// y lo que se siguió diciendo después. Escribir la cabecera a mano —en vez de pasar por el escritor
/// de NAudio— es lo que permite pegarlos sin copiar todo dos veces y, sobre todo, probar esta parte
/// sin micrófono: se le dan dos arreglos de bytes y se leen los campos del resultado.
/// </remarks>
public static class WaveAudio
{
    /// <summary>Bytes que ocupa la cabecera RIFF/WAVE mínima.</summary>
    public const int HeaderBytes = 44;

    /// <summary>
    /// Pega los trozos de PCM en un WAV completo.
    /// </summary>
    /// <param name="parts">Los trozos, en orden; los vacíos se ignoran.</param>
    /// <param name="sampleRate">Muestras por segundo.</param>
    /// <param name="bitsPerSample">Bits por muestra.</param>
    /// <param name="channels">Cantidad de canales.</param>
    /// <returns>Un stream posicionado en cero, listo para transcribir.</returns>
    public static MemoryStream CreateWave(
        IReadOnlyList<ReadOnlyMemory<byte>> parts,
        int sampleRate = 16_000,
        int bitsPerSample = 16,
        int channels = 1)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var blockAlign = channels * bitsPerSample / 8;
        var dataLength = parts.Sum(part => (long)part.Length);

        // Un largo que no cae en bloque entero deja media muestra al final: los bytes se
        // reinterpretan corridos y el final del audio suena a siseo.
        dataLength -= dataLength % blockAlign;
        if (dataLength > int.MaxValue - HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(parts));
        }

        var stream = new MemoryStream(HeaderBytes + (int)dataLength);
        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataLength));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataLength);
        stream.Write(header);

        var remaining = dataLength;
        foreach (var part in parts)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = (int)Math.Min(part.Length, remaining);
            stream.Write(part.Span[..take]);
            remaining -= take;
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>Cuánto dura un PCM de este formato.</summary>
    /// <param name="pcmBytes">Cantidad de bytes de audio, sin cabecera.</param>
    /// <param name="sampleRate">Muestras por segundo.</param>
    /// <param name="bitsPerSample">Bits por muestra.</param>
    /// <param name="channels">Cantidad de canales.</param>
    /// <returns>La duración del audio.</returns>
    public static TimeSpan Duration(
        long pcmBytes,
        int sampleRate = 16_000,
        int bitsPerSample = 16,
        int channels = 1) =>
        TimeSpan.FromSeconds((double)pcmBytes / (sampleRate * channels * bitsPerSample / 8));
}
