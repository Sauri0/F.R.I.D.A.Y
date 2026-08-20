using System.Text.Json;
using Viernes.Core.Conversation;
using Viernes.Core.Live;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;

namespace Viernes.App.Services;

/// <summary>
/// Las manos de la sesión hablada: unas pocas herramientas, las mismas que las del otro camino.
/// </summary>
/// <remarks>
/// Hablando no había ninguna. El usuario le pedía que abriera una aplicación y la asistente le
/// contestaba que en la sesión de voz no podía —era cierto y estaba escrito en su instrucción—, con
/// treinta herramientas cargadas del otro lado. Enterarse así, en el medio de la charla y pidiendo
/// algo, es la peor forma de enterarse.
/// <para>
/// <b>Van tres y no las treinta, a propósito.</b> Cada herramienta declarada es superficie que puede
/// fallar en vivo: un esquema que el otro protocolo no acepta rebota el setup entero y la sesión se
/// cae al camino de siempre sin que nadie entienda por qué. Estas tres cubren lo que se pide
/// hablando —abrí esto, anotámelo, encargate de aquello— y dejan el camino armado: agregar una más
/// es agregarla a <see cref="Allowed"/>.
/// </para>
/// <para>
/// No ejecuta nada por su cuenta. Todo pasa por <see cref="ConversationOrchestrator.ExecuteLocalToolAsync"/>,
/// que es el mismo ejecutor con la misma política que el camino escrito: el permiso de hacer algo no
/// puede depender de por dónde entró el pedido.
/// </para>
/// </remarks>
internal sealed class LiveToolBridge : ILiveToolBridge
{
    /// <summary>
    /// Lo único que la sesión hablada puede pedir.
    /// </summary>
    /// <remarks>
    /// Es lista blanca y no lista negra: lo que no está acá no se declara <em>y</em> no se ejecuta
    /// aunque el servidor lo pida igual. Las dos mitades hacen falta — la declaración es lo que el
    /// modelo ve, y la ejecución es lo que realmente pasa.
    /// </remarks>
    internal static readonly string[] Allowed =
    [
        PcActionTool.ToolName,
        ReminderCreateTool.ToolName,
        MissionTool.ToolName
    ];

    /// <summary>
    /// Cómo se le cuentan estas manos al modelo, para pegar en la instrucción de sistema.
    /// </summary>
    /// <remarks>
    /// Vive acá, al lado de <see cref="Allowed"/>, y no en el texto de la instrucción, porque son la
    /// misma decisión escrita dos veces y separarlas es garantizar que un día digan cosas distintas.
    /// Si se declara una herramienta y no se la nombra, el modelo no la usa —cree que no la tiene—;
    /// si se la nombra y no se declara, promete algo que no puede. Las dos formas de que se
    /// desincronicen terminan en el mismo síntoma: una asistente que miente sobre sí misma.
    /// <para>
    /// Hay una prueba que verifica que este texto nombre a cada una de las permitidas.
    /// </para>
    /// </remarks>
    internal const string Anuncio = """
        Hablando tenés tres manos y son de verdad: «pc_action» maneja esta computadora —abrir una
        aplicación, traerla al frente, cerrarla, el volumen, buscar en la web—, «reminder_create»
        anota un recordatorio con fecha y hora, y «mision» lleva los encargos que duran más que esta
        charla. Si te piden algo que entra ahí, hacelo: no digas que no podés.

        Lo que hablando NO tenés es el resto del taller: leer o escribir archivos, mirar la pantalla,
        buscar en tu memoria, los servidores conectados. Si te piden algo de eso, decilo derecho y
        ofrecé que te lo escriban por el chat, que ahí sí lo tenés todo.

        Cuando uses una herramienta, contá en una frase qué pasó, y contalo según lo que la
        herramienta te contestó: si te dijo que falló, decí que falló. Nunca des por hecho algo que
        no te confirmaron.
        """;

    /// <summary>
    /// Las acciones de <c>pc_action</c> que necesitan ojos, y hablando no hay ojos.
    /// </summary>
    /// <remarks>
    /// Es la única parte de la herramienta que no sobrevive al cambio de camino, y hay que frenarla
    /// acá porque su propia descripción la ofrece: <c>see_screen</c> devuelve una captura, y el
    /// orquestador escrito la adjunta al turno para que el modelo la mire antes de decidir dónde
    /// hacer clic. En la sesión hablada la respuesta de una herramienta viaja como texto y nada más:
    /// la imagen no tiene por dónde volver.
    /// <para>
    /// Sin este freno pasaría lo peor: <c>see_screen</c> contestaría «hecho», el modelo creería que
    /// vio la pantalla, y haría clic en coordenadas inventadas. Sale bien la llamada y sale mal la
    /// computadora.
    /// </para>
    /// <para>
    /// Las de control por nombre —<c>read_controls</c>, <c>click_control</c>, <c>set_text</c>— no
    /// están en esta lista y no es un olvido: ésas hablan por texto de punta a punta, así que
    /// hablando funcionan igual de bien que escribiendo. Es justamente la vía que la herramienta
    /// pide usar primero.
    /// </para>
    /// </remarks>
    private static readonly string[] NecesitanOjos =
    [
        "see_screen", "move_cursor", "click", "double_click", "right_click"
    ];

    /// <summary>
    /// El orquestador se pide cada vez, no se guarda.
    /// </summary>
    /// <remarks>
    /// Se rearma cuando se conectan los servidores MCP, y la sesión en vivo se arma una sola vez y
    /// se reusa toda la vida del programa. Guardarlo acá dejaría a la sesión hablada ejecutando
    /// contra un orquestador viejo — el de antes de conectar nada.
    /// </remarks>
    private readonly Func<ConversationOrchestrator> _orchestrator;

    public LiveToolBridge(Func<ConversationOrchestrator> orchestrator) =>
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Declarations =>
        [.. _orchestrator().ToolDefinitions.Where(
            definition => Array.IndexOf(Allowed, definition.Name) >= 0)];

    /// <inheritdoc />
    public async Task<LiveToolOutcome> InvokeAsync(
        LiveFunctionCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (Array.IndexOf(Allowed, call.Name) < 0)
        {
            return LiveToolOutcome.Failed(
                "Esa herramienta no está disponible hablando. Pedímelo por escrito.");
        }

        if (NecesitaOjos(call))
        {
            return LiveToolOutcome.Failed(
                "Hablando no puedo mirar la pantalla, así que tampoco puedo hacer clic a ciegas. " +
                "Probá con read_controls y click_control, que van por el nombre del control, o " +
                "pedímelo por escrito.");
        }

        var result = await _orchestrator()
            .ExecuteLocalToolAsync(call.Name, call.Arguments, confirmationGranted: false, cancellationToken)
            .ConfigureAwait(false);

        // El estado viaja además del texto: es lo que le permite al modelo distinguir «lo hice» de
        // «me lo frenaron» sin tener que interpretar una frase en castellano.
        return new LiveToolOutcome(result.Status.ToString(), result.Message);
    }

    /// <summary>Si la llamada pide algo que sin ver la pantalla no se puede hacer.</summary>
    private static bool NecesitaOjos(LiveFunctionCall call)
    {
        if (!string.Equals(call.Name, PcActionTool.ToolName, StringComparison.Ordinal) ||
            call.Arguments.ValueKind != JsonValueKind.Object ||
            !call.Arguments.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = action.GetString();
        return value is not null &&
            Array.Exists(NecesitanOjos, name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
    }
}
