using System.Buffers.Binary;
using Viernes.Platform.Windows.Speech.Recognition;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// La ventana rodante y el recorte de la juntura: lo que hace posible decir el nombre en el medio.
/// </summary>
/// <remarks>
/// Se prueba acá y no con un micrófono porque es aritmética sobre bytes: si el anillo se pisa mal o
/// la juntura repite audio, el modelo recibe una frase con una sílaba duplicada en el medio y no hay
/// forma de darse cuenta escuchando el resultado — Whisper transcribe lo que le den.
/// </remarks>
public sealed class RollingAudioBufferTests
{
    private const int BytesPerSecond = 32_000;
    private const int BlockAlign = 2;

    private static byte[] Ramp(int bytes, byte from = 0)
    {
        var data = new byte[bytes];
        for (var index = 0; index < bytes; index++)
        {
            data[index] = (byte)(from + index);
        }

        return data;
    }

    [Fact]
    public void Snapshot_ConMenosAudioDelPedido_DevuelveLoQueHay()
    {
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), BytesPerSecond, BlockAlign);
        buffer.Write(Ramp(1000));

        var snapshot = buffer.Snapshot(TimeSpan.FromSeconds(10));

        Assert.Equal(1000, snapshot.Pcm.Length);
        Assert.Equal(1000, snapshot.EndPosition);
    }

    [Fact]
    public void Write_MasQueLaCapacidad_ConservaLoUltimo()
    {
        // Escuchar todo el día con una ventana de diez segundos sólo sirve si lo viejo se pisa: es
        // lo que hace que el micrófono siempre abierto no sea una grabación.
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(0.25), BytesPerSecond, BlockAlign);
        var capacity = BytesPerSecond / 4;
        buffer.Write(Ramp(capacity * 3));

        var snapshot = buffer.Snapshot(TimeSpan.FromSeconds(1));

        Assert.Equal(capacity, snapshot.Pcm.Length);
        Assert.Equal(capacity * 3, snapshot.EndPosition);
        Assert.Equal((byte)((capacity * 3) - capacity), snapshot.Pcm[0]);
    }

    [Fact]
    public void Write_EnVariosTrozos_ElAnilloDaLaVueltaSinDesordenarse()
    {
        // El caso que rompe una implementación ingenua: la escritura cae justo sobre el final del
        // arreglo y hay que partirla en dos.
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(0.1), BytesPerSecond, BlockAlign);
        var capacity = BytesPerSecond / 10;
        var written = 0;
        while (written < capacity * 2)
        {
            buffer.Write(Ramp(700, (byte)written));
            written += 700;
        }

        var snapshot = buffer.Snapshot(TimeSpan.FromSeconds(1));

        Assert.Equal(capacity, snapshot.Pcm.Length);
        for (var index = 1; index < snapshot.Pcm.Length; index++)
        {
            // Cada byte tiene que ser el siguiente del anterior; un salto significa que el anillo
            // devolvió los pedazos al revés.
            Assert.Equal((byte)(snapshot.Pcm[index - 1] + 1), snapshot.Pcm[index]);
        }
    }

    [Fact]
    public void Snapshot_ConUnLargoImpar_NoCortaAMitadDeMuestra()
    {
        // Media muestra corre todos los bytes que siguen: el audio se reinterpreta y sale siseo.
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), BytesPerSecond, BlockAlign);
        buffer.Write(Ramp(10_000));

        var snapshot = buffer.Snapshot(TimeSpan.FromSeconds(0.0000937));

        Assert.Equal(0, snapshot.Pcm.Length % BlockAlign);
    }

    [Fact]
    public void Write_DevuelveLaPosicionAnteriorAEscribir()
    {
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), BytesPerSecond, BlockAlign);

        Assert.Equal(0, buffer.Write(Ramp(100)));
        Assert.Equal(100, buffer.Write(Ramp(50)));
        Assert.Equal(150, buffer.Position);
    }

    [Fact]
    public void Tail_ConUnBloqueQueSolapaElRecorte_TiraLaParteRepetida()
    {
        // Éste es el bug que la juntura provoca sola: el recorte se lleva hasta el byte 100 y el
        // bloque que estaba llegando arranca en el 90. Sin recortar, esos diez bytes se dicen dos
        // veces.
        var tail = new UtteranceTail(startPosition: 100);

        tail.Append(90, Ramp(20));

        Assert.Equal(10, tail.Length);
        Assert.Equal(10, tail.ToArray()[0]);
    }

    [Fact]
    public void Tail_ConUnBloqueEnteramenteAnterior_NoTomaNada()
    {
        var tail = new UtteranceTail(startPosition: 100);

        tail.Append(0, Ramp(50));

        Assert.Equal(0, tail.Length);
    }

    [Fact]
    public void Tail_ConBloquesPosteriores_LosPegaEnOrden()
    {
        var tail = new UtteranceTail(startPosition: 0);

        tail.Append(0, [1, 2]);
        tail.Append(2, [3, 4]);

        Assert.Equal<byte[]>([1, 2, 3, 4], tail.ToArray());
    }

    [Fact]
    public void CreateWave_PegaLosDosPedazosConUnaCabeceraValida()
    {
        var wave = WaveAudio.CreateWave([new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6 }]);

        var bytes = wave.ToArray();
        Assert.Equal(WaveAudio.HeaderBytes + 6, bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6], bytes[WaveAudio.HeaderBytes..]);
    }

    [Fact]
    public void CreateWave_ConUnLargoImpar_DescartaElByteSuelto()
    {
        var wave = WaveAudio.CreateWave([new byte[] { 1, 2, 3 }]);

        Assert.Equal(WaveAudio.HeaderBytes + 2, wave.Length);
    }
}
