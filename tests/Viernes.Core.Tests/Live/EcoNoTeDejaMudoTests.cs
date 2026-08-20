using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Que la compuerta de eco nunca pueda dejarte sin micrófono.
/// </summary>
/// <remarks>
/// La compuerta se pone cuando el parlante suena, y eso se lo pregunta a la salida de audio: la cola
/// pendiente más lo que el driver tiene en la mano. <b>Esa cola no es suya y puede quedar clavada</b>
/// —el dispositivo se cae, lo desenchufan en el medio de una respuesta, el proveedor deja de leer—.
/// Con la cola trabada, el parlante figura sonando para siempre y la compuerta se queda puesta para
/// siempre.
/// <para>
/// Lo grave no es perder el eco: es que <b>deja de oírte y nadie se entera</b>. El orbe seguiría
/// diciendo que escucha. Antes de que la compuerta existiera, un parlante trabado era inofensivo
/// —no la oías hablar y listo—; con la compuerta también te dejaría mudo. Un mecanismo nuevo no
/// puede convertir la falla de otro en algo peor de lo que era.
/// </para>
/// </remarks>
public sealed class EcoNoTeDejaMudoTests
{
    private static readonly TimeSpan Bloque = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void ConElParlanteTrabadoTerminaDejandoPasarElMicrofono()
    {
        var compuerta = new LiveEchoGate();

        // Un minuto entero con el parlante diciendo que suena y sólo eco flojo entrando: es
        // exactamente lo que se ve si la cola de salida se traba.
        var frenados = 0;
        var pasados = 0;

        for (var i = 0; i < 3000; i++)
        {
            var veredicto = compuerta.Decide(
                speakerAudible: true, isVoice: true, level: 0.30, blockDuration: Bloque);

            if (veredicto == LiveMicrophoneVerdict.Hold)
            {
                frenados++;
            }
            else
            {
                pasados++;
            }
        }

        Assert.True(compuerta.GaveUp, "la compuerta se quedó puesta para siempre: eso te deja mudo.");
        Assert.True(frenados > 0, "no frenó nada: entonces tampoco estaba protegiendo del eco.");
        // Sesenta segundos de bloques: los primeros treinta frenados, el resto pasando. La cuenta
        // exacta no importa; lo que importa es que la segunda mitad pase.
        Assert.True(
            pasados > 1400,
            $"sólo dejó pasar {pasados} de 3000 bloques después de rendirse.");
    }

    [Fact]
    public void SeRindeReciénDespuesDeMuchoMasQueUnaRespuesta()
    {
        // La válvula no puede dispararse en una respuesta larga de verdad. Diez segundos hablando
        // seguido es muchísimo para una charla y tiene que seguir protegida.
        var compuerta = new LiveEchoGate();

        for (var i = 0; i < 500; i++)
        {
            compuerta.Decide(speakerAudible: true, isVoice: true, level: 0.30, blockDuration: Bloque);
        }

        Assert.False(compuerta.GaveUp, "se rindió en diez segundos: eso es una respuesta normal.");
    }

    [Fact]
    public void CuandoElParlanteSeCallaLaCuentaVuelveACero()
    {
        // Lo que hace que la válvula no se gaste: cada vez que ella termina de hablar de verdad, la
        // cuenta arranca de nuevo. Si no, una charla larga terminaría rindiéndose sola.
        var compuerta = new LiveEchoGate();

        for (var vuelta = 0; vuelta < 6; vuelta++)
        {
            // Ocho segundos hablando…
            for (var i = 0; i < 400; i++)
            {
                compuerta.Decide(speakerAudible: true, isVoice: true, level: 0.30, blockDuration: Bloque);
            }

            // …y un silencio de verdad, que drena la cola.
            for (var i = 0; i < 30; i++)
            {
                compuerta.Decide(speakerAudible: false, isVoice: false, level: 0.02, blockDuration: Bloque);
            }
        }

        Assert.False(
            compuerta.GaveUp,
            "se rindió después de varias respuestas normales: la cuenta no se estaba reiniciando.");
    }
}
