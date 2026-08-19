using Viernes.Platform.Windows.Speech.WakeWord;
using Viernes.Platform.Windows.Storage;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El oído que se arma después de renombrar tiene que despertar con el nombre nuevo.
/// </summary>
/// <remarks>
/// Es la mitad del renombrado en caliente que no se puede hacer en el lugar: las frases se le fijan
/// al oído cuando se lo construye —adentro arma con ellas la gramática de SAPI, una sola vez—, así
/// que el shell lo cierra y abre otro. Lo que se comprueba acá es lo que se puede comprobar sin
/// micrófono: que el oído nuevo queda armado con las frases que salen del nombre nuevo. Que además
/// abra el dispositivo y reconozca esa voz necesita alguien hablando y no se prueba desde acá.
/// <para>
/// Sin detector entrenado a propósito: cargarlo no depende del nombre, tarda, y la heurística
/// alcanza para mirar las frases.
/// </para>
/// </remarks>
public sealed class WakeListenerFollowsTheNameTests
{
    private static ContinuousWakeListenerOptions Para(string nombre) => new()
    {
        Phrases = new ViernesLocalSettings { AssistantName = nombre }.EffectiveWakePhrases,
        PreferTrainedVoiceDetector = false,
        CompareVoiceDetectors = false
    };

    [Fact]
    public async Task ElOidoSeArmaConLasFrasesDelNombreElegido()
    {
        await using var oido = new ContinuousWakeListener(Para("Ana"));

        Assert.Equal(["Hola Ana", "Che Ana", "Ey Ana"], oido.Phrases);
    }

    [Fact]
    public async Task DespuesDeRenombrarNoQuedaNadaDelNombreAnterior()
    {
        await using var antes = new ContinuousWakeListener(Para("Viernes"));
        await using var despues = new ContinuousWakeListener(Para("Ana"));

        Assert.Empty(antes.Phrases.Intersect(despues.Phrases, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(despues.Phrases, phrase =>
            phrase.Contains("Viernes", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// Un nombre inválido llega hasta acá igual: entre la ventana que valida y el oído está el
    /// archivo de preferencias, que cualquiera puede editar a mano. Tiene que quedar un oído que
    /// escuche algo, no uno con frases rotas.
    /// </remarks>
    [Fact]
    public async Task UnNombreQueNoSirveDejaElOidoConElDeFabrica()
    {
        await using var oido = new ContinuousWakeListener(Para("R2D2"));

        Assert.Equal(["Hola Viernes", "Che Viernes", "Ey Viernes"], oido.Phrases);
    }
}
