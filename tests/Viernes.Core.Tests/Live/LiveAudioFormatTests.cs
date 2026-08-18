using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Las dos frecuencias, que no son la misma y no se pueden mezclar.
/// </summary>
public sealed class LiveAudioFormatTests
{
    [Fact]
    public void EntradaYSalida_NoTienenLaMismaFrecuencia()
    {
        Assert.Equal(16_000, LiveAudioFormat.InputSampleRate);
        Assert.Equal(24_000, LiveAudioFormat.OutputSampleRate);
        Assert.NotEqual(LiveAudioFormat.InputSampleRate, LiveAudioFormat.OutputSampleRate);
    }

    [Fact]
    public void ElTipoMimeDeEntrada_DiceLaFrecuenciaDeEntrada()
    {
        // Va escrita adentro del propio tipo MIME: si se separan, el servidor recibe una mentira.
        Assert.Equal("audio/pcm;rate=16000", LiveAudioFormat.InputMimeType);
        Assert.Equal("audio/pcm;rate=24000", LiveAudioFormat.OutputMimeType);
    }

    [Theory]
    [InlineData(20, 640)]
    [InlineData(40, 1_280)]
    [InlineData(1_000, 32_000)]
    public void UnFragmentoDeEntrada_OcupaLosBytesQueCorresponden(int milliseconds, int expectedBytes)
    {
        Assert.Equal(expectedBytes, LiveAudioFormat.InputBytesForMilliseconds(milliseconds));
    }

    [Fact]
    public void UnFragmentoSiempreCierraEnMuestraEntera()
    {
        // Mandar medio par de bytes desalinea todo lo que sigue.
        for (var ms = 1; ms <= 60; ms++)
        {
            Assert.Equal(0, LiveAudioFormat.InputBytesForMilliseconds(ms) % LiveAudioFormat.BytesPerSample);
        }
    }

    [Fact]
    public void LaDuracionDeLaSalida_SeCalculaAVeinticuatroKilohertz()
    {
        var unSegundo = LiveAudioFormat.OutputSampleRate * LiveAudioFormat.BytesPerSample;

        Assert.Equal(1, LiveAudioFormat.OutputDurationOf(unSegundo).TotalSeconds, 6);

        // El mismo bloque interpretado como entrada dura un segundo y medio: es exactamente lo que
        // se oye cuando se confunden las frecuencias, y por eso suena grave y lento en vez de fallar.
        Assert.Equal(1.5, LiveAudioFormat.InputDurationOf(unSegundo).TotalSeconds, 6);
    }

    [Fact]
    public void UnBloqueVacio_NoDuraNada()
    {
        Assert.Equal(TimeSpan.Zero, LiveAudioFormat.OutputDurationOf(0));
        Assert.Equal(0, LiveAudioFormat.InputBytesForMilliseconds(-5));
    }
}
