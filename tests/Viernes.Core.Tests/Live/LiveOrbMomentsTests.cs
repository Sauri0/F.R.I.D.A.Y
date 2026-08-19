using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// El mapeo del turno en vivo a lo que dibuja el orbe.
/// </summary>
public sealed class LiveOrbMomentsTests
{
    [Fact]
    public void ReposoSinNadaEsperando_EsTeEscucho()
    {
        Assert.Equal(
            LiveOrbMoment.Listening,
            LiveOrbMoments.For(LiveTurnState.Idle, waitingForReply: false));
    }

    [Fact]
    public void ReposoConLaFraseCerrada_EsPensando()
    {
        Assert.Equal(
            LiveOrbMoment.Thinking,
            LiveOrbMoments.For(LiveTurnState.Idle, waitingForReply: true));
    }

    [Fact]
    public void RespondiendoEsHablando()
    {
        Assert.Equal(
            LiveOrbMoment.Speaking,
            LiveOrbMoments.For(LiveTurnState.Responding, waitingForReply: false));
    }

    [Fact]
    public void DrenandoTambienEsHablando()
    {
        // Es el tramo en que el servidor terminó de generar y los parlantes siguen sonando. Dibujar
        // «terminó» acá apaga el orbe mientras ella sigue hablando.
        Assert.Equal(
            LiveOrbMoment.Speaking,
            LiveOrbMoments.For(LiveTurnState.Draining, waitingForReply: false));
    }

    [Fact]
    public void InterrumpidaGanaSobreCualquierEspera()
    {
        Assert.Equal(
            LiveOrbMoment.Interrupted,
            LiveOrbMoments.For(LiveTurnState.Interrupted, waitingForReply: true));
    }

    [Fact]
    public void HablandoIgnoraLaEspera()
    {
        // Con el micrófono abierto mientras ella habla, su propia voz vuelve por los parlantes. Si
        // la espera pudiera ganarle a «hablando», el eco de una respuesta larga dibujaría «pensando»
        // encima de una voz que está sonando.
        Assert.Equal(
            LiveOrbMoment.Speaking,
            LiveOrbMoments.For(LiveTurnState.Responding, waitingForReply: true));
    }
}
