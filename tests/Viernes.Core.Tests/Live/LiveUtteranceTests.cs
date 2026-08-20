using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Que interrumpirla sea la misma conversación y no dos pedidos sueltos.
/// </summary>
/// <remarks>
/// El síntoma, con las palabras del usuario: «hay veces como que capta lo que digo y si la interrumpo
/// y digo otra cosas como que intenta responder ambas separado en vez de considerarlo la misma
/// conversacion». Y lo que pidió que pasara: «que lo sume a lo que escuchó antes de ser interrumpida,
/// que sea como una conversación».
/// <para>
/// Lo que estas pruebas <b>no</b> cubren, dicho derecho: que el modelo de Google efectivamente funda
/// las dos partes en una sola respuesta. Eso lo decide él con la instrucción de sistema y pide clave,
/// red y hablarle. Acá se fija la mitad que sí es código de este lado: que los tramos se junten en un
/// pedido solo, y que lo único que cierre un pedido sea una respuesta que salió entera.
/// </para>
/// </remarks>
public sealed class LiveUtteranceTests
{
    [Fact]
    public void UnPedidoDeUnSoloTramo_EsElTramo()
    {
        var frase = new LiveUtterance();

        Assert.Equal("abrí el navegador", frase.Add("abrí el navegador").Text);
        Assert.True(frase.IsOpen);
        Assert.Equal(1, frase.Parts);
    }

    [Fact]
    public void LosTramosSeSuman_YDevuelveLaFraseEntera()
    {
        var frase = new LiveUtterance();

        frase.Add("anotá que mañana");

        Assert.Equal("anotá que mañana tengo que llamar al médico", frase.Add("tengo que llamar al médico").Text);
        Assert.Equal(2, frase.Parts);
    }

    [Fact]
    public void CerrarDevuelveLoQueHabia_YDejaElSiguienteLimpio()
    {
        var frase = new LiveUtterance();
        frase.Add("abrí el navegador");
        frase.Add("y buscá el clima");

        Assert.Equal("abrí el navegador y buscá el clima", frase.Close());
        Assert.False(frase.IsOpen);
        Assert.Null(frase.Close());

        // Y el pedido siguiente arranca sin arrastrar nada del anterior.
        Assert.Equal("qué hora es", frase.Add("qué hora es").Text);
    }

    [Fact]
    public void UnTramoVacioNoAbreNada()
    {
        // Los bordes del orbe llegan también cuando no hubo transcripción: sin esto, un pedido
        // quedaría «abierto» sin una sola palabra y la frase siguiente se anotaría como continuación
        // de la nada.
        var frase = new LiveUtterance();

        Assert.Equal(string.Empty, frase.Add("   ").Text);
        Assert.False(frase.IsOpen);
        Assert.Equal(0, frase.Parts);
    }

    [Fact]
    public void AlUnirNoQuedanEspaciosDeMas()
    {
        var frase = new LiveUtterance();
        frase.Add("  anotá   que mañana ");

        Assert.Equal("anotá que mañana tengo turno", frase.Add(" tengo  turno ").Text);
    }

    [Fact]
    public void ResetTiraLoQueHabiaAbierto()
    {
        var frase = new LiveUtterance();
        frase.Add("abrí el nav");

        frase.Reset();

        Assert.False(frase.IsOpen);
        Assert.Equal(string.Empty, frase.Text);
    }

    [Fact]
    public void UnaRespuestaEnteraCierraElPedido()
    {
        // «Hablando» → «te escucho» es la única vuelta que significa que contestó entera. Después de
        // eso lo que se diga sí es un pedido nuevo, y unirlo al anterior sería el defecto al revés.
        Assert.True(LiveUtterance.ClosesUtterance(LiveOrbMoment.Speaking, LiveOrbMoment.Listening));
    }

