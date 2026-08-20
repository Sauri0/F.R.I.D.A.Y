using System.Diagnostics;
using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Cuándo arranca la reproducción y cuándo se oyó un silencio que no estaba en la voz.
/// </summary>
/// <remarks>
/// Estas pruebas son el motivo de que <see cref="LivePlayout"/> viva en Core y no adentro de la
/// salida de Windows: el corte de voz que reportó el usuario no dejaba rastro y se buscó una vez
/// leyendo código, sin encontrarlo. Acá la aritmética se puede correr sin tarjeta de sonido.
/// <para>
/// <b>Cuatro de estas pruebas son de un instrumento que mentía.</b> La primera versión informaba un
/// atraso falso en cada arranque, informaba una herramienta lenta como si fuera un corte de voz, y
/// después del primer aviso se quedaba muda para el resto de la charla. Están marcadas.
/// </para>
/// </remarks>
public sealed class LivePlayoutTests
{
    /// <summary>Un búfer del driver con la geometría real de la salida: 20 ms a 24 kHz de 16 bits.</summary>
    private const int Buffer = 24_000 * 2 * 20 / 1000;

    /// <summary>Lo que el driver se lleva en la primera vuelta: cinco búferes de veinte.</summary>
    private static readonly TimeSpan DriverLatency = TimeSpan.FromMilliseconds(100);

    /// <summary>Un instante, en sellos de <see cref="Stopwatch"/>, a tantos milisegundos de cero.</summary>
    private static long At(double milliseconds) =>
        (long)(milliseconds / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void ConUnSoloBloqueEncolado_NoDejaArrancar()
    {
        // Es el defecto que esto viene a cerrar, y está medido: NAudio, en la primera vuelta de su
        // hilo, le pide al proveedor TODOS sus búferes. Arrancar con veinte encolados manda ochenta
        // milisegundos de silencio a los parlantes adentro de la primera palabra, todas las veces.
        var playout = new LivePlayout();

        Assert.False(playout.ShouldStart(TimeSpan.FromMilliseconds(20), noMoreComing: false));
    }

    /// <summary>El colchón es exactamente lo que el driver se lleva de una. Ni más.</summary>
    /// <remarks>
    /// El banco da relleno cero justo al llegar a la latencia del driver, así que todo lo que se
    /// espere por encima de eso es demora pura — y se paga en cada respuesta, incluida la que sigue
    /// a una interrupción, que es la interacción que el usuario dijo que andaba bien.
    /// </remarks>
    [Fact]
    public void ElColchonEsLoQueElDriverSeLlevaDeUna_NiUnMilisegundoMas()
    {
        var playout = new LivePlayout();

        Assert.Equal(DriverLatency, LivePlayout.DefaultPrime);
        Assert.True(playout.ShouldStart(DriverLatency, noMoreComing: false));
        Assert.False(playout.ShouldStart(DriverLatency - TimeSpan.FromMilliseconds(20), noMoreComing: false));
    }

    [Fact]
    public void CuandoNoVieneMasAudio_ArrancaAunqueNoJunteElColchon()
    {
        // Una respuesta de una palabra puede no juntar cien milisegundos nunca. Sin esta salida se
        // quedaría muda esperando un audio que ya no existe.
        var playout = new LivePlayout();

        Assert.True(playout.ShouldStart(TimeSpan.FromMilliseconds(20), noMoreComing: true));
    }

    [Fact]
    public void SinNadaEncolado_NoArrancaNiAunqueElTurnoHayaCerrado()
    {
        var playout = new LivePlayout();

        Assert.False(playout.ShouldStart(TimeSpan.Zero, noMoreComing: true));
    }

    [Fact]
    public void LaColaSeQuedaCortaYVuelveElAudio_EsUnHuecoDeLaCola()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        // Sonando bien.
        Assert.Null(playout.NoteRead(Buffer * 5, Buffer, At(0)));

        // Se queda corta: media lectura de audio y media de relleno.
        Assert.Null(playout.NoteRead(Buffer / 2, Buffer, At(20)));

        // Y dos lecturas enteras de nada.
        Assert.Null(playout.NoteRead(0, Buffer, At(40)));
        Assert.Null(playout.NoteRead(0, Buffer, At(60)));

        // Vuelve el audio: recién ahí se sabe que fue un tajo y no el final.
        var gap = playout.NoteRead(Buffer * 3, Buffer, At(80));

        Assert.NotNull(gap);
        Assert.Equal(LiveAudioGapKind.Queue, gap.Value.Kind);
        Assert.Equal(50, gap.Value.Duration.TotalMilliseconds, 1);
        Assert.Equal(1, playout.Gaps);
    }

