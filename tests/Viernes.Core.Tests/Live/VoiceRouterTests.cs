using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// La elección de camino: cuándo se va por la sesión en vivo y cuándo por el de siempre.
/// </summary>
/// <remarks>
/// Lo que se prueba acá no es sólo qué camino sale: es que <b>siempre salga un motivo</b>. Un
/// asistente que se fue por el camino viejo sin decir por qué manda a revisar tres cosas a mano
/// —el interruptor, la clave y el servicio— cada vez que alguien pregunta por qué tarda más.
/// </remarks>
public sealed class VoiceRouterTests
{
    [Fact]
    public void ConClaveYEncendido_VaPorElCaminoNuevo()
    {
        var decision = VoiceRouter.Choose(liveEnabled: true, hasGoogleKey: true);

        Assert.Equal(VoiceRoute.Live, decision.Route);
        Assert.True(decision.IsLive);
        Assert.Equal(VoiceRouter.LiveReason, decision.Reason);
    }

    [Fact]
    public void Apagado_VaPorElDeSiempreYLoDice()
    {
        var decision = VoiceRouter.Choose(liveEnabled: false, hasGoogleKey: true);

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.Equal(VoiceRouter.DisabledReason, decision.Reason);
    }

    [Fact]
    public void SinClave_VaPorElDeSiempreYLoDice()
    {
        var decision = VoiceRouter.Choose(liveEnabled: true, hasGoogleKey: false);

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.Equal(VoiceRouter.MissingKeyReason, decision.Reason);
    }

    [Fact]
    public void ApagadoYSinClave_ElMotivoEsElInterruptor()
    {
        // El orden importa: mandar a buscar la clave a alguien que además lo tiene apagado es
        // mandarlo a hacer un trabajo que no va a cambiar nada.
        var decision = VoiceRouter.Choose(liveEnabled: false, hasGoogleKey: false);

        Assert.Equal(VoiceRouter.DisabledReason, decision.Reason);
    }

    [Fact]
    public void ConLaTrabaPuesta_VaPorElDeSiempreConElMotivoDeLaTraba()
    {
        var decision = VoiceRouter.Choose(
            liveEnabled: true,
            hasGoogleKey: true,
            blockedReason: "Se cortó la sesión en vivo y no pude reconectar.");

        Assert.Equal(VoiceRoute.Classic, decision.Route);
        Assert.Equal("Se cortó la sesión en vivo y no pude reconectar.", decision.Reason);
    }

    [Fact]
    public void LaLineaDeBitacoraDiceElCaminoYElMotivo()
    {
        Assert.Equal(
            $"vivo · {VoiceRouter.LiveReason}",
            VoiceRouter.Choose(liveEnabled: true, hasGoogleKey: true).ToString());

        Assert.Equal(
            $"siempre · {VoiceRouter.MissingKeyReason}",
            VoiceRouter.Choose(liveEnabled: true, hasGoogleKey: false).ToString());
    }
}
