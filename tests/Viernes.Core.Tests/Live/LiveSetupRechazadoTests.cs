using System.Diagnostics;
using System.Text.Json;
using Viernes.Core.Live;
using Viernes.Core.Tests.TestDoubles;
using Viernes.Core.Tools;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Que un setup rechazado cueste manos y no la voz.
/// </summary>
/// <remarks>
/// <b>Es lo que permite que hablando tenga todas las herramientas.</b> Antes declaraba tres de
/// cuarenta y seis por miedo a que un esquema que este protocolo no acepta rebotara el setup entero.
/// El miedo era razonable: no da un error de campo, rebota todo, y desde afuera se ve igual que
/// quedarse sin internet. Y verificar por adelantado no alcanza, porque el conjunto cambia — los
/// esquemas de las herramientas de servidores MCP los escribe un tercero y el usuario puede agregar
/// un servidor cualquier día.
/// <para>
/// Así que lo que protege no es una lista corta sino esto: si el servidor no acepta, se reintenta
/// una vez con el piso medido.
/// </para>
/// </remarks>
public sealed class LiveSetupRechazadoTests
{
    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(5);

    /// <summary>Un servidor de mentira que rechaza el primer setup y acepta el segundo.</summary>
    /// <remarks>
    /// Cerrar sin decir nada es exactamente lo que hace el de verdad cuando el setup no le gusta: no
    /// contesta qué campo estuvo mal, cierra el socket.
    /// </remarks>
    private sealed class RechazaElPrimero
    {
        private readonly List<FakeLiveTransport> _transports = [];
        private int _creados;

        public int Count => Volatile.Read(ref _creados);

        public IReadOnlyList<FakeLiveTransport> Todos
        {
            get
            {
                lock (_transports)
                {
                    return _transports.ToArray();
                }
            }
        }

        public ILiveTransport Create()
        {
            var transport = new FakeLiveTransport();
            var cual = Interlocked.Increment(ref _creados);
            lock (_transports)
            {
                _transports.Add(transport);
            }

            if (cual == 1)
            {
                transport.DeliverClose();
            }
            else
            {
                transport.Deliver("""{"setupComplete":{}}""");
            }

            return transport;
        }
    }

    private static ToolDefinition Herramienta(string nombre) =>
        ToolDefinition.Create(nombre, "de mentira", new { type = "object" });

    [Fact]
    public async Task SiElServidorRechazaElSetup_ReintentaConElPisoYArranca()
    {
        var banco = new RechazaElPrimero();
        var manos = new FakeLiveToolBridge();
        manos.Tools.AddRange([Herramienta("pc_action"), Herramienta("archivo"), Herramienta("spotify_play")]);
        manos.Essential.Add(Herramienta("pc_action"));

        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            banco.Create,
            tools: manos);

        Assert.True(await client.StartAsync());
        Assert.Equal(2, banco.Count);

        var segundo = banco.Todos[1].SentSnapshot().First();
        Assert.Contains("pc_action", segundo, StringComparison.Ordinal);
        Assert.DoesNotContain("spotify_play", segundo, StringComparison.Ordinal);
        Assert.DoesNotContain("archivo", segundo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElPrimerIntento_SiLlevaTodasLasHerramientas()
    {
        var banco = new RechazaElPrimero();
        var manos = new FakeLiveToolBridge();
        manos.Tools.AddRange([Herramienta("pc_action"), Herramienta("archivo")]);
        manos.Essential.Add(Herramienta("pc_action"));

        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            banco.Create,
            tools: manos);

        await client.StartAsync();

        var primero = banco.Todos[0].SentSnapshot().First();
        Assert.Contains("archivo", primero, StringComparison.Ordinal);
    }

    /// <summary>
    /// El rechazo queda dicho, no se traga.
    /// </summary>
    /// <remarks>
    /// Sin el renglón, la asistente pierde la mitad de sus manos y se comporta como si nunca las
    /// hubiera tenido — contestando «eso no lo puedo hacer» a cosas que sí sabe hacer, sin que nada
    /// explique por qué. Es el mismo defecto que ya tuvo dos veces en este proyecto.
    /// </remarks>
    [Fact]
    public async Task ElRechazoQuedaDichoYNoEsFatal()
    {
        var banco = new RechazaElPrimero();
        var manos = new FakeLiveToolBridge();
        manos.Tools.AddRange([Herramienta("pc_action"), Herramienta("archivo")]);
        manos.Essential.Add(Herramienta("pc_action"));

        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            banco.Create,
            tools: manos);

