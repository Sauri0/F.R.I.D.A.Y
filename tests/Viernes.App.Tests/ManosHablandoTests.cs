using System.Net.Http;
using System.Text.Json;
using Viernes.App.Services;
using Viernes.Core;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Live;
using Xunit;

namespace Viernes.App.Tests;

/// <summary>
/// Las manos de la sesión hablada: que existan, que sean las mismas que se anuncian, y que lo que no
/// está en la lista no se ejecute.
/// </summary>
/// <remarks>
/// El síntoma que esto viene a cerrar es de los que no se ven leyendo el código: el usuario le
/// hablaba, le pedía que abriera una aplicación, y la asistente le contestaba que no podía. Era
/// cierto —hablando no había ninguna herramienta declarada— y estaba dicho en su instrucción de
/// sistema, con treinta herramientas cargadas del otro lado.
/// <para>
/// <b>Lo que estas pruebas NO cubren, dicho derecho:</b> que el servidor de Google acepte el setup
/// con las declaraciones y pida las herramientas de verdad. Eso pide una clave, red y hablarle. Acá
/// se fija lo que sí se puede fijar sin nada de eso: que la lista no quede vacía por un renombre,
/// que el texto que la anuncia diga lo mismo que la lista, y que la puerta esté cerrada para lo que
/// no está permitido.
/// </para>
/// </remarks>
public sealed class ManosHablandoTests
{
    private static ConversationOrchestrator Armar() =>
        ViernesCoreFactory.CreateDefault(
            new HttpClient(),
            new ViernesOptions(apiKey: null));

    [Fact]
    public void LasTresHerramientasDeclaradasExistenDeVerdad()
    {
        // Es una lista de nombres escritos a mano contra un ejecutor que se arma en otro lado: si
        // alguna se renombra, acá la lista queda vacía en silencio y hablando vuelve a no tener
        // manos, sin que nada falle ni se registre.
        var bridge = new LiveToolBridge(Armar);

        var nombres = bridge.Declarations.Select(definition => definition.Name).ToArray();

        Assert.Equal(LiveToolBridge.Allowed.Length, nombres.Length);
        foreach (var permitida in LiveToolBridge.Allowed)
        {
            Assert.Contains(permitida, nombres);
        }
    }

    [Fact]
    public void ElAnuncioNombraATodasLasPermitidas()
    {
        // Las dos mitades tienen que decir lo mismo. Declarar una herramienta sin nombrarla deja al
        // modelo creyendo que no la tiene; nombrarla sin declararla lo hace prometer algo que no
        // puede. Las dos terminan en una asistente que miente sobre sí misma.
        foreach (var permitida in LiveToolBridge.Allowed)
        {
            Assert.Contains(permitida, LiveToolBridge.Anuncio, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoQueNoEstaEnLaListaNoSeEjecutaAunqueElServidorLoPida()
    {
        // La lista blanca es las dos cosas: lo que se declara y lo que se deja pasar. Confiar sólo
        // en la declaración es confiar en que el otro lado nunca pida de más.
        var bridge = new LiveToolBridge(Armar);

        var resultado = await bridge.InvokeAsync(
            new LiveFunctionCall("c1", "shell", JsonDocument.Parse("{}").RootElement),
            CancellationToken.None);

        Assert.False(resultado.Succeeded);
        Assert.Contains("por escrito", resultado.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("see_screen")]
    [InlineData("click")]
    [InlineData("move_cursor")]
    public async Task MirarLaPantallaYHacerClicACiegasSeFrenanAntesDeTocarNada(string accion)
    {
        // La captura vuelve como imagen y en la sesión hablada la respuesta de una herramienta viaja
        // como texto: la imagen no tiene por dónde volver. Sin este freno, «see_screen» contestaría
        // «hecho», el modelo creería que vio la pantalla, y haría clic en coordenadas inventadas.
        var bridge = new LiveToolBridge(Armar);

        var resultado = await bridge.InvokeAsync(
            new LiveFunctionCall(
                "c1",
                "pc_action",
                JsonDocument.Parse($$"""{"action":"{{accion}}","target":"200,300"}""").RootElement),
            CancellationToken.None);

        Assert.False(resultado.Succeeded);
        Assert.Contains("pantalla", resultado.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ElControlPorNombreSiPasa()
    {
        // La vía de texto —listar los controles de una ventana por su nombre— funciona hablando
        // igual que escribiendo, y es la que la herramienta pide usar primero. Frenarla junto con
        // las visuales sería sacarle la mano buena por sacarle la mala.
        var bridge = new LiveToolBridge(Armar);

        var resultado = await bridge.InvokeAsync(
            new LiveFunctionCall(
                "c1",
                "pc_action",
                JsonDocument.Parse("""{"action":"read_controls","target":"Bloc de notas"}""").RootElement),
            CancellationToken.None);

        // No se afirma que haya encontrado la ventana —acá no hay ninguna abierta—, sino que el
        // pedido llegó hasta la herramienta en vez de rebotar en la puerta.
        Assert.DoesNotContain("pantalla", resultado.Message, StringComparison.OrdinalIgnoreCase);
    }
}
