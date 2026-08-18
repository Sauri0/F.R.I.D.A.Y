using System.Globalization;
using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Prueba la lectura de lo que manda el servidor, con los mensajes tal como llegan.
/// </summary>
public sealed class LiveServerEventParserTests
{
    [Fact]
    public void SetupComplete_SeReconoce()
    {
        var serverEvent = LiveServerEventParser.Parse("""{"setupComplete":{}}""");

        Assert.True(serverEvent.SetupComplete);
        Assert.False(serverEvent.IsEmpty);
    }

    [Fact]
    public void Interrupted_SeLee()
    {
        var serverEvent = LiveServerEventParser.Parse("""{"serverContent":{"interrupted":true}}""");

        Assert.True(serverEvent.Interrupted);
        Assert.False(serverEvent.TurnComplete);
        Assert.False(serverEvent.GenerationComplete);
    }

    [Fact]
    public void SinInterrupted_NoSeInventa()
    {
        var serverEvent = LiveServerEventParser.Parse("""{"serverContent":{"turnComplete":true}}""");

        Assert.False(serverEvent.Interrupted);
        Assert.True(serverEvent.TurnComplete);
    }

    [Fact]
    public void InterruptedEnFalse_NoCuentaComoInterrupcion()
    {
        // Vaciar la cola por un false sería peor que no leerlo: la corta sin que nadie hable.
        var serverEvent = LiveServerEventParser.Parse("""{"serverContent":{"interrupted":false}}""");

        Assert.False(serverEvent.Interrupted);
    }

    [Fact]
    public void GenerationComplete_YTurnComplete_SonCosasDistintas()
    {
        var generado = LiveServerEventParser.Parse("""{"serverContent":{"generationComplete":true}}""");
        var cerrado = LiveServerEventParser.Parse("""{"serverContent":{"turnComplete":true}}""");

        Assert.True(generado.GenerationComplete);
        Assert.False(generado.TurnComplete);
        Assert.False(cerrado.GenerationComplete);
        Assert.True(cerrado.TurnComplete);
    }

    [Fact]
    public void Audio_SeDecodificaDeInlineData()
    {
        var data = Convert.ToBase64String([9, 8, 7, 6]);
        var serverEvent = LiveServerEventParser.Parse(
            """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"UNO"}}]}}}"""
                .Replace("UNO", data, StringComparison.Ordinal));

        Assert.Single(serverEvent.Audio);
        Assert.Equal([9, 8, 7, 6], serverEvent.Audio[0]);
        Assert.Equal(4, serverEvent.AudioByteCount);
    }

    [Fact]
    public void Audio_TomaTodasLasPartesYNoSoloLaPrimera()
    {
        var json = """
            {"serverContent":{"modelTurn":{"parts":[
              {"text":"hola"},
              {"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"UNO"}},
              {"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"DOS"}}
            ]}}}
            """
            .Replace("UNO", Convert.ToBase64String([1, 1]), StringComparison.Ordinal)
            .Replace("DOS", Convert.ToBase64String([2, 2]), StringComparison.Ordinal);

        var serverEvent = LiveServerEventParser.Parse(json);

        // Quedarse con la primera parte deja huecos en la voz en medio de una frase.
        Assert.Equal(2, serverEvent.Audio.Count);
        Assert.Equal("hola", serverEvent.Text);
    }

    [Fact]
    public void Transcripciones_SeSeparanPorQuienHablo()
    {
        var serverEvent = LiveServerEventParser.Parse(
            """{"serverContent":{"inputTranscription":{"text":"poné música"},"outputTranscription":{"text":"dale"}}}""");

        Assert.Equal("poné música", serverEvent.InputTranscript);
        Assert.Equal("dale", serverEvent.OutputTranscript);
    }

    [Fact]
    public void Reanudacion_GuardaElHandleYSiSirve()
    {
        var serverEvent = LiveServerEventParser.Parse(
            """{"sessionResumptionUpdate":{"newHandle":"abc123","resumable":true}}""");

        Assert.Equal("abc123", serverEvent.ResumptionHandle);
        Assert.True(serverEvent.ResumptionHandleIsResumable);
    }

    [Theory]
    [InlineData("""{"goAway":{"timeLeft":"10s"}}""", 10)]
    [InlineData("""{"goAway":{"timeLeft":"0.5s"}}""", 0.5)]
    [InlineData("""{"goAway":{"timeLeft":"9.500s"}}""", 9.5)]
    public void GoAway_LeeLaDuracionDeProtobuf(string json, double expectedSeconds)
    {
        var serverEvent = LiveServerEventParser.Parse(json);

        Assert.NotNull(serverEvent.GoAwayTimeLeft);
        Assert.Equal(expectedSeconds, serverEvent.GoAwayTimeLeft!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void GoAway_LaDuracionSeLeeConPuntoAunqueLaMaquinaUseComa()
    {
        var anterior = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new CultureInfo("es-AR");
        try
        {
            var serverEvent = LiveServerEventParser.Parse("""{"goAway":{"timeLeft":"9.5s"}}""");

            // Con la cultura del sistema esto daría noventa y cinco segundos de margen donde hay
            // nueve y medio, y la sesión se cortaría sola sin que nadie entienda por qué.
            Assert.Equal(9.5, serverEvent.GoAwayTimeLeft!.Value.TotalSeconds, 3);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = anterior;
        }
    }

    [Fact]
    public void GoAway_SinTiempo_SigueSiendoUnAvisoDeCierre()
    {
        var serverEvent = LiveServerEventParser.Parse("""{"goAway":{}}""");

        Assert.Equal(TimeSpan.Zero, serverEvent.GoAwayTimeLeft);
    }

    [Fact]
    public void Uso_SeLeeCuandoLlega()
    {
        var serverEvent = LiveServerEventParser.Parse(
            """{"usageMetadata":{"promptTokenCount":12,"responseTokenCount":34,"totalTokenCount":46}}""");

        Assert.Equal(new LiveTokenUsage(12, 34, 46), serverEvent.Usage);
    }

    [Fact]
    public void UnMensajeConCamposDesconocidos_NoRompeYSigueLeyendoLoQueImporta()
    {
        // El modelo es preview y el servidor agrega campos. Romperse acá sería perder el
        // interrupted que venía en el mismo mensaje.
        var serverEvent = LiveServerEventParser.Parse(
            """{"serverContent":{"interrupted":true,"campoQueNoExistiaAyer":{"x":1}},"otraCosa":[1,2]}""");

        Assert.True(serverEvent.Interrupted);
    }

    [Fact]
    public void JsonRoto_VuelveComoErrorYNoComoExcepcion()
    {
        var serverEvent = LiveServerEventParser.Parse("{ esto no es json");

        Assert.NotNull(serverEvent.Error);
        Assert.False(serverEvent.Interrupted);
    }

    [Fact]
    public void ErrorDelServidor_SeResumeSinVolcarElCuerpo()
    {
        var serverEvent = LiveServerEventParser.Parse(
            """{"error":{"code":400,"message":"Invalid setup","details":[{"clave":"no deberia viajar"}]}}""");

        Assert.Equal("Invalid setup (código 400)", serverEvent.Error);
        Assert.DoesNotContain("no deberia viajar", serverEvent.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MensajeVacio_SeReconoceComoVacio()
    {
        Assert.True(LiveServerEventParser.Parse("{}").IsEmpty);
        Assert.True(LiveServerEventParser.Parse("   ").IsEmpty);
    }
}
