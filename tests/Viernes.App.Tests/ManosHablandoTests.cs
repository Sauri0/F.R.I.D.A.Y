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

    /// <summary>
    /// Hablando declara todo lo que hay, igual que por escrito.
    /// </summary>
    /// <remarks>
    /// Eran tres de cuarenta y seis. El motivo escrito era el miedo a que un esquema que el
    /// protocolo no acepta rebotara el setup entero; se probó contra el servidor de verdad, una por
    /// una y todas juntas, y las dieciséis integradas entran. Lo que protege ahora no es una lista
    /// corta sino que el rechazo sea recuperable.
    /// </remarks>
    [Fact]
    public void HablandoDeclaraLoMismoQueEscribiendo()
    {
        var orquestador = Armar();
        var bridge = new LiveToolBridge(() => orquestador);

        Assert.Equal(orquestador.ToolDefinitions.Count, bridge.Declarations.Count);
        Assert.True(bridge.Declarations.Count > LiveToolBridge.Essential.Length);
    }

    /// <summary>
    /// El piso al que caer existe, es más chico, y son herramientas de verdad.
    /// </summary>
    /// <remarks>
    /// Devolver lo mismo que <c>Declarations</c> sería no tener piso: el reintento declararía otra
    /// vez lo que el servidor acaba de rechazar. Y una lista de nombres escritos a mano contra un
    /// ejecutor que se arma en otro lado se vacía sola si alguien renombra una — en silencio, y sin
    /// que nada falle.
    /// </remarks>
    [Fact]
    public void ElPisoAlQueCaerEsMasChicoYExiste()
    {
        var bridge = new LiveToolBridge(Armar);

        var nombres = bridge.EssentialDeclarations.Select(definition => definition.Name).ToArray();

        Assert.Equal(LiveToolBridge.Essential.Length, nombres.Length);
        Assert.True(bridge.EssentialDeclarations.Count < bridge.Declarations.Count);
        foreach (var esencial in LiveToolBridge.Essential)
        {
            Assert.Contains(esencial, nombres);
        }
    }

    /// <summary>
    /// El anuncio no le dice que no puede hacer algo que sí puede.
    /// </summary>
    /// <remarks>
    /// Es la forma que tomó la prueba vieja, que verificaba que el anuncio nombrara a cada permitida.
    /// Con cuarenta y seis herramientas enumerarlas es absurdo, pero el error que aquella cuidaba
    /// sigue estando y es el peor de todos: <b>una asistente que miente sobre sí misma</b>. El texto
    /// decía «lo que hablando NO tenés es leer o escribir archivos, buscar en tu memoria, los
    /// servidores conectados», y las tres cosas pasaron a ser falsas.
    /// </remarks>
    [Fact]
    public void ElAnuncioNoNiegaLoQueSiPuede()
    {
        var bridge = new LiveToolBridge(Armar);
        var tiene = bridge.Declarations.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("archivo", tiene);
        Assert.Contains("web_search", tiene);

        // Lo único que de verdad no puede hablando es mirar la pantalla, y eso sí tiene que estar
        // dicho: si no lo dijera, intentaría hacer clic a ciegas.
        Assert.Contains("pantalla", LiveToolBridge.Anuncio, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO tenés es el resto del taller", LiveToolBridge.Anuncio, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnNombreInventadoNoSeEjecutaAunqueElServidorLoPida()
    {
        // La puerta sigue existiendo aunque ya no recorte: lo que se ejecuta es lo que el ejecutor
        // tiene, no lo que el servidor diga. Confiar sólo en la declaración es confiar en que el
        // otro lado nunca pida de más.
        var bridge = new LiveToolBridge(Armar);

        var resultado = await bridge.InvokeAsync(
            new LiveFunctionCall("c1", "shell", JsonDocument.Parse("{}").RootElement),
            CancellationToken.None);

        Assert.False(resultado.Succeeded);
        Assert.Contains("ninguna herramienta con ese nombre", resultado.Message, StringComparison.OrdinalIgnoreCase);
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
