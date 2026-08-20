using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

/// <summary>
/// Que la compuerta de eco no aprenda a ignorar a la persona.
/// </summary>
/// <remarks>
/// Los dos defectos que estas pruebas fijan salieron de una revisión, no de un síntoma, y los dos
/// eran silenciosos: la compuerta seguía funcionando y se iba volviendo sorda sola.
/// <list type="number">
///   <item><b>El listón trepaba por encima de quien intentaba interrumpir.</b> La referencia aprendía
///   de cualquier bloque que no cruzara el listón, y una voz humana que quedara por debajo tampoco
///   cruza: se la comía como si fuera eco y subía la vara. Cuanto más te esforzabas, más alto la
///   ponía.</item>
///   <item><b>Un eco fuerte apagaba la compuerta para toda la respuesta.</b> Si el eco entraba por
///   encima del listón, todos sus bloques cruzaban, ninguno alimentaba la referencia, y la referencia
///   no lo alcanzaba nunca.</item>
/// </list>
/// <para>
/// Los dos se arreglan con lo mismo: la referencia sólo se mide en la ventana del arranque de cada
/// respuesta —donde lo único que puede sonar es ella— y después se sostiene sin decaer, que es lo que
/// </para>
/// </remarks>
public sealed class EcoNoAprendeDeLaPersonaTests
{
    private static readonly TimeSpan Bloque = TimeSpan.FromMilliseconds(20);

    /// <summary>Deja pasar la ventana de medición con eco parejo, que es lo que pasa de verdad.</summary>
    private static LiveEchoGate ConEcoMedido(double eco)
    {
        var compuerta = new LiveEchoGate();

        // Un segundo entero de ella hablando sola: cubre la ventana de medición y bastante más.
        for (var i = 0; i < 50; i++)
        {
            compuerta.Decide(speakerAudible: true, isVoice: true, level: eco, blockDuration: Bloque);
        }

        return compuerta;
    }

    [Fact]
    public void LaVozDeLaPersonaNoPuedeSubirElListon()
    {
        var compuerta = ConEcoMedido(0.30);
        var listonAntes = compuerta.Bar;

        // Alguien habla encima, pero flojo: queda por debajo del listón. Antes, cada uno de estos
        // bloques subía la referencia y el listón se le iba por encima.
        for (var i = 0; i < 40; i++)
        {
            compuerta.Decide(speakerAudible: true, isVoice: true, level: listonAntes * 0.9, blockDuration: Bloque);
        }

        Assert.True(
            compuerta.Bar <= listonAntes + 0.0001,
            $"el listón subió de {listonAntes:0.000} a {compuerta.Bar:0.000} por escuchar a la persona.");
    }

    [Fact]
    public void DespuesDeHablarleFlojoTodaviaSeLaPuedeCortar()
    {
        // La consecuencia de lo de arriba, dicha como la vive el usuario: intentar cortarla sin
        // éxito no puede hacer que cortarla se vuelva más difícil.
        var compuerta = ConEcoMedido(0.30);

        for (var i = 0; i < 40; i++)
        {
            compuerta.Decide(speakerAudible: true, isVoice: true, level: compuerta.Bar * 0.9, blockDuration: Bloque);
        }

        var abrio = false;
        for (var i = 0; i < 20 && !abrio; i++)
        {
            abrio = compuerta.Decide(speakerAudible: true, isVoice: true, level: 0.95, blockDuration: Bloque)
                == LiveMicrophoneVerdict.Release;
        }

        Assert.True(abrio, "después de hablarle flojo, gritarle ya no la cortaba.");
    }

    [Fact]
    public void UnEcoFuerteSeMideEnLaVentanaYNoApagaLaCompuerta()
    {
        // El segundo defecto: con el eco por encima del listón de arranque, la referencia no lo
        // alcanzaba nunca y todo pasaba como si no hubiera compuerta.
        var compuerta = ConEcoMedido(0.80);

        var dejoPasar = 0;
        for (var i = 0; i < 50; i++)
        {
            if (compuerta.Decide(speakerAudible: true, isVoice: true, level: 0.80, blockDuration: Bloque)
                != LiveMicrophoneVerdict.Hold)
            {
                dejoPasar++;
            }
        }

        Assert.True(dejoPasar == 0, $"dejó pasar {dejoPasar} bloques de eco fuerte: la compuerta quedó apagada.");
        Assert.True(compuerta.Bar > 0.80, $"el listón quedó en {compuerta.Bar:0.000}, por debajo del eco medido.");
    }

    [Fact]
    public void CalladaSubeTodoIgualQueSiempre()
    {
        // La otra mitad, y es la que hay que cuidar: el noventa por ciento de la charla ella está
        // callada, y ahí la compuerta no existe.
        var compuerta = ConEcoMedido(0.30);

        // Primero se drena la cola: el eco no se corta cuando se corta el parlante, así que la
        // compuerta sigue puesta unos 200 ms más. Eso es deliberado y no es lo que se prueba acá.
        for (var i = 0; i < 15; i++)
        {
            compuerta.Decide(speakerAudible: false, isVoice: false, level: 0.02, blockDuration: Bloque);
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(
                LiveMicrophoneVerdict.Send,
                compuerta.Decide(speakerAudible: false, isVoice: true, level: 0.05, blockDuration: Bloque));
        }
    }
}