    [Fact]
    public void UnaInterrupcionNoCierraElPedido()
    {
        // Es literalmente lo que pidió el usuario: «que lo sume a lo que escuchó antes de ser
        // interrumpida». La cortaron, así que lo que sigue diciendo es la misma frase.
        Assert.False(LiveUtterance.ClosesUtterance(LiveOrbMoment.Interrupted, LiveOrbMoment.Listening));
    }

    [Fact]
    public void UnTurnoQueNacioYMurioSinRespuesta_NoCierraElPedido()
    {
        // El caso de la bitácora del usuario, tal cual: «Listening → Thinking» y 340 ms después
        // «Thinking → Listening». El servidor tomó una pausa para respirar por un punto final y
        // abrió un turno que murió sin que saliera una sola palabra. Eso no es un pedido contestado.
        Assert.False(LiveUtterance.ClosesUtterance(LiveOrbMoment.Thinking, LiveOrbMoment.Listening));
    }

    [Theory]
    [InlineData(LiveOrbMoment.Listening, LiveOrbMoment.Thinking)]
    [InlineData(LiveOrbMoment.Thinking, LiveOrbMoment.Speaking)]
    [InlineData(LiveOrbMoment.Speaking, LiveOrbMoment.Interrupted)]
    public void MientrasNoVuelvaElTurnoALaPersona_NoSeCierraNada(LiveOrbMoment antes, LiveOrbMoment ahora)
    {
        Assert.False(LiveUtterance.ClosesUtterance(antes, ahora));
    }

    [Fact]
    public void LaPausaQueElServidorTomoPorPuntoFinal_QuedaComoUnSoloPedido()
    {
        // La secuencia entera de la bitácora, con los momentos en el orden en que llegaron:
        // la persona dice media frase, el servidor la da por terminada, ella arranca a pensar, la
        // persona sigue hablando y el turno muere sin respuesta.
        var frase = new LiveUtterance();

        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);
        frase.Add("anotá que mañana");

