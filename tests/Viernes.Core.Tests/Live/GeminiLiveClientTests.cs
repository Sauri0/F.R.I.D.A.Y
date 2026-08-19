using System.Diagnostics;
using System.Text.Json;
using Viernes.Core.Live;
using Viernes.Core.Tests.TestDoubles;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Prueba el cliente entero contra un servidor de mentira: sin red, sin micrófono y sin parlantes.
/// </summary>
/// <remarks>
/// Lo que se verifica acá es lo que no se puede verificar mirando el código: que cuando llega
/// <c>interrupted</c> la cola queda <b>vacía</b>, y que un <c>goAway</c> reconecta llevándose el
/// handle de la sesión.
/// </remarks>
public sealed class GeminiLiveClientTests
{
    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Va guardando cada conexión que abre el cliente, para poder mirar la segunda cuando reconecta.
    /// </summary>
    /// <remarks>
    /// Todo pasa por el candado porque el cliente reconecta desde su propio hilo de lectura mientras
    /// la prueba mira desde el suyo.
    /// </remarks>
    private sealed class Banco
    {
        private readonly Lock _gate = new();
        private readonly List<FakeLiveTransport> _transports = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _transports.Count;
                }
            }
        }

        public FakeLiveTransport Last
        {
            get
            {
                lock (_gate)
                {
                    return _transports[^1];
                }
            }
        }

        public ILiveTransport Create()
        {
            var transport = new FakeLiveTransport();
            lock (_gate)
            {
                _transports.Add(transport);
            }

            // El cliente espera el setupComplete antes de dejar mandar nada, así que el servidor de
            // mentira tiene que aceptar la sesión igual que el de verdad.
            transport.Deliver("""{"setupComplete":{}}""");
            return transport;
        }
    }

    private static async Task<bool> EsperarAsync(Func<bool> condicion)
    {
        var reloj = Stopwatch.StartNew();
        while (reloj.Elapsed < Paciencia)
        {
            if (condicion())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return condicion();
    }

    private static (GeminiLiveClient Client, Banco Banco, RecordingAudioSink Sink) Armar(GeminiLiveOptions? options = null)
    {
        var banco = new Banco();
        var sink = new RecordingAudioSink();
        var client = new GeminiLiveClient(
            options ?? new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            sink,
            banco.Create);

        return (client, banco, sink);
    }

    private static string AudioMessage(int bytes)
    {
        var data = Convert.ToBase64String(new byte[bytes]);
        return """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"PCM"}}]}}}"""
            .Replace("PCM", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apagada_NoConectaYLoDice()
    {
        var (client, banco, _) = Armar(new GeminiLiveOptions(enabled: false));
        await using var _guard = client;

        Assert.False(await client.StartAsync());
        Assert.Equal(0, banco.Count);
        Assert.Equal("La sesión en vivo está apagada por configuración.", client.LastFailure);
    }

    [Fact]
    public async Task SinClave_NoConectaYLoDice()
    {
        var banco = new Banco();
        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => null,
            new RecordingAudioSink(),
            banco.Create);

        Assert.False(await client.StartAsync());
        Assert.Equal("No hay clave de Google configurada.", client.LastFailure);
    }

    [Fact]
    public async Task AlArrancar_MandaElSetupYEsperaLaConfirmacion()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;

        Assert.True(await client.StartAsync());
        Assert.True(client.IsConnected);

        var enviado = banco.Last.SentSnapshot();
        Assert.Single(enviado);
        using var document = JsonDocument.Parse(enviado[0]);
        Assert.True(document.RootElement.TryGetProperty("setup", out _));
    }

    [Fact]
    public async Task LaInstruccionSeRefrescaAntesDeCadaConexion()
    {
        // La sesión hablada nacía con una instrucción fija y era amnésica: el camino de siempre le
        // arma al modelo la memoria personal y las misiones abiertas con su pregunta sin contestar,
        // y el camino nuevo no le armaba nada. Justo el camino elegido como principal era el único
        // donde no sabía quién era el usuario.
        //
        // Que se pueda refrescar es lo que lo arregla, y tiene que valer para la SEGUNDA charla
        // también: una misión creada entre una y otra no serviría de nada si la instrucción quedara
        // congelada en la primera.
        var (client, banco, _) = Armar();
        await using var _guard = client;

        // Sin acentos a propósito: el setup viaja como JSON y ahí los no-ASCII salen escapados, así
        // que buscar «Sabés» en el texto crudo falla por la codificación y no por el comportamiento.
        client.UseSystemInstruction("Se llama Ana.");
        Assert.True(await client.StartAsync());
        Assert.Contains("Se llama Ana.", banco.Last.SentSnapshot()[0], StringComparison.Ordinal);

        await client.StopAsync();

        client.UseSystemInstruction("Le preguntaste por la factura.");
        Assert.True(await client.StartAsync());

        var segunda = banco.Last.SentSnapshot()[0];
        Assert.Contains("Le preguntaste por la factura.", segunda, StringComparison.Ordinal);
        Assert.DoesNotContain("Se llama Ana", segunda, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiElServidorNoConfirma_NoDaLaSesionPorAbierta()
    {
        // Esta fábrica no manda setupComplete: es el caso en que el servidor acepta el socket y
        // después rechaza el setup callado, sin cuerpo y sin decir qué campo le molestó.
        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true, setupTimeout: TimeSpan.FromMilliseconds(150)),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            () => new FakeLiveTransport());

        Assert.False(await client.StartAsync());
        Assert.False(client.IsConnected);
        Assert.Equal("El servidor no confirmó la sesión a tiempo.", client.LastFailure);
    }

    [Fact]
    public async Task ElAudioDeLaRespuesta_LlegaALosParlantes()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(480));

        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 480));
        Assert.Equal(LiveTurnState.Responding, client.TurnState);
    }

    [Fact]
    public async Task CuandoLaInterrumpen_LaColaQuedaVacia()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        var avisos = 0;
        client.Interrupted += (_, _) => Interlocked.Increment(ref avisos);

        // Varios segundos de respuesta ya bufferados de este lado: es la situación exacta del bug.
        banco.Last.Deliver(AudioMessage(24_000 * 2 * 3));
        Assert.True(await EsperarAsync(() => sink.QueuedBytes > 0));

        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");

        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 0));
        Assert.True(await EsperarAsync(() => Volatile.Read(ref avisos) == 1));
        Assert.Equal(1, sink.FlushCount);
        Assert.Equal(LiveTurnState.Interrupted, client.TurnState);
        Assert.Equal(1, client.InterruptionCount);
    }

    [Fact]
    public async Task DespuesDeInterrumpirla_ElAudioRezagadoNoVuelveAEncolarse()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(1_000));
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 1_000));

        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 0));

        // Lo que el servidor ya había despachado sigue viajando por el cable.
        banco.Last.Deliver(AudioMessage(1_000));
        await Task.Delay(150);

        Assert.Equal(0, sink.QueuedBytes);
    }

    [Fact]
    public async Task UnTurnoInterrumpido_CierraConTurnCompleteSinGenerado()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(400));
        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");
        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");

        Assert.True(await EsperarAsync(() => sink.CompletedTurns == 1));
        Assert.Equal(LiveTurnState.Idle, client.TurnState);
    }

    [Fact]
    public async Task SilenceNow_CortaLaVozSinEsperarAlServidor()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(5_000));
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 5_000));

        // Probando contra el servidor de verdad, el interrupted no siempre llega. El anfitrión tiene
        // su propio detector de voz y puede callarla igual.
        client.SilenceNow();

        Assert.Equal(0, sink.QueuedBytes);
        Assert.Equal(1, sink.FlushCount);
    }

    [Fact]
    public async Task LasTranscripciones_LleganSeparadasPorQuienHablo()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        var dichos = new List<(LiveSpeaker Speaker, string Text)>();
        client.TranscriptReceived += (_, e) =>
        {
            lock (dichos)
            {
                dichos.Add((e.Speaker, e.Text));
            }
        };

        banco.Last.Deliver("""{"serverContent":{"inputTranscription":{"text":"poné música"}}}""");
        banco.Last.Deliver("""{"serverContent":{"outputTranscription":{"text":"dale"}}}""");

        Assert.True(await EsperarAsync(() =>
        {
            lock (dichos)
            {
                return dichos.Count == 2;
            }
        }));

        Assert.Equal((LiveSpeaker.User, "poné música"), dichos[0]);
        Assert.Equal((LiveSpeaker.Assistant, "dale"), dichos[1]);
    }

    [Fact]
    public async Task ConGoAway_ReconectaLlevandoseElHandleDeLaSesion()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        var primero = banco.Last;
        primero.Deliver("""{"sessionResumptionUpdate":{"newHandle":"handle-de-la-charla","resumable":true}}""");
        primero.Deliver("""{"goAway":{"timeLeft":"10s"}}""");

        // La conexión nueva aparece en el banco antes de mandar el setup, así que se espera el
        // mensaje y no la conexión.
        Assert.True(await EsperarAsync(() => banco.Count == 2 && banco.Last.SentSnapshot().Count == 1));

        var setup = banco.Last.SentSnapshot()[0];
        using var document = JsonDocument.Parse(setup);
        Assert.Equal(
            "handle-de-la-charla",
            document.RootElement.GetProperty("setup").GetProperty("sessionResumption").GetProperty("handle").GetString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ElAvisoDeCierreNoCortaUnaFraseAMitadDeCamino()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(400));
        Assert.True(await EsperarAsync(() => client.TurnState == LiveTurnState.Responding));

        banco.Last.Deliver("""{"goAway":{"timeLeft":"20s"}}""");
        await Task.Delay(150);

        // Cortar para reconectar se oye peor que usar el margen que el propio servidor está dando.
        Assert.Equal(1, banco.Count);

        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(await EsperarAsync(() => banco.Count == 2));
    }

    [Fact]
    public async Task SiSeCortaLaConexion_VaciaLaColaYReconecta()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(2_000));
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 2_000));

        banco.Last.DeliverClose();

        // Lo encolado pertenece a un turno que del otro lado ya no existe.
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 0));
        Assert.True(await EsperarAsync(() => banco.Count == 2));
    }

    [Fact]
    public async Task ElAudioDelMicrofono_SeParteEnFragmentosDeVeinteMilisegundos()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        // 100 ms de audio a 16 kHz: tienen que salir cinco mensajes, no uno.
        var cienMilisegundos = new byte[LiveAudioFormat.InputBytesForMilliseconds(100)];
        Assert.True(await client.SendAudioAsync(cienMilisegundos));

        var enviado = banco.Last.SentSnapshot();
        Assert.Equal(6, enviado.Count);

        foreach (var mensaje in enviado.Skip(1))
        {
            using var document = JsonDocument.Parse(mensaje);
            var data = document.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString();
            Assert.Equal(LiveAudioFormat.InputBytesForMilliseconds(20), Convert.FromBase64String(data!).Length);
        }
    }

    [Fact]
    public async Task ElRestoQueNoLlenaUnFragmento_SeGuardaParaLaProximaVez()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        var mitad = new byte[LiveAudioFormat.InputBytesForMilliseconds(10)];

        Assert.False(await client.SendAudioAsync(mitad));
        Assert.Single(banco.Last.SentSnapshot());

        Assert.True(await client.SendAudioAsync(mitad));
        Assert.Equal(2, banco.Last.SentSnapshot().Count);
    }

    [Fact]
    public async Task UnBloqueDeBytesImpares_NoDesalineaLoQueSigue()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        // El dispositivo de captura entrega lo que quiere. El servidor lee de a dos bytes y no
        // tiene forma de darse cuenta de que arrancó corrido: no falla, suena a ruido.
        await client.SendAudioAsync(new byte[LiveAudioFormat.InputBytesForMilliseconds(20) + 1]);
        await client.SendAudioAsync(new byte[LiveAudioFormat.InputBytesForMilliseconds(20) - 1]);

        foreach (var mensaje in banco.Last.SentSnapshot().Skip(1))
        {
            using var document = JsonDocument.Parse(mensaje);
            var data = document.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString();
            Assert.Equal(0, Convert.FromBase64String(data!).Length % LiveAudioFormat.BytesPerSample);
        }
    }

    [Fact]
    public async Task SinConexion_NoMandaAudioNiLoAcumulaParaDespues()
    {
        var (client, _, _) = Armar();
        await using var _guard = client;

        // Audio del micrófono de hace tres segundos, mandado ahora, es peor que no mandar nada.
        Assert.False(await client.SendAudioAsync(new byte[640]));
    }

    [Fact]
    public async Task ElCosto_SeMideEnMinutosDeAudioYNoEnTokens()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        await client.SendAudioAsync(new byte[LiveAudioFormat.InputBytesForMilliseconds(1_000)]);
        banco.Last.Deliver(AudioMessage(LiveAudioFormat.OutputSampleRate * LiveAudioFormat.BytesPerSample));

        Assert.True(await EsperarAsync(() => client.Cost.OutputAudio.TotalSeconds >= 1));
        Assert.Equal(1, client.Cost.InputAudio.TotalSeconds, 2);
        Assert.Equal(1, client.Cost.OutputAudio.TotalSeconds, 2);

        var esperado = (LiveCostMeter.InputUsdPerMinute + LiveCostMeter.OutputUsdPerMinute) / 60m;
        Assert.Equal((double)esperado, (double)client.Cost.EstimatedUsd, 6);
    }

    [Fact]
    public async Task UnSuscriptorQueFalla_NoMataElBucleDeLectura()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        client.TranscriptReceived += (_, _) => throw new InvalidOperationException("la interfaz explotó");

        banco.Last.Deliver("""{"serverContent":{"outputTranscription":{"text":"dale"}}}""");
        banco.Last.Deliver(AudioMessage(320));

        // Una sesión abierta y muda es la peor forma de fallar: no se parece a un error.
        Assert.True(await EsperarAsync(() => sink.TotalEnqueuedBytes == 320));
    }

    [Fact]
    public async Task AlDetenerla_CierraElTransporteYVaciaLaCola()
    {
        var (client, banco, sink) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        banco.Last.Deliver(AudioMessage(800));
        Assert.True(await EsperarAsync(() => sink.QueuedBytes == 800));

        await client.StopAsync();

        Assert.False(client.IsConnected);
        Assert.Equal(0, sink.QueuedBytes);
        Assert.True(banco.Last.WasClosed);
    }

    [Fact]
    public async Task LaDireccionLlevaLaClaveYLaVersionSinClaveNoLaTiene()
    {
        var (client, banco, _) = Armar();
        await using var _guard = client;
        await client.StartAsync();

        Assert.Contains("key=clave-de-mentira", banco.Last.Endpoint!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("key=", LiveEndpoint.Redacted, StringComparison.Ordinal);
    }
}