        var avisos = new List<LiveFailureEventArgs>();
        client.Failed += (_, aviso) => avisos.Add(aviso);

        Assert.True(await client.StartAsync());

        var aviso = Assert.Single(avisos, a => a.Message.Contains("herramientas", StringComparison.Ordinal));
        Assert.False(aviso.Fatal);
    }

    /// <summary>
    /// Una vez rechazado, no vuelve a intentar con todas en cada reconexión.
    /// </summary>
    /// <remarks>
    /// Si el rechazo lo causa un esquema, va a volver a causarlo siempre. Reintentando cada vez, la
    /// persona vería la voz cortarse una vez por reconexión sin motivo aparente, y cada corte
    /// costaría una conexión de más.
    /// </remarks>
    [Fact]
    public async Task DespuesDeUnRechazo_LasReconexionesVanDirectoAlPiso()
    {
        var banco = new RechazaElPrimero();
        var manos = new FakeLiveToolBridge();
        manos.Tools.AddRange([Herramienta("pc_action"), Herramienta("archivo")]);
        manos.Essential.Add(Herramienta("pc_action"));

        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            banco.Create,
            tools: manos);

        Assert.True(await client.StartAsync());
        await client.StopAsync();
        Assert.True(await client.StartAsync());

        // Tres transportes: el rechazado, el reintento, y el de la vuelta. El último ya no declara
        // todas.
        Assert.True(await EsperarAsync(() => banco.Count >= 3));
        var ultimo = banco.Todos[^1].SentSnapshot().First();
        Assert.DoesNotContain("archivo", ultimo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un corte de red NO cuesta las herramientas.
    /// </summary>
    /// <remarks>
    /// <b>Es el defecto que la auditoría encontró en el arreglo anterior.</b> Se reintentaba con el
    /// piso ante cualquier falla de conexión, y la marca no se bajaba nunca: un corte de internet de
    /// dos segundos dejaba a la asistente hablando con tres herramientas de cuarenta y seis hasta que
    /// se cerrara el programa — mientras el anuncio pegado en su instrucción de sistema le seguía
    /// prometiendo al modelo que las tenía todas.
    /// <para>
    /// La firma de un rechazo de esquema es que el setup llegue a mandarse y el servidor cierre o
    /// conteste error. Que el socket ni siquiera abra es otra cosa.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SiNoAbreElSocket_NoSeCulpaALasHerramientas()
    {
        var banco = new NoAbreLaPrimera();
        var manos = new FakeLiveToolBridge();
        manos.Tools.AddRange([Herramienta("pc_action"), Herramienta("archivo")]);
        manos.Essential.Add(Herramienta("pc_action"));

        await using var client = new GeminiLiveClient(
            new GeminiLiveOptions(enabled: true),
            () => "clave-de-mentira",
            new RecordingAudioSink(),
            banco.Create,
            tools: manos);

        // El primer intento no abre: no se reintenta, y no se culpa a las herramientas.
        Assert.False(await client.StartAsync());
        Assert.Equal(1, banco.Count);

        // Y cuando la red vuelve, sigue declarando TODAS.
        Assert.True(await client.StartAsync());
        var setup = banco.Todos[^1].SentSnapshot().First();
        Assert.Contains("archivo", setup, StringComparison.Ordinal);
    }

    /// <summary>Un transporte que no abre la primera vez y sí la segunda.</summary>
    private sealed class NoAbreLaPrimera
    {
        private readonly List<FakeLiveTransport> _transports = [];
        private int _creados;

        public int Count => Volatile.Read(ref _creados);

        public IReadOnlyList<FakeLiveTransport> Todos
        {
            get
            {
                lock (_transports)
                {
                    return _transports.ToArray();
                }
            }
        }

        public ILiveTransport Create()
        {
            var cual = Interlocked.Increment(ref _creados);
            if (cual == 1)
            {
                return new NoAbre();
            }

            var transport = new FakeLiveTransport();
            lock (_transports)
            {
                _transports.Add(transport);
            }

            transport.Deliver("""{"setupComplete":{}}""");
            return transport;
        }
    }

    /// <summary>Se cae al abrir, como un socket sin red del otro lado.</summary>
    private sealed class NoAbre : ILiveTransport
    {
        public bool IsOpen => false;

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
            throw new System.Net.WebSockets.WebSocketException("no hay red");

        public Task SendAsync(string message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no abrió");

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no abrió");

        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
