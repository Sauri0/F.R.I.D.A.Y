using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// La compuerta que convierte el veredicto bloque a bloque en «arrancó» y «terminó».
/// </summary>
public sealed class LiveUserSpeechGateTests
{
    private static readonly TimeSpan Bloque = TimeSpan.FromMilliseconds(20);

    private static LiveUserSpeechGate Armar(int silencioMs = 700) =>
        new(TimeSpan.FromMilliseconds(silencioMs));

    [Fact]
    public void ElPrimerBloqueConVoz_AbreLaFrase()
    {
        var gate = Armar();

        Assert.Equal(LiveSpeechEdge.Started, gate.Write(isVoice: true, Bloque));
        Assert.True(gate.IsSpeaking);
    }

    [Fact]
    public void MientrasSigueHablando_NoHayMasBordes()
    {
        var gate = Armar();
        gate.Write(isVoice: true, Bloque);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(LiveSpeechEdge.None, gate.Write(isVoice: true, Bloque));
        }
    }

    [Fact]
    public void UnaPausaCortaNoCierraLaFrase()
    {
        // Es el caso que justifica toda la clase: entre dos palabras hay silencio, y un borde de
        // «terminó» en cada silencio manda el orbe de «te escucho» a «pensando» tres veces por
        // oración.
        var gate = Armar(silencioMs: 700);
        gate.Write(isVoice: true, Bloque);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(LiveSpeechEdge.None, gate.Write(isVoice: false, Bloque));
        }

        Assert.True(gate.IsSpeaking);
    }

    [Fact]
    public void AlAguantarElSilencioCompleto_CierraLaFrase()
    {
        var gate = Armar(silencioMs: 700);
        gate.Write(isVoice: true, Bloque);

        LiveSpeechEdge ultimo = LiveSpeechEdge.None;
        for (var i = 0; i < 35 && ultimo == LiveSpeechEdge.None; i++)
        {
            ultimo = gate.Write(isVoice: false, Bloque);
        }

        Assert.Equal(LiveSpeechEdge.Finished, ultimo);
        Assert.False(gate.IsSpeaking);
    }

    [Fact]
    public void ElSilencioSeReiniciaConCadaPalabra()
    {
        var gate = Armar(silencioMs: 700);
        gate.Write(isVoice: true, Bloque);

        // Casi cierra…
        for (var i = 0; i < 30; i++)
        {
            gate.Write(isVoice: false, Bloque);
        }

        // …y vuelve a hablar: el contador arranca de cero otra vez.
        gate.Write(isVoice: true, Bloque);
        Assert.Equal(TimeSpan.Zero, gate.Silence);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(LiveSpeechEdge.None, gate.Write(isVoice: false, Bloque));
        }
    }

    [Fact]
    public void ElSilencioDeQuienNoHabloNoSeAcumula()
    {
        // Si se acumulara, la primera vez que alguien habla después de un rato callado saldría un
        // «terminó» apenas se calle una milésima: el contador ya venía pasado de largo.
        var gate = Armar(silencioMs: 700);

        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(LiveSpeechEdge.None, gate.Write(isVoice: false, Bloque));
        }

        Assert.Equal(LiveSpeechEdge.Started, gate.Write(isVoice: true, Bloque));
        Assert.Equal(LiveSpeechEdge.None, gate.Write(isVoice: false, Bloque));
    }

    [Fact]
    public void CerradaLaFrase_LaSiguienteVuelveAAbrir()
    {
        var gate = Armar(silencioMs: 100);
        gate.Write(isVoice: true, Bloque);
        for (var i = 0; i < 5; i++)
        {
            gate.Write(isVoice: false, Bloque);
        }

        Assert.False(gate.IsSpeaking);
        Assert.Equal(LiveSpeechEdge.Started, gate.Write(isVoice: true, Bloque));
    }

    [Fact]
    public void ReiniciarLaDejaComoAlPrincipio()
    {
        var gate = Armar();
        gate.Write(isVoice: true, Bloque);

        gate.Reset();

        Assert.False(gate.IsSpeaking);
        Assert.Equal(TimeSpan.Zero, gate.Silence);
        Assert.Equal(LiveSpeechEdge.Started, gate.Write(isVoice: true, Bloque));
    }

    [Fact]
    public void UnSilencioSinDuracionNoEsUnaCompuerta()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveUserSpeechGate(TimeSpan.Zero));
    }
}
