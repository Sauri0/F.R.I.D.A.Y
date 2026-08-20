using System.Text.Json;
using Viernes.Core.Live;
using Viernes.Core.Tools;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Fija la forma del primer mensaje, que es el único que si sale mal no da un error entendible.
/// </summary>
/// <remarks>
/// El servidor rechaza un setup mal armado cerrando el websocket, sin cuerpo y sin decir qué campo
/// le molestó. Estas pruebas están para que ese diagnóstico no haya que hacerlo dos veces.
/// </remarks>
public sealed class LiveClientMessagesTests
{
    private static JsonElement Setup(
        GeminiLiveOptions options,
        string? handle = null,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildSetup(options, handle, tools));
        return document.RootElement.GetProperty("setup").Clone();
    }

    [Fact]
    public void Setup_UsaElModeloVerificadoConElPrefijoDeRecurso()
    {
        var setup = Setup(new GeminiLiveOptions());

        Assert.Equal("models/gemini-3.1-flash-live-preview", setup.GetProperty("model").GetString());
    }

    [Fact]
    public void Setup_PideAudioYLaVozQueEligioElUsuario()
    {
        var setup = Setup(new GeminiLiveOptions());
        var generation = setup.GetProperty("generationConfig");

        var modalities = generation.GetProperty("responseModalities").EnumerateArray().Select(m => m.GetString() ?? string.Empty).ToArray();
        Assert.Equal(["AUDIO"], modalities);

        Assert.Equal(
            "Aoede",
            generation
                .GetProperty("speechConfig")
                .GetProperty("voiceConfig")
                .GetProperty("prebuiltVoiceConfig")
                .GetProperty("voiceName")
                .GetString());
    }

    [Fact]
    public void Setup_DejaElDetectorDeVozDelServidorPrendidoYConElSilencioRecomendado()
    {
        var setup = Setup(new GeminiLiveOptions());
        var realtime = setup.GetProperty("realtimeInputConfig");

        var detection = realtime.GetProperty("automaticActivityDetection");

        // Mandar disabled=true apagaría lo único que permite interrumpirla.
        Assert.False(detection.TryGetProperty("disabled", out _));
        Assert.Equal(700, detection.GetProperty("silenceDurationMs").GetInt32());
        Assert.Equal("START_OF_ACTIVITY_INTERRUPTS", realtime.GetProperty("activityHandling").GetString());
    }

    [Fact]
    public void Setup_ConInterrupcionApagada_PideNoInterruption()
    {
        var setup = Setup(new GeminiLiveOptions(interruptOnUserSpeech: false));

        Assert.Equal(
            "NO_INTERRUPTION",
            setup.GetProperty("realtimeInputConfig").GetProperty("activityHandling").GetString());
    }

    [Fact]
    public void Setup_MandaLosEnterosDeSesentaYCuatroBitsComoCadena()
    {
        var setup = Setup(new GeminiLiveOptions());
        var compression = setup.GetProperty("contextWindowCompression");

        // Es la regla de proto3 para int64 y ningún serializador de C# la aplica solo. Si esto se
        // rompe, el setup entero se rechaza sin explicación.
        Assert.Equal(JsonValueKind.String, compression.GetProperty("triggerTokens").ValueKind);
        Assert.Equal("25600", compression.GetProperty("triggerTokens").GetString());

        var sliding = compression.GetProperty("slidingWindow");
        Assert.Equal(JsonValueKind.String, sliding.GetProperty("targetTokens").ValueKind);
        Assert.Equal("12800", sliding.GetProperty("targetTokens").GetString());
    }

    [Fact]
    public void Setup_SinHandle_PideReanudacionConObjetoVacio()
    {
        var setup = Setup(new GeminiLiveOptions());
        var resumption = setup.GetProperty("sessionResumption");

        // El objeto vacío significa «activada, primera vez». Omitir la propiedad significaría
        // «desactivada» y la sesión se moriría sola a los diez minutos.
        Assert.Equal(JsonValueKind.Object, resumption.ValueKind);
        Assert.False(resumption.TryGetProperty("handle", out _));
    }

    [Fact]
    public void Setup_ConHandle_LoMandaParaReanudarLaConversacion()
    {
        var setup = Setup(new GeminiLiveOptions(), "el-handle-de-la-sesion");

        Assert.Equal(
            "el-handle-de-la-sesion",
            setup.GetProperty("sessionResumption").GetProperty("handle").GetString());
    }

    [Fact]
    public void Setup_PideLasDosTranscripcionesPorDefecto()
    {
        var setup = Setup(new GeminiLiveOptions());

        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
    }

    [Fact]
    public void Setup_SinTranscripciones_NoLasPide()
    {
        var setup = Setup(new GeminiLiveOptions(transcribeInput: false, transcribeOutput: false));

        Assert.False(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.False(setup.TryGetProperty("outputAudioTranscription", out _));
    }

    [Fact]
    public void Setup_SinInstruccionDeSistema_NoMandaLaPropiedad()
    {
        var setup = Setup(new GeminiLiveOptions());

        Assert.False(setup.TryGetProperty("systemInstruction", out _));
    }

    [Fact]
    public void Setup_ConInstruccionDeSistema_LaMandaComoParteDeTexto()
    {
        var setup = Setup(new GeminiLiveOptions(systemInstruction: "Sos Viernes."));

        Assert.Equal(
            "Sos Viernes.",
            setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void AudioChunk_VaPorRealtimeInputYDeclaraDieciseisKilohertz()
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildAudioChunk([1, 2, 3, 4]));
        var audio = document.RootElement.GetProperty("realtimeInput").GetProperty("audio");

        // Por realtimeInput y no por clientContent: clientContent cierra el turno en cada envío.
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), audio.GetProperty("data").GetString());
    }

    [Fact]
    public void AudioChunk_NoDeclaraLaFrecuenciaDeSalida()
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildAudioChunk([1, 2]));

        var mime = document.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("mimeType").GetString();

        // Confundir 16 con 24 no rompe nada: hace que el servidor reconstruya mal lo que se le dijo.
        Assert.DoesNotContain("24000", mime, StringComparison.Ordinal);
    }

    [Fact]
    public void Texto_CierraElTurnoPorqueNoHayDetectorDeVozQueLoHaga()
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildText("hola"));
        var content = document.RootElement.GetProperty("clientContent");

        Assert.True(content.GetProperty("turnComplete").GetBoolean());
        Assert.Equal("user", content.GetProperty("turns")[0].GetProperty("role").GetString());
        Assert.Equal("hola", content.GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void FinDeAudio_EsUnMensajePropio()
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildAudioStreamEnd());

        Assert.True(document.RootElement.GetProperty("realtimeInput").GetProperty("audioStreamEnd").GetBoolean());
    }

    /// <summary>Una herramienta con un esquema como los que arma este proyecto de verdad.</summary>
    private static ToolDefinition Herramienta() => ToolDefinition.Create(
        "pc_action",
        "Controla Windows.",
        new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", description = "Qué hacer." },
                target = new { type = "string", description = "Sobre qué." }
            },
            required = new[] { "action" },
            // Es JSON Schema válido y el protocolo de esta API no lo tiene. Está acá a propósito.
            additionalProperties = false
        });

    [Fact]
    public void Setup_SinHerramientas_NoDeclaraNinguna()
    {
        // Mandar un arreglo vacío no es lo mismo que no mandar nada, y acá la sesión sin manos tiene
        // que seguir armando exactamente el mismo setup que armaba antes de que esto existiera.
        var setup = Setup(new GeminiLiveOptions());

        Assert.False(setup.TryGetProperty("tools", out _));
    }

    [Fact]
    public void Setup_DeclaraLasHerramientasConNombreDescripcionYEsquema()
    {
        var setup = Setup(new GeminiLiveOptions(), tools: [Herramienta()]);

        var declaration = setup
            .GetProperty("tools")[0]
            .GetProperty("functionDeclarations")[0];

        Assert.Equal("pc_action", declaration.GetProperty("name").GetString());
        Assert.Equal("Controla Windows.", declaration.GetProperty("description").GetString());

        var parameters = declaration.GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("action").GetProperty("type").GetString());
        Assert.Equal("Sobre qué.", parameters.GetProperty("properties").GetProperty("target").GetProperty("description").GetString());
        Assert.Equal(
            ["action"],
            parameters.GetProperty("required").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void Setup_TiraLosCamposDeEsquemaQueEsteProtocoloNoTiene()
    {
        // El esquema del protocolo es un subconjunto de JSON Schema. Un campo de más no da un error
        // de campo: rebota el setup entero, la sesión se cae al camino de siempre, y el usuario se
        // queda sin manos sin que nadie sepa por qué.
        var setup = Setup(new GeminiLiveOptions(), tools: [Herramienta()]);

        var parameters = setup
            .GetProperty("tools")[0]
            .GetProperty("functionDeclarations")[0]
            .GetProperty("parameters");

        Assert.False(parameters.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public void Setup_NoMandaUnaListaVaciaDeObligatorios()
    {
        var tool = ToolDefinition.Create(
            "listar",
            "No lleva argumentos.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() });

        var setup = Setup(new GeminiLiveOptions(), tools: [tool]);

        var parameters = setup
            .GetProperty("tools")[0]
            .GetProperty("functionDeclarations")[0]
            .GetProperty("parameters");

        Assert.False(parameters.TryGetProperty("required", out _));
    }

    [Fact]
    public void RespuestaDeHerramienta_LlevaElIdQueApareaConLaLlamada()
    {
        using var document = JsonDocument.Parse(LiveClientMessages.BuildToolResponse(
        [
            new LiveFunctionResponse("c1", "pc_action", new LiveToolOutcome("Succeeded", "Abrí Spotify.")),
            new LiveFunctionResponse("c2", "mision", LiveToolOutcome.Failed("No encontré esa misión."))
        ]));

        var responses = document.RootElement.GetProperty("toolResponse").GetProperty("functionResponses");
        Assert.Equal(2, responses.GetArrayLength());

        // Sin el id el servidor queda esperando una contestación que ya se mandó, y la charla se
        // cuelga sin error.
        Assert.Equal("c1", responses[0].GetProperty("id").GetString());
        Assert.Equal("pc_action", responses[0].GetProperty("name").GetString());
        Assert.Equal("Succeeded", responses[0].GetProperty("response").GetProperty("status").GetString());
        Assert.Equal("Abrí Spotify.", responses[0].GetProperty("response").GetProperty("message").GetString());

        // El estado viaja aparte del texto: es lo que le permite al modelo distinguir «lo hice» de
        // «no pude» sin interpretar una frase en castellano.
        Assert.Equal("Failed", responses[1].GetProperty("response").GetProperty("status").GetString());
    }
}
