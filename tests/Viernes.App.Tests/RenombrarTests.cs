using Viernes.App.Services;
using Xunit;

namespace Viernes.App.Tests;

/// <summary>
/// Qué se le dice al usuario cuando renombrar sale a medias.
/// </summary>
/// <remarks>
/// Renombrar toca cinco cosas —el archivo de preferencias, el prompt del sistema, el oído, la charla
/// hablada que esté abierta y las frases de activación— y cualquiera de las cinco puede no salir. Lo
/// que <b>no</b> puede pasar es que salga a medias y se informe que salió entero: el usuario cierra
/// la ventanita, prueba el nombre nuevo y no pasa nada.
/// <para>
/// <b>Lo que esta prueba NO cubre, dicho derecho:</b> que el oído de verdad vuelva a arrancar con las
/// frases nuevas. Eso pide un micrófono y un motor de reconocimiento reales, no se puede montar acá,
/// y está verificado sólo leyendo el código. Lo que sí se prueba es que, cuando el oído no vuelve, se
/// diga.
/// </para>
/// </remarks>
public sealed class RenombrarTests
{
    [Fact]
    public void SiSalioTodoBienNoHayNadaQueAvisar()
    {
        var pendiente = AssistantRuntime.DescribeRenameLeftovers(
            saved: true,
            promptRenamed: true,
            wakeRestarted: true,
            liveSessionOpen: false,
            handPickedPhrases: false);

        // El null no es sólo cosmético: SetAssistantNameAsync lo usa para saber si un reintento con
        // el mismo nombre tiene que volver a intentarlo o puede contestar que sí de una.
        Assert.Null(pendiente);
    }

    [Theory]
    [InlineData(false, true, true, false, false, "al reiniciar vuelve el nombre anterior")]
    [InlineData(true, false, true, false, false, "prompt del sistema")]
    [InlineData(true, true, false, false, false, "el oído no volvió a arrancar")]
    [InlineData(true, true, true, true, false, "la charla en voz que está abierta")]
    [InlineData(true, true, true, false, true, "puestas a mano")]
    public void CadaCosaQueFallaSeAvisa(
        bool saved,
        bool promptRenamed,
        bool wakeRestarted,
        bool liveSessionOpen,
        bool handPickedPhrases,
        string esperado)
    {
        var pendiente = AssistantRuntime.DescribeRenameLeftovers(
            saved,
            promptRenamed,
            wakeRestarted,
            liveSessionOpen,
            handPickedPhrases);

        Assert.NotNull(pendiente);
        Assert.Contains(esperado, pendiente, StringComparison.Ordinal);
    }

    [Fact]
    public void SiFallaTodoSeAvisaTodo()
    {
        // Y en una sola frase, no en cinco: son cinco caras del mismo renombrado, no cinco errores.
        var pendiente = AssistantRuntime.DescribeRenameLeftovers(
            saved: false,
            promptRenamed: false,
            wakeRestarted: false,
            liveSessionOpen: true,
            handPickedPhrases: true);

        Assert.NotNull(pendiente);
        Assert.Equal(4, pendiente.Count(caracter => caracter == ';'));
        Assert.EndsWith(".", pendiente, StringComparison.Ordinal);
    }

    [Fact]
    public void ElAvisoNoTerminaEnPuntoYComa()
    {
        // Un detalle de redacción que se rompe solo al agregar la sexta condición.
        foreach (var wake in new[] { true, false })
        {
            var pendiente = AssistantRuntime.DescribeRenameLeftovers(
                saved: false,
                promptRenamed: true,
                wakeRestarted: wake,
                liveSessionOpen: false,
                handPickedPhrases: false);

            Assert.NotNull(pendiente);
            Assert.False(pendiente.EndsWith(";", StringComparison.Ordinal), pendiente);
        }
    }
}
