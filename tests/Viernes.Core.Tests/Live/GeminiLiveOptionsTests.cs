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
        // Por reflexión y no campo por campo, y ese cambio salió de un defecto de verdad: la copia
        // se olvidó de WebSearch cuando se agregó, así que el interruptor de la búsqueda hablada
        // existía, se leía del entorno, y se perdía en silencio a mitad de camino. La prueba miraba
        // cuatro propiedades de doce y no vio nada.
        //
        // Una copia que se olvida un campo es peor que no tener copia. Esto no se puede olvidar de
        // ninguno: los recorre todos y el único que puede diferir es el que se cambió a propósito.
        var original = new GeminiLiveOptions(
            enabled: true,
            model: "un-modelo",
            voiceName: "Puck",
            systemInstruction: "la vieja",
            silenceDurationMs: 550,
            chunkMilliseconds: 40,
            interruptOnUserSpeech: false,
            transcribeInput: false,
            transcribeOutput: false,
            triggerTokens: 12_345,
            targetTokens: 6_789,
            setupTimeout: TimeSpan.FromSeconds(7),
            webSearch: false);

        var copia = original.WithSystemInstruction("Sos Viernes.");

        Assert.Equal("Sos Viernes.", copia.SystemInstruction);

        var olvidadas = typeof(GeminiLiveOptions)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(propiedad => propiedad.Name != nameof(GeminiLiveOptions.SystemInstruction))
            .Where(propiedad => !Equals(propiedad.GetValue(original), propiedad.GetValue(copia)))
            .Select(propiedad => propiedad.Name)
            .ToArray();

        Assert.Empty(olvidadas);
    }

    [Fact]
    public void ToString_NoTieneNadaSecretoQueMostrar()
    {
        var texto = new GeminiLiveOptions(enabled: true).ToString();

        Assert.Contains("Aoede", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("key", texto, StringComparison.OrdinalIgnoreCase);
    }
}
