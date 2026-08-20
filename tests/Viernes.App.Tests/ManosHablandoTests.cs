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
    public void HablandoDeclaraTodoMenosLoQueEsSoloPorEscrito()
    {
        var orquestador = Armar();
        var bridge = new LiveToolBridge(() => orquestador);

        var hablando = bridge.Declarations.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var escribiendo = orquestador.ToolDefinitions.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);

        escribiendo.ExceptWith(LiveToolBridge.SoloPorEscrito);
        Assert.Equal(escribiendo, hablando);
        Assert.True(bridge.Declarations.Count > LiveToolBridge.Essential.Length);
    }

    /// <summary>
    /// Un comando de PowerShell dictado no se declara ni se ejecuta.
    /// </summary>
    /// <remarks>
    /// Es la única herramienta donde equivocarse no se deshace, y hablando no hay forma de confirmar
    /// nada: el puente ejecuta con <c>confirmationGranted: false</c> y no existe un camino de vuelta
    /// para preguntar. Por escrito la orden se tipea y se lee antes de mandarla; hablando la escribe
    /// un reconocedor de voz a partir de lo que le pareció oír.
    /// </remarks>
    [Fact]
    public async Task ElShellNoSeDeclaraNiSeEjecutaHablando()
    {
        var bridge = new LiveToolBridge(Armar);

        Assert.DoesNotContain(
            bridge.Declarations,
            definition => string.Equals(definition.Name, "comando", StringComparison.Ordinal));

        // Y no alcanza con no declararlo: si el servidor lo pide igual, tampoco corre.
        var resultado = await bridge.InvokeAsync(
            new LiveFunctionCall("c1", "comando", JsonDocument.Parse("""{"comando":"Get-Process"}""").RootElement),
            CancellationToken.None);

        Assert.False(resultado.Succeeded);
        Assert.Contains("de oído", resultado.Message, StringComparison.OrdinalIgnoreCase);
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
    /// Es la forma que tomó la prueba que verificaba que el anuncio nombrara a cada permitida. Con
    /// cuarenta y seis herramientas enumerarlas es absurdo, pero el error que aquella cuidaba sigue
    /// siendo el peor de todos: <b>una asistente que miente sobre sí misma</b>. El texto decía «lo
    /// que hablando NO tenés es leer o escribir archivos, buscar en tu memoria, los servidores
    /// conectados», y las tres cosas pasaron a ser falsas.
    /// <para>
    /// <b>La primera versión de esta prueba no cruzaba nada</b> y una auditoría lo marcó: afirmaba
    /// sobre las herramientas declaradas y buscaba una frase vieja literal, o sea que el anuncio
    /// podía negar cualquier otra capacidad sin que nadie se enterara. Ahora sí cruza: parte el texto
    /// en oraciones, se queda con las que niegan, y verifica que ninguna nombre algo que la asistente
    /// tiene declarado.
    /// </para>
    /// </remarks>
    [Fact]
    public void ElAnuncioNoNiegaLoQueSiPuede()
    {
        var bridge = new LiveToolBridge(Armar);
        var declaradas = bridge.Declarations.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);

        // Cómo se dice en castellano cada cosa que sabe hacer, contra la herramienta que la hace.
        (string Palabra, string Herramienta)[] capacidades =
        [
            ("archivo", "archivo"),
            ("carpeta", "archivo"),
            ("recordatorio", "reminder_create"),
            ("agenda", "agenda_create"),
            ("aplicaci", "pc_action"),
            ("página", "leer_web"),
            ("regla", "aprender"),
        ];

        var niegan = LiveToolBridge.Anuncio
            .Split(['.', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(oracion =>
                oracion.Contains("NO ", StringComparison.Ordinal) ||
                oracion.Contains("no pod", StringComparison.OrdinalIgnoreCase) ||
                oracion.Contains("no tenés", StringComparison.OrdinalIgnoreCase) ||
                oracion.Contains("no puedo", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var oracion in niegan)
        {
            foreach (var (palabra, herramienta) in capacidades)
            {
                if (oracion.Contains(palabra, StringComparison.OrdinalIgnoreCase) && declaradas.Contains(herramienta))
                {
                    Assert.Fail(
                        $"El anuncio niega «{palabra}» pero «{herramienta}» está declarada. " +
                        $"La oración es: «{oracion}»");
                }
            }
        }

        // Y las dos que de verdad no puede tienen que estar dichas, o las va a intentar.
        Assert.Contains("pantalla", LiveToolBridge.Anuncio, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(niegan);
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
