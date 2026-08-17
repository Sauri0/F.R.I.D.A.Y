using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// PC capability preview. It never invokes the operating system. Benign previews require consent;
/// sensitive/destructive requests stay permanently pending under <see cref="SafeToolPolicy"/>.
/// </summary>
public sealed class PcActionTool : IAssistantTool
{
    public const string ToolName = "pc_action";

    private readonly IPcActionExecutor? _executor;
    private readonly bool _confirmActions;

    /// <summary>
    /// Sin ejecutor la herramienta previsualiza y nada más. Con ejecutor, las acciones previsualizables
    /// se ejecutan de verdad —pero recién después de que la política las haya dejado pasar.
    /// </summary>
    public PcActionTool(IPcActionExecutor? executor = null, bool confirmActions = false)
    {
        _executor = executor;
        _confirmActions = confirmActions;
        Definition = BuildDefinition();
    }

    private static readonly HashSet<string> DestructiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_file", "delete_folder", "format_disk", "wipe", "uninstall"
    };

    private static readonly HashSet<string> SensitiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "restart", "logoff", "lock", "kill_process", "run_command", "change_setting"
    };

    private static readonly HashSet<string> PreviewableActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "open_settings", "open_application", "show_desktop",
        "media_control", "volume", "focus_application", "close_application", "search_web",
        "play_music", "minimize_application", "restore_application", "lock_screen",
        "see_screen", "move_cursor", "click", "double_click", "right_click",
        "type_text", "press_key", "scroll",
        "read_controls", "click_control", "set_text", "undo", "what_did_you_do"
    };

    public ToolDefinition Definition { get; }

    /// <summary>
    /// La descripción tiene que decir la verdad de lo que la herramienta hace hoy: mientras dijo
    /// «previsualiza, no ejecuta», el modelo asumía que no podía actuar y ni siquiera la ofrecía.
    /// </summary>
    private ToolDefinition BuildDefinition() => ToolDefinition.Create(
        ToolName,
        _executor is null
            ? "Previsualiza una acción de PC. No ejecuta comandos ni cambios reales."
            : "Controla Windows de verdad, tras confirmación del usuario. Acciones y su target: " +
              "open_application abre CUALQUIER aplicación instalada —target: su nombre común, por " +
              "ejemplo «spotify», «steam», «visual studio code», «discord»—. " +
              "focus_application trae al frente una ventana ya abierta —target: nombre de la app—. " +
              "close_application le pide a una app que se cierre —target: nombre de la app—. " +
              "minimize_application minimiza UNA ventana y restore_application la vuelve a abrir " +
              "—target: nombre de la app—; para minimizar una sola cosa usá ésta, nunca " +
              "show_desktop, que minimiza absolutamente todo. " +
              "play_music NO reproduce nada: sólo abre Spotify con una búsqueda y queda esperando " +
              "que alguien le dé play a mano. Si tenés herramientas spotify_, usá ésas para poner " +
              "música; play_music es el último recurso cuando no hay ninguna. " +
              "media_control maneja lo que YA se está reproduciendo —target: play, pause, next, " +
              "previous, stop—. " +
              "volume ajusta el audio —target: up, down, mute—. " +
              "search_web abre el navegador con una búsqueda —target: qué buscar—. " +
              "open_settings abre Configuración —target: sonido, pantalla, bluetooth, red, wifi, " +
              "batería, micrófono, privacidad, aplicaciones, notificaciones, inicio—. " +
              "show_desktop muestra el escritorio. lock_screen bloquea la sesión. " +
              "undo deshace lo último reversible que hiciste —«deshacé eso», «volvé atrás»—. " +
              "what_did_you_do enumera lo que hiciste recién, sin cambiar nada. " +
              "PARA OPERAR DENTRO DE UNA APLICACIÓN, EN ESTE ORDEN: " +
              "1) read_controls —target: nombre de la ventana— te lista sus controles por nombre; " +
              "2) click_control —target «ventana|control»— activa uno; " +
              "3) set_text —target «campo|texto»— escribe en un campo. " +
              "Esta vía es exacta y no se rompe si cambia la resolución o el tema. Usala primero. " +
              "SÓLO si la aplicación no expone controles —juegos, lienzos, cosas dibujadas a mano— " +
              "caé en la vía visual: " +
              "see_screen TE MUESTRA la pantalla —target vacío para todo, «ventana» para la de " +
              "adelante—, y recién ahí move_cursor, click, double_click y right_click con target " +
              "«x,y» leído de esa captura; click sin target usa donde está el cursor. " +
              "type_text escribe el target tal cual. press_key aprieta una tecla —enter, tab, " +
              "escape, suprimir, arriba, abajo, f5—. scroll con target «arriba» o «abajo». " +
              "No hay shell, comandos arbitrarios, borrado ni apagado.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["action"] = ToolSchemas.String(
                    "read_controls, click_control, set_text, open_application, focus_application, " +
                    "close_application, minimize_application, restore_application, play_music, " +
                    "media_control, volume, search_web, open_settings, show_desktop, see_screen, " +
                    "move_cursor, click, double_click, right_click, type_text, press_key, scroll " +
                    "o lock_screen."),
                ["target"] = ToolSchemas.String("Destino, según la acción; ver la descripción.")
            },
            ["action"]),
        // El riesgo real lo decide AssessRisk por acción, que es lo que consulta la política: acá
        // sólo se declara el piso. Dejarlo distinto era prometer una barrera que nadie aplicaba.
        ToolRiskLevel.RequiresConfirmation);

    /// <summary>
    /// Lo que no está en la lista blanca no pasa nunca. Lo que sí está, según la preferencia.
    /// </summary>
    /// <remarks>
    /// La lista blanca es la barrera dura y no se negocia: no hay shell, ni comandos arbitrarios, ni
    /// borrado, ni apagado. La confirmación es la barrera blanda, y ahí no hay una respuesta
    /// correcta: pedir permiso por cada cosa vuelve inservible una charla hablada, y no pedirlo deja
    /// que un texto leído de la web pueda disparar una acción. Por eso es una preferencia y no una
    /// decisión tomada de antemano.
    /// </remarks>
    public ToolRiskLevel AssessRisk(JsonElement arguments)
    {
        var action = JsonToolArguments.RequiredString(arguments, "action", 80);
        if (DestructiveActions.Contains(action))
        {
            return ToolRiskLevel.Destructive;
        }

        if (SensitiveActions.Contains(action) || !PreviewableActions.Contains(action))
        {
            return ToolRiskLevel.Sensitive;
        }

        return _confirmActions ? ToolRiskLevel.RequiresConfirmation : ToolRiskLevel.Safe;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "action", 80);
        var target = JsonToolArguments.OptionalString(arguments, "target", 260);

        // Doble llave: aunque llegue hasta acá, sólo se ejecuta lo previsualizable. Las acciones
        // sensibles y destructivas ya fueron detenidas por la política y nunca alcanzan esta línea.
        if (_executor is null ||
            !PreviewableActions.Contains(action) ||
            !_executor.SupportedActions.Contains(action.ToLowerInvariant()))
        {
            // Falla, no éxito. El estado es lo que viaja al modelo, y un «Succeeded» sobre algo que
            // no pasó lo lleva a contestar que lo hizo. Las dos listas de acciones permitidas se
            // mantienen a mano por separado, así que desincronizarlas dispara justo este camino:
            // conviene que se note como error y no como una acción cumplida.
            return ToolExecutionResult.Failure(
                context.ToolCallId,
                ToolName,
                _executor is null
                    ? $"No puedo hacer «{action}» en esta PC: no tengo el control del sistema conectado."
                    : $"No sé hacer «{action}». No modifiqué nada.");
        }

        var outcome = await _executor.ExecuteAsync(action, target, cancellationToken).ConfigureAwait(false);
        if (!outcome.Executed)
        {
            return ToolExecutionResult.Failure(context.ToolCallId, ToolName, outcome.Message);
        }

        // Mirar la pantalla devuelve una imagen, no un dato: el orquestador la adjunta al turno para
        // que el modelo la vea antes de decidir dónde hacer clic.
        return outcome.ImageDataUrl is { Length: > 0 } image
            ? ToolExecutionResult.SuccessWithImage(context.ToolCallId, ToolName, outcome.Message, image)
            : ToolExecutionResult.Success(
                context.ToolCallId,
                ToolName,
                outcome.Message,
                new { action, target, simulated = false });
    }
}
