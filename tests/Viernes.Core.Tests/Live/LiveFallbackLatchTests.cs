using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// La traba que impide que un servicio caído se cobre un intento por conversación.
/// </summary>
/// <remarks>
/// Todo corre contra un reloj de mentira. Probar la escalera con el reloj de verdad tardaría media
/// hora, así que no se probaría, y una traba sin probar que se queda trabada apaga el camino nuevo
/// para siempre sin que nadie se entere: falla callada, que es la peor.
/// </remarks>
public sealed class LiveFallbackLatchTests
{
    /// <summary>Un reloj que se mueve cuando la prueba lo dice.</summary>
    private sealed class Reloj
    {
        public DateTimeOffset Ahora { get; private set; } = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Leer() => Ahora;

        public void Avanzar(TimeSpan cuanto) => Ahora += cuanto;
    }

    private static (LiveFallbackLatch Traba, Reloj Reloj) Armar(TimeSpan? espera = null, TimeSpan? techo = null)
    {
        var reloj = new Reloj();
        var traba = new LiveFallbackLatch(
            espera ?? TimeSpan.FromMinutes(2),
            techo ?? TimeSpan.FromMinutes(30),
            reloj.Leer);

        return (traba, reloj);
    }

    [Fact]
    public void ReciénArmada_NoTrabaNada()
    {
        var (traba, _) = Armar();

        Assert.Null(traba.BlockedReason);
        Assert.Null(traba.OpensAt);
        Assert.Equal(0, traba.ConsecutiveTrips);
    }

    [Fact]
    public void AlCaerse_TrabaYGuardaElMotivo()
    {
        var (traba, _) = Armar();

        traba.Trip("Se cortó la sesión en vivo y no pude reconectar.");

        Assert.Equal("Se cortó la sesión en vivo y no pude reconectar.", traba.BlockedReason);
        Assert.Equal(1, traba.ConsecutiveTrips);
    }

    [Fact]
    public void PasadaLaEspera_SeAbreSola()
    {
        var (traba, reloj) = Armar(TimeSpan.FromMinutes(2));
        traba.Trip("se cayó");

        reloj.Avanzar(TimeSpan.FromMinutes(1));
        Assert.NotNull(traba.BlockedReason);

        reloj.Avanzar(TimeSpan.FromMinutes(1));
        Assert.Null(traba.BlockedReason);
    }

    [Fact]
    public void CadaCaidaSeguida_EsperaElDoble()
    {
        var (traba, reloj) = Armar(TimeSpan.FromMinutes(2));

        traba.Trip("primera");
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        Assert.Null(traba.BlockedReason);

        traba.Trip("segunda");
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        Assert.NotNull(traba.BlockedReason);
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        Assert.Null(traba.BlockedReason);

        Assert.Equal(2, traba.ConsecutiveTrips);
    }

    [Fact]
    public void LaEscaleraTieneTecho()
    {
        var (traba, reloj) = Armar(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));

        for (var i = 0; i < 20; i++)
        {
            traba.Trip($"caída {i}");
        }

        reloj.Avanzar(TimeSpan.FromMinutes(10));
        Assert.Null(traba.BlockedReason);
    }

    [Fact]
    public void ElContadorNoSeBorraSoloPorQuePasoElTiempo()
    {
        // Que se venza la espera no prueba que el servicio volvió. Si el contador se borrara acá, un
        // servicio caído todo el día costaría un intento cada dos minutos para siempre.
        var (traba, reloj) = Armar(TimeSpan.FromMinutes(2));

        traba.Trip("primera");
        reloj.Avanzar(TimeSpan.FromMinutes(5));
        Assert.Null(traba.BlockedReason);

        Assert.Equal(1, traba.ConsecutiveTrips);
    }

    [Fact]
    public void CuandoLaSesionAbre_SeBorraLaEscalera()
    {
        var (traba, _) = Armar();
        traba.Trip("primera");
        traba.Trip("segunda");

        traba.Reset();

        Assert.Null(traba.BlockedReason);
        Assert.Equal(0, traba.ConsecutiveTrips);
    }

    [Fact]
    public void UnMotivoVacioNoEsUnMotivo()
    {
        var (traba, _) = Armar();

        Assert.Throws<ArgumentException>(() => traba.Trip("   "));
    }

    [Fact]
    public void SesentaCaidasSeguidasNoDesbordanElReloj()
    {
        // La escalera es exponencial y un asistente que arranca con Windows puede sumar caídas todo
        // el día: sin el tope del exponente, el TimeSpan desborda y la traba queda puesta para
        // siempre o —peor— se abre al instante por un tiempo negativo.
        var (traba, reloj) = Armar(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));

        for (var i = 0; i < 60; i++)
        {
            traba.Trip($"caída {i}");
        }

        Assert.NotNull(traba.BlockedReason);
        reloj.Avanzar(TimeSpan.FromMinutes(30));
        Assert.Null(traba.BlockedReason);
    }
}