        Recorrer(frase, LiveOrbMoment.Thinking, LiveOrbMoment.Listening);
        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);
        frase.Add("tengo que llamar al médico");

        // Un pedido, no dos. Es lo que después se anota como turno de la charla y lo que se dibuja
        // en la burbuja; partido en dos, la primera mitad se borraba al escribirse la segunda.
        Assert.Equal(2, frase.Parts);
        Assert.Equal("anotá que mañana tengo que llamar al médico", frase.Text);
    }

    [Fact]
    public void LoQueSeDiceAlCortarla_SeSumaALoAnterior()
    {
        var frase = new LiveUtterance();

        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);
        frase.Add("cuánto falta para las tres");

        // Empieza a contestar y la cortan hablándole encima.
        Recorrer(frase, LiveOrbMoment.Thinking, LiveOrbMoment.Speaking);
        Recorrer(frase, LiveOrbMoment.Speaking, LiveOrbMoment.Interrupted);
        Recorrer(frase, LiveOrbMoment.Interrupted, LiveOrbMoment.Listening);
        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);
        frase.Add("y en hora de españa");

        Assert.Equal("cuánto falta para las tres y en hora de españa", frase.Text);
    }

    [Fact]
    public void DespuesDeContestarEntera_LoSiguienteEsUnPedidoNuevo()
    {
        var frase = new LiveUtterance();

        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);
        frase.Add("qué hora es");
        Recorrer(frase, LiveOrbMoment.Thinking, LiveOrbMoment.Speaking);
        Recorrer(frase, LiveOrbMoment.Speaking, LiveOrbMoment.Listening);

        Recorrer(frase, LiveOrbMoment.Listening, LiveOrbMoment.Thinking);

        Assert.Equal("abrí el navegador", frase.Add("abrí el navegador").Text);
        Assert.Equal(1, frase.Parts);
    }

    [Fact]
    public void SumarTramoDice_SiContinuaLoAnterior()
    {
        var frase = new LiveUtterance();

        Assert.False(frase.Add("anotá que mañana").Continued);
        Assert.True(frase.Add("tengo turno").Continued);

        frase.Close();

        Assert.False(frase.Add("abrí el navegador").Continued);
    }

    /// <summary>
    /// Que la persona se vaya en el medio no puede dejar el pedido abierto para siempre.
    /// </summary>
    /// <remarks>
    /// Es el agujero que quedaba: lo único que cierra un pedido es que ella conteste entera, y si la
    /// cortaron y nadie volvió, eso no pasa nunca. Sin caducidad, lo que se dijera media hora más
    /// tarde se anotaba como continuación de una frase de media hora antes.
    /// </remarks>
    [Fact]
    public void SiLaPersonaSeFue_LoQueDiceAlVolverEsUnPedidoNuevo()
    {
        var reloj = new FakeTimeProvider();
        var frase = new LiveUtterance(TimeSpan.FromSeconds(45), reloj);

        frase.Add("cuánto falta para las tres");
        reloj.Advance(TimeSpan.FromMinutes(30));

        var alVolver = frase.Add("apagá la música");

        Assert.False(alVolver.Continued);
        Assert.Equal("cuánto falta para las tres", alVolver.Expired);
        Assert.Equal("apagá la música", alVolver.Text);
        Assert.Equal(1, frase.Parts);
    }

    /// <summary>Callar unos segundos después de cortarla sigue siendo la misma frase.</summary>
    [Fact]
    public void PensarUnosSegundosDespuesDeCortarla_SigueSiendoLaMismaFrase()
    {
        var reloj = new FakeTimeProvider();
        var frase = new LiveUtterance(TimeSpan.FromSeconds(45), reloj);

        frase.Add("cuánto falta para las tres");
        reloj.Advance(TimeSpan.FromSeconds(30));

        var sigue = frase.Add("y en hora de españa");

        Assert.True(sigue.Continued);
        Assert.Null(sigue.Expired);
        Assert.Equal("cuánto falta para las tres y en hora de españa", sigue.Text);
    }

    /// <summary>El plazo corre desde el último tramo, no desde el primero.</summary>
    /// <remarks>
    /// Alguien que sigue hablando de a pedazos no se queda sin pedido por llevar mucho rato: lo que
    /// lo suelta es el silencio, no la duración.
    /// </remarks>
    [Fact]
    public void HablandoDeAPedazos_ElPlazoCorreDesdeElUltimo()
    {
        var reloj = new FakeTimeProvider();
        var frase = new LiveUtterance(TimeSpan.FromSeconds(45), reloj);

        for (var tramo = 0; tramo < 10; tramo++)
        {
            Assert.Null(frase.Add($"tramo{tramo}").Expired);
            reloj.Advance(TimeSpan.FromSeconds(40));
        }

        Assert.Equal(10, frase.Parts);
    }

    /// <summary>Un tramo vacío no reinicia el plazo: no llegó nada.</summary>
    [Fact]
    public void UnTramoVacio_NoEstiraElPlazo()
    {
        var reloj = new FakeTimeProvider();
        var frase = new LiveUtterance(TimeSpan.FromSeconds(45), reloj);

        frase.Add("anotá que mañana");
        reloj.Advance(TimeSpan.FromSeconds(30));
        frase.Add("   ");
        reloj.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("anotá que mañana", frase.Add("tengo turno").Expired);
    }

    /// <summary>Aplica un cambio de momento igual que lo hace el anfitrión.</summary>
    private static void Recorrer(LiveUtterance frase, LiveOrbMoment antes, LiveOrbMoment ahora)
    {
        if (LiveUtterance.ClosesUtterance(antes, ahora))
        {
            frase.Close();
        }
    }

    /// <summary>Un reloj que sólo avanza cuando la prueba lo dice.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _ahora = new(2026, 8, 20, 3, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public void Advance(TimeSpan cuanto) => _ahora += cuanto;
    }
}
