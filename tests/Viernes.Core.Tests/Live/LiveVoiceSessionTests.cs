using System.Diagnostics;
using Viernes.Core.Live;
using Viernes.Core.Tests.TestDoubles;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// El enchufe entero contra un servidor de mentira: elección de camino, momentos del orbe, el corte
/// por interrupción y la caída al camino de siempre.
/// </summary>
/// <remarks>
/// Ninguna de estas pruebas toca la red. La que más importa —que al interrumpirla la cola quede
/// vacía <em>y</em> el orbe se entere— no se podría verificar contra Google ni pagando el turno:
/// habría que hablarle encima en el momento justo y confiar en que el servidor haga lo mismo la
/// próxima vez.
/// </remarks>
public sealed class LiveVoiceSessionTests
{
    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(5);

    /// <summary>Guarda cada conexión que abre el cliente, y opcionalmente las hace fallar.</summary>
    private sealed class Banco(bool aceptar = true)
    {
        private readonly Lock _gate = new();
        private readonly List<FakeLiveTransport> _transports = [];

        /// <summary>Deja de aceptar a partir de la próxima. Para simular que el servicio se cayó.</summary>
        public bool RechazarDeAcaEnMas { get; set; }

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

            if (aceptar && !RechazarDeAcaEnMas)
            {
                transport.Deliver("""{"setupComplete":{}}""");
            }
            else
            {
                // El servidor acepta el socket y después cierra sin confirmar. Es el caso real de
                // una clave rechazada: no llega ningún cuerpo que diga qué pasó.
                transport.DeliverClose();
            }

