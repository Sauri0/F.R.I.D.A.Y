using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

public sealed class GeminiLiveOptionsTests
{
    private static Func<string, string?> Entorno(params (string Name, string Value)[] pares) =>
        name => pares.FirstOrDefault(p => p.Name == name).Value;

    [Fact]
    public void PorDefecto_VieneApagada()
    {
        // El camino de siempre tiene que seguir andando sin que nadie decida nada.
        Assert.False(new GeminiLiveOptions().Enabled);
        Assert.False(GeminiLiveOptions.FromEnvironment(Entorno()).Enabled);
    }

    [Fact]
    public void PorDefecto_ElModeloYLaVozSonLosVerificados()
    {
        var options = new GeminiLiveOptions();

        Assert.Equal("gemini-3.1-flash-live-preview", options.Model);
        Assert.Equal("Aoede", options.VoiceName);
    }

    [Fact]
    public void PorDefecto_ElSilencioEstaEnLaVentanaRecomendada()
    {
        var options = new GeminiLiveOptions();

        // Google recomienda entre 500 y 800; abajo de eso parte la oración en varios turnos.
        Assert.InRange(options.SilenceDurationMs, 500, 800);
    }

    [Fact]
    public void PorDefecto_ElFragmentoEsDeVeinteMilisegundos()
    {
        var options = new GeminiLiveOptions();

        Assert.Equal(20, options.ChunkMilliseconds);
        Assert.Equal(640, options.ChunkBytes);
    }

    [Fact]
    public void SeEnciendeConVariableDeEntorno()
    {
        Assert.True(GeminiLiveOptions.FromEnvironment(Entorno((GeminiLiveOptions.EnabledEnvironmentVariable, "true"))).Enabled);
        Assert.True(GeminiLiveOptions.FromEnvironment(Entorno((GeminiLiveOptions.EnabledEnvironmentVariable, "1"))).Enabled);
        Assert.True(GeminiLiveOptions.FromEnvironment(Entorno((GeminiLiveOptions.EnabledEnvironmentVariable, "sí"))).Enabled);
        Assert.False(GeminiLiveOptions.FromEnvironment(Entorno((GeminiLiveOptions.EnabledEnvironmentVariable, "off"))).Enabled);
    }

    [Fact]
    public void UnaVariableMalEscrita_CaeAlValorPorDefectoYNoTumbaElArranque()
    {
        var options = GeminiLiveOptions.FromEnvironment(Entorno(
            (GeminiLiveOptions.SilenceEnvironmentVariable, "setecientos"),
            (GeminiLiveOptions.ChunkEnvironmentVariable, "9999")));

        Assert.Equal(GeminiLiveOptions.DefaultSilenceDurationMs, options.SilenceDurationMs);
        Assert.Equal(GeminiLiveOptions.DefaultChunkMilliseconds, options.ChunkMilliseconds);
    }

    [Fact]
    public void UnSilencioAbsurdo_SeRechaza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveOptions(silenceDurationMs: 7_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveOptions(silenceDurationMs: 0));
    }

    [Fact]
    public void UnaVentanaDeCompresionQueNoLiberaNada_SeRechaza()
    {
        // Recortar a un valor mayor o igual que el disparador comprime sin liberar y vuelve a
        // disparar en el turno siguiente.
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveOptions(triggerTokens: 10_000, targetTokens: 10_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeminiLiveOptions(triggerTokens: 10_000, targetTokens: 20_000));
    }

    [Fact]
    public void WithSystemInstruction_ConservaLoDemas()
    {
        var original = new GeminiLiveOptions(enabled: true, silenceDurationMs: 550, chunkMilliseconds: 40);

        var copia = original.WithSystemInstruction("Sos Viernes.");

        Assert.Equal("Sos Viernes.", copia.SystemInstruction);
        Assert.True(copia.Enabled);
        Assert.Equal(550, copia.SilenceDurationMs);
        Assert.Equal(40, copia.ChunkMilliseconds);
    }

    [Fact]
    public void ToString_NoTieneNadaSecretoQueMostrar()
    {
        var texto = new GeminiLiveOptions(enabled: true).ToString();

        Assert.Contains("Aoede", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("key", texto, StringComparison.OrdinalIgnoreCase);
    }
}