    /// <summary>
    /// Una herramienta lenta a mitad de turno no es un corte de voz.
    /// </summary>
    /// <remarks>
    /// <b>Acá el instrumento mentía, y mentía justo en el caso que hay en la bitácora del usuario:</b>
    /// una herramienta que tardó 10,1 s. La cola se seca con el turno abierto, y como la marca de
    /// «esto es el final» dependía de un <c>turnComplete</c> que no llega, el silencio entero se
    /// informaba como un hueco de diez segundos — el mismo renglón que un corte de voz, enterrándolo.
    /// </remarks>
    [Fact]
    public void UnaHerramientaLenta_NoSeInformaComoCorteDeVoz()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 5, Buffer, At(0)));
        Assert.Null(playout.NoteRead(Buffer / 2, Buffer, At(20)));

        // Diez segundos de cola seca con el turno abierto: la herramienta.
        for (var t = 40; t <= 10_000; t += 20)
        {
            Assert.Null(playout.NoteRead(0, Buffer, At(t)));
        }

        // Y vuelve el audio con el resultado.
        Assert.Null(playout.NoteRead(Buffer * 5, Buffer, At(10_020)));
        Assert.Equal(0, playout.Gaps);

        // Pero el silencio se contó igual: lo que el techo decide es qué merece un renglón.
        Assert.True(playout.Filler > TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void ElFinalDeLaRespuestaNoEsUnHueco()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 5, Buffer, At(0)));
        Assert.Null(playout.NoteRead(Buffer / 2, Buffer, At(20)));
        playout.NoteTurnEnded();

        for (var t = 40; t <= 1_000; t += 20)
        {
            Assert.Null(playout.NoteRead(0, Buffer, At(t)));
        }

        // Llega el turno siguiente: lo de antes era el final, no un tajo.
        Assert.Null(playout.NoteRead(Buffer * 5, Buffer, At(1_020)));
        Assert.Equal(0, playout.Gaps);
    }

    [Fact]
    public void ElParlanteCalladoEntreTurnosNoEsUnHueco()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(playout.NoteRead(0, Buffer, At(i * 20)));
        }

        Assert.Equal(0, playout.Gaps);
    }

    /// <summary>
    /// El despacho tardío de la primera lectura no es un atraso del driver.
    /// </summary>
    /// <remarks>
    /// <b>Era un falso positivo en cada arranque.</b> El reparto se contaba desde el <c>Play</c>, y
    /// entre el <c>Play</c> y la primera lectura hay un despacho del ThreadPool que en esta máquina
    /// llegó a tardar 56 ms —medido en el banco—. Se informaba un hueco que no sonó: no se le había
    /// entregado nada al dispositivo todavía. Y encima gastaba el único aviso de la charla, por la
    /// falla de abajo.
    /// </remarks>
    [Fact]
    public void LaPrimeraLecturaTardia_NoEsUnAtrasoDelDriver()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(60)));
        Assert.Equal(0, playout.Gaps);
    }

    [Fact]
    public void ElDriverQueVuelveTarde_SeInformaComoDelLadoDeLaMaquina()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(0)));

        // Con audio de sobra en la cola, volvió 300 ms tarde: la máquina, no la red. Se informan
        // 260 y no 300 porque las dos lecturas entregaron 40 ms de audio, que sí sonaron.
        var gap = playout.NoteRead(Buffer * 50, Buffer, At(300));

        Assert.NotNull(gap);
        Assert.Equal(LiveAudioGapKind.Driver, gap.Value.Kind);
        Assert.Equal(260, gap.Value.Duration.TotalMilliseconds, 1);
    }

    [Fact]
    public void ElDriverAlDia_NoInformaNada()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(0)));
        }

        for (var i = 5; i < 100; i++)
        {
            Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At((i - 4) * 20)));
        }

        Assert.Equal(0, playout.Gaps);
    }

    [Fact]
    public void UnAtrasoSostenidoSeInformaUnaSolaVez()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(0)));
        Assert.NotNull(playout.NoteRead(Buffer * 50, Buffer, At(500)));
        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(520)));
        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(540)));

        Assert.Equal(1, playout.Gaps);
    }

    /// <summary>
    /// Un segundo atraso, más tarde en la misma charla, se sigue informando.
    /// </summary>
    /// <remarks>
    /// <b>Acá el instrumento se quedaba mudo para siempre.</b> El reparto es acumulado y su techo es
    /// la latencia del driver, así que una vez ido a negativo no vuelve: con una bandera que sólo se
    /// baja cuando el reparto vuelve a dar, el primer atraso apagaba el aviso y todo lo que pasara
    /// después en esa charla no se contaba. Los contadores del renglón de cierre subcontaban.
    /// </remarks>
    [Fact]
    public void UnSegundoAtrasoMasTarde_TambienSeInforma()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(0)));
        Assert.NotNull(playout.NoteRead(Buffer * 50, Buffer, At(300)));

        // Treinta segundos al día: cada lectura de 20 ms llega 20 ms después.
        var ahora = 300.0;
        for (var i = 0; i < 1_500; i++)
        {
            ahora += 20;
            Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(ahora)));
        }

        // Y ahora sí, otro atraso de medio segundo.
        var gap = playout.NoteRead(Buffer * 50, Buffer, At(ahora + 500));

        Assert.NotNull(gap);
        Assert.Equal(LiveAudioGapKind.Driver, gap.Value.Kind);
        Assert.Equal(480, gap.Value.Duration.TotalMilliseconds, 1);
        Assert.Equal(2, playout.Gaps);
    }

    [Fact]
    public void ConLaReproduccionParada_NoCuentaNada()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();
        playout.NoteStopped();

        Assert.Null(playout.NoteRead(0, Buffer, At(5_000)));
        Assert.Equal(0, playout.Gaps);
    }

    [Fact]
    public void ElSilencioDeTodosLosHuecosSeSuma()
    {
        var playout = new LivePlayout();
        playout.NoteStarted();

        Assert.Null(playout.NoteRead(Buffer * 50, Buffer, At(0)));
        Assert.Null(playout.NoteRead(0, Buffer, At(20)));
        Assert.NotNull(playout.NoteRead(Buffer * 50, Buffer, At(40)));

        Assert.Null(playout.NoteRead(0, Buffer, At(60)));
        Assert.Null(playout.NoteRead(0, Buffer, At(80)));
        Assert.NotNull(playout.NoteRead(Buffer * 50, Buffer, At(100)));

        Assert.Equal(2, playout.Gaps);
        Assert.Equal(60, playout.GapTotal.TotalMilliseconds, 1);
    }
}