            return transport;
        }
    }

    private static async Task<bool> EsperarAsync(Func<bool> condicion, TimeSpan? paciencia = null)
    {
        var limite = paciencia ?? Paciencia;
        var reloj = Stopwatch.StartNew();
        while (reloj.Elapsed < limite)
        {
            if (condicion())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return condicion();
    }

    private static string AudioMessage(int bytes)
    {
        var data = Convert.ToBase64String(new byte[bytes]);
        return """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"PCM"}}]}}}"""
            .Replace("PCM", data, StringComparison.Ordinal);
    }

    private static (LiveVoiceSession Sesion, Banco Banco, RecordingAudioSink Parlantes) Armar(
        bool enabled = true,
        string? clave = "clave-de-mentira",
        bool aceptar = true,
        LiveFallbackLatch? traba = null,
        TimeSpan? abandono = null)
    {
        var banco = new Banco(aceptar);
        var parlantes = new RecordingAudioSink();
        var sesion = new LiveVoiceSession(
            new GeminiLiveOptions(enabled: enabled, setupTimeout: TimeSpan.FromMilliseconds(300)),
            () => clave,
            parlantes,
            banco.Create,
            traba,
            abandono);

        return (sesion, banco, parlantes);
    }

    /// <summary>
    /// Lo mismo, pero con unos parlantes que tardan en sonar, que es lo que hace un parlante.
    /// </summary>
    private static (LiveVoiceSession Sesion, Banco Banco, DrainingAudioSink Parlantes) ArmarConParlanteLento()
    {
        var banco = new Banco();
        var parlantes = new DrainingAudioSink();
        var sesion = new LiveVoiceSession(
            new GeminiLiveOptions(enabled: true, setupTimeout: TimeSpan.FromMilliseconds(300)),
            () => "clave-de-mentira",
            parlantes,
            banco.Create);

        return (sesion, banco, parlantes);
    }

    [Fact]
    public async Task ConClaveYEncendida_AbreLaSesionYVaPorElCaminoNuevo()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;

        var decision = await sesion.StartAsync();

        Assert.True(decision.IsLive);
        Assert.True(sesion.IsConnected);
        Assert.Equal(1, banco.Count);
        Assert.Equal(LiveOrbMoment.Listening, sesion.Moment);
    }

    [Fact]
    public async Task Apagada_NiSiquieraAbreElSocket()
    {
        var (sesion, banco, _) = Armar(enabled: false);
        await using var _guard = sesion;

        var decision = await sesion.StartAsync();

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.Equal(VoiceRouter.DisabledReason, decision.Reason);
        Assert.Equal(0, banco.Count);
    }

    [Fact]
    public async Task SinClave_NiSiquieraAbreElSocket()
    {
        var (sesion, banco, _) = Armar(clave: null);
        await using var _guard = sesion;

        var decision = await sesion.StartAsync();

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.Equal(VoiceRouter.MissingKeyReason, decision.Reason);
        Assert.Equal(0, banco.Count);
    }

    [Fact]
    public async Task DecidirNoAbreNada()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;

        Assert.True(sesion.Decide().IsLive);
        Assert.Equal(0, banco.Count);
    }

    [Fact]
    public async Task SiElServidorNoAcepta_CaeAlCaminoDeSiempreConElMotivo()
    {
        var (sesion, _, _) = Armar(aceptar: false);
        await using var _guard = sesion;

        var decision = await sesion.StartAsync();

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.False(sesion.IsConnected);
    }

    [Fact]
    public async Task SiElServidorNoAcepta_QuedaTrabadoYLaProximaNiLoIntenta()
    {
        // Esto es lo que separa «se cae al camino de siempre» de «se cae al camino de siempre y
        // además no cuesta cinco segundos por conversación durante todo el día».
        var (sesion, banco, _) = Armar(aceptar: false);
        await using var _guard = sesion;

        await sesion.StartAsync();
        var intentosDespuesDeLaPrimera = banco.Count;

        var segunda = await sesion.StartAsync();

        Assert.Equal(VoiceRoute.Classic, segunda.Route);
        Assert.Equal(intentosDespuesDeLaPrimera, banco.Count);
        Assert.NotNull(sesion.BlockedReason);
    }

    [Fact]
    public async Task CuandoLaSesionAbre_SeBorraLaTrabaAnterior()
    {
        var traba = new LiveFallbackLatch();
        traba.Trip("una caída vieja");

        var (sesion, _, _) = Armar(traba: traba);
        await using var _guard = sesion;

        // Con la traba puesta ni lo intenta.
        Assert.Equal(VoiceRoute.Classic, (await sesion.StartAsync()).Route);

        traba.Reset();
        Assert.True((await sesion.StartAsync()).IsLive);
        Assert.Equal(0, traba.ConsecutiveTrips);
    }

    [Fact]
    public async Task ElAudioDeLaRespuesta_PoneElOrbeEnHablando()
    {
        var (sesion, banco, parlantes) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(480));

        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));
        Assert.Equal(480, parlantes.QueuedBytes);
    }

    [Fact]
    public async Task AlCerrarElTurno_ElOrbeVuelveATeEscucho()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(480));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");

        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));
    }

    [Fact]
    public async Task CuandoLaInterrumpen_LaColaQuedaVaciaYElOrbeLoDice()
    {
        var (sesion, banco, parlantes) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        var momentos = new List<LiveOrbMoment>();
        sesion.MomentChanged += (_, e) =>
        {
            lock (momentos)
            {
                momentos.Add(e.Current);
            }
        };

        // Tres segundos de respuesta ya bufferados de este lado: la situación exacta del bug.
        banco.Last.Deliver(AudioMessage(24_000 * 2 * 3));
        Assert.True(await EsperarAsync(() => parlantes.QueuedBytes > 0));

        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");

        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Interrupted));
        Assert.Equal(0, parlantes.QueuedBytes);
        Assert.Equal(1, parlantes.FlushCount);
        Assert.Equal(1, sesion.InterruptionCount);

        lock (momentos)
        {
            Assert.Equal([LiveOrbMoment.Speaking, LiveOrbMoment.Interrupted], momentos);
        }
    }

    [Fact]
    public async Task DespuesDeInterrumpirla_ElTurnoNuevoArrancaLimpio()
    {
        var (sesion, banco, parlantes) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(4_800));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));
        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Interrupted));

        // El turno interrumpido cierra sin generationComplete: es el orden real del protocolo.
        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));

        banco.Last.Deliver(AudioMessage(960));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));
        Assert.True(await EsperarAsync(() => parlantes.QueuedBytes == 960));
    }

    [Fact]
    public async Task AlCerrarLaFrase_ElOrbePasaAPensando()
    {
        var (sesion, _, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);
        Assert.Equal(LiveOrbMoment.Listening, sesion.Moment);

        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Thinking, sesion.Moment);
    }

    [Fact]
    public async Task ElTurnoCompleto_RecorreTeEscuchoPensandoHablandoYVuelve()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        var momentos = new List<LiveOrbMoment>();
        sesion.MomentChanged += (_, e) =>
        {
            lock (momentos)
            {
                momentos.Add(e.Current);
            }
        };

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Thinking, sesion.Moment);

        banco.Last.Deliver(AudioMessage(960));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        banco.Last.Deliver("""{"serverContent":{"generationComplete":true}}""");
        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");

        // Lo que importa acá: al cerrar el turno la espera se borra. Si quedara puesta, el orbe se
        // quedaría en «pensando» para siempre después de la primera respuesta.
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));

        lock (momentos)
        {
            Assert.Equal(
                [LiveOrbMoment.Thinking, LiveOrbMoment.Speaking, LiveOrbMoment.Listening],
                momentos);
        }
    }

    [Fact]
    public async Task AlCerrarLaSesion_ElOrbeVuelveAlPrincipio()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(960));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        await sesion.StopAsync();

        Assert.Equal(LiveOrbMoment.Listening, sesion.Moment);
        Assert.False(sesion.IsUserSpeaking);
        Assert.False(sesion.IsConnected);
    }

    [Fact]
    public async Task SiVuelveAHablarAntesDeQueConteste_ElOrbeVuelveATeEscucho()
    {
        var (sesion, _, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Thinking, sesion.Moment);

        sesion.NoteUserAudio(isVoice: true, bloque);
        Assert.Equal(LiveOrbMoment.Listening, sesion.Moment);
    }

    [Fact]
    public async Task ElEcoDeSuPropiaVozNoDibujaPensandoEncimaDeHablando()
    {
        // El micrófono queda abierto mientras ella habla: lo que sale por los parlantes vuelve por
        // el micrófono y el detector lo ve como voz. Si ese borde contara, el orbe diría «pensando»
        // en el medio de una respuesta que está sonando.
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(4_800));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Speaking, sesion.Moment);
    }

    [Fact]
    public async Task UnMinutoSinQueNadieHable_AvisaQueLaCharlaQuedoAbandonada()
    {
        // Una sesión abierta manda audio del micrófono a la nube sin parar y se cobra por minuto.
        // Sin este aviso, alguien que se levanta de la silla deja el micrófono transmitiendo hasta
        // que apague la máquina.
        var (sesion, _, _) = Armar(abandono: TimeSpan.FromMilliseconds(200));
        await using var _guard = sesion;
        await sesion.StartAsync();

        var avisos = 0;
        sesion.WentQuiet += (_, _) => Interlocked.Increment(ref avisos);

        var bloque = TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < 30; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        // Una sola vez por tramo de silencio: si avisara en cada bloque, el anfitrión cerraría la
        // charla cincuenta veces por segundo.
        Assert.Equal(1, Volatile.Read(ref avisos));
    }

    [Fact]
    public async Task SiVuelveAHablar_ElContadorDeAbandonoArrancaDeCero()
    {
        var (sesion, _, _) = Armar(abandono: TimeSpan.FromMilliseconds(200));
        await using var _guard = sesion;
        await sesion.StartAsync();

        var avisos = 0;
        sesion.WentQuiet += (_, _) => Interlocked.Increment(ref avisos);

        var bloque = TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < 9; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(0, Volatile.Read(ref avisos));

        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 9; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(0, Volatile.Read(ref avisos));
    }

    [Fact]
    public async Task MientrasElServidorContesta_NadieEstaAbandonandoNada()
    {
        // El silencio del usuario mientras ella habla es escuchar, no irse. Contarlo cerraría la
        // charla en el medio de una respuesta larga.
        var (sesion, banco, _) = Armar(abandono: TimeSpan.FromMilliseconds(200));
        await using var _guard = sesion;
        await sesion.StartAsync();

        var avisos = 0;
        sesion.WentQuiet += (_, _) => Interlocked.Increment(ref avisos);

        banco.Last.Deliver(AudioMessage(960));
        Assert.True(await EsperarAsync(() => sesion.TurnState == LiveTurnState.Responding));

        var bloque = TimeSpan.FromMilliseconds(20);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(0, Volatile.Read(ref avisos));
    }

    [Fact]
    public async Task LaTranscripcionSeReenvia()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        var dichos = new List<(LiveSpeaker Quien, string Texto)>();
        sesion.TranscriptReceived += (_, e) =>
        {
            lock (dichos)
            {
                dichos.Add((e.Speaker, e.Text));
            }
        };

        banco.Last.Deliver("""{"serverContent":{"inputTranscription":{"text":"hola"}}}""");

        Assert.True(await EsperarAsync(() =>
        {
            lock (dichos)
            {
                return dichos.Count == 1;
            }
        }));

        lock (dichos)
        {
            Assert.Equal(LiveSpeaker.User, dichos[0].Quien);
            Assert.Equal("hola", dichos[0].Texto);
        }
    }

    [Fact]
    public async Task SiLaSesionSeMuere_AvisaQueHayQueVolverAlCaminoDeSiempre()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        LiveFailureEventArgs? caida = null;
        sesion.FellBack += (_, e) => Volatile.Write(ref caida, e);

        // Se cae el servicio: ninguna reconexión vuelve a aceptar. Mientras la reconexión falla, el
        // cliente sigue leyendo del caño viejo, así que los cierres se encolan ahí —uno por intento—
        // hasta que se le acaben los reintentos. Reintenta con espera creciente, y por eso esta
        // prueba necesita más paciencia que las demás.
        var caño = banco.Last;
        banco.RechazarDeAcaEnMas = true;
        for (var i = 0; i < 8; i++)
        {
            caño.DeliverClose();
        }

        Assert.True(await EsperarAsync(
            () => Volatile.Read(ref caida) is not null,
            TimeSpan.FromSeconds(20)));
        Assert.True(Volatile.Read(ref caida)!.Fatal);
        Assert.NotNull(sesion.BlockedReason);
    }

    [Fact]
    public async Task ElFrenoDelUsuario_CallaLosParlantesYSeVe()
    {
        var (sesion, banco, parlantes) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(24_000 * 2));
        Assert.True(await EsperarAsync(() => parlantes.QueuedBytes > 0));

        sesion.SilenceNow();

        Assert.Equal(0, parlantes.QueuedBytes);
        Assert.Equal(LiveOrbMoment.Interrupted, sesion.Moment);
    }

    [Fact]
    public async Task AlCerrar_SeDesuscribeYNoAvisaMasNada()
    {
        var (sesion, banco, _) = Armar();
        await sesion.StartAsync();

        var avisos = 0;
        sesion.MomentChanged += (_, _) => Interlocked.Increment(ref avisos);

        await sesion.DisposeAsync();
        banco.Last.Deliver(AudioMessage(480));
        await Task.Delay(80);

        Assert.Equal(0, Volatile.Read(ref avisos));
    }

    [Fact]
    public async Task ElAudioDelMicrofonoLlegaAlServidor()
    {
        var (sesion, banco, _) = Armar();
        await using var _guard = sesion;
        await sesion.StartAsync();

        // Un fragmento entero de los que manda la sesión: 20 ms a 16 kHz mono de 16 bits.
        var bloque = new byte[LiveAudioFormat.InputBytesForMilliseconds(20)];

        Assert.True(await sesion.PushMicrophoneAsync(bloque));
        Assert.True(await EsperarAsync(() => banco.Last.SentSnapshot().Count > 1));
    }

    [Fact]
    public async Task ConElParlanteTodaviaSonando_CerrarElTurnoNoDibujaTeEscucho()
    {
        // El servidor manda la respuesta más rápido que tiempo real: cuando llega el turnComplete
        // pueden quedar segundos de voz sin salir. Durante todo ese tramo la pantalla decía «te
        // escucho» mientras en el cuarto se la seguía oyendo.
        var (sesion, banco, parlantes) = ArmarConParlanteLento();
        await using var _guard = sesion;
        await sesion.StartAsync();

        // Tres segundos de respuesta encolados.
        banco.Last.Deliver(AudioMessage(24_000 * 2 * 3));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        banco.Last.Deliver("""{"serverContent":{"generationComplete":true}}""");
        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");

        Assert.True(await EsperarAsync(() => sesion.TurnState == LiveTurnState.Idle));
        Assert.True(sesion.IsSpeakerBusy);
        Assert.Equal(LiveOrbMoment.Speaking, sesion.Moment);

        // Y cuando termina de sonar de verdad, ahí sí, sin que llegue ningún mensaje más.
        parlantes.Drain();

        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));
    }

    [Fact]
    public async Task LaColaDelEcoDespuesDelTurno_NoDejaElOrbeClavadoEnPensando()
    {
        // El caso que ninguna prueba cubría: el eco DURANTE el turno ya estaba, pero su propia voz
        // sigue volviendo por el micrófono después del turnComplete. La compuerta necesita 700 ms de
        // silencio para cerrar, así que su borde de «terminó» llega con el turno ya en reposo — y el
        // orbe se quedaba en «Pensando…» sin que nadie hubiera hablado, tapando además «En vivo ·
        // decime «listo» para cortar», que es el único lugar donde se dice cómo cerrar la charla.
        var (sesion, banco, parlantes) = ArmarConParlanteLento();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(24_000 * 2));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        var bloque = TimeSpan.FromMilliseconds(20);

        // El eco arranca mientras el parlante suena.
        sesion.NoteUserAudio(isVoice: true, bloque);

        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(await EsperarAsync(() => sesion.TurnState == LiveTurnState.Idle));

        parlantes.Drain();
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));

        // Y recién ahora la compuerta junta el silencio que le faltaba para cerrar el tramo del eco.
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Listening, sesion.Moment);
    }

    [Fact]
    public async Task DespuesDelEco_UnaFraseDeVerdadSiPasaAPensando()
    {
        // La contracara de la prueba anterior: callar el eco no puede callar a la persona.
        var (sesion, banco, parlantes) = ArmarConParlanteLento();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(24_000 * 2));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);

        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(await EsperarAsync(() => sesion.TurnState == LiveTurnState.Idle));
        parlantes.Drain();
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Listening));

        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        // Con el parlante callado, lo que arranca ahora es alguien hablando.
        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Thinking, sesion.Moment);
    }

    [Fact]
    public async Task AlInterrumpirla_LaVozQueSigueNoSeConfundeConSuEco()
    {
        // Hablarle encima vacía la cola en el acto, así que lo que la persona siga diciendo arranca
        // con el parlante callado. Sin borrar la marca del eco al interrumpir, la frase nueva
        // heredaba la marca de la respuesta que acaba de cortar y no dibujaba «pensando».
        var (sesion, banco, _) = ArmarConParlanteLento();
        await using var _guard = sesion;
        await sesion.StartAsync();

        banco.Last.Deliver(AudioMessage(24_000 * 2));
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Speaking));

        var bloque = TimeSpan.FromMilliseconds(20);
        sesion.NoteUserAudio(isVoice: true, bloque);

        banco.Last.Deliver("""{"serverContent":{"interrupted":true}}""");
        Assert.True(await EsperarAsync(() => sesion.Moment == LiveOrbMoment.Interrupted));
        banco.Last.Deliver("""{"serverContent":{"turnComplete":true}}""");
        Assert.True(await EsperarAsync(() => sesion.TurnState == LiveTurnState.Idle));

        sesion.NoteUserAudio(isVoice: true, bloque);
        for (var i = 0; i < 40; i++)
        {
            sesion.NoteUserAudio(isVoice: false, bloque);
        }

        Assert.Equal(LiveOrbMoment.Thinking, sesion.Moment);
    }
}
