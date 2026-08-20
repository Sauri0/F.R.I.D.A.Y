using Viernes.Core.Configuration;
using Viernes.Core.Autonomy;
using Viernes.Core.Awareness;
using Viernes.Core.Conversation;
using Viernes.Core.Goals;
using Viernes.Core.Learning;
using Viernes.Core.Missions;
using Viernes.Core.OpenRouter;
using Viernes.Core.Persistence;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Viernes.Core.Usage;

namespace Viernes.Core;

/// <summary>Composition root for UI hosts that do not use a dependency-injection container.</summary>
public static class ViernesCoreFactory
{
    public static ConversationOrchestrator CreateDefault(
        HttpClient httpClient,
        ViernesOptions? options = null,
        IUserDataStore? dataStore = null,
        UsageLedger? usageLedger = null,
        IPcActionExecutor? pcActions = null,
        IActionMemory? actionMemory = null,
        IReadOnlyList<IAssistantTool>? extraTools = null,
        IEnvironmentObserver? environment = null,
        RuleBook? rules = null,
        GoalBook? goals = null,
        Func<CancellationToken, Task<string?>>? personalContext = null,
        MissionBook? missions = null,
        AutonomyPolicy? autonomy = null,
        Func<RestDepth, CancellationToken, Task>? rest = null)
    {
        rules ??= new RuleBook();
        goals ??= new GoalBook();

        // Las misiones vienen puestas por defecto, como el recetario: que un encargo sobreviva a
        // cerrar la charla no debería ser algo que haya que acordarse de encender en cada host.
        missions ??= new MissionBook();
        autonomy ??= new AutonomyPolicy();
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= ViernesOptions.FromEnvironment();
        dataStore ??= new JsonUserDataStore();

        // Las herramientas de servidores MCP entran acá, por la misma puerta que las propias: el
        // ejecutor y su política son el único lugar donde se decide si algo se ejecuta.
        var builtIn = BuiltInTools.Create(
            dataStore,
            pcActions,
            options.WebSearchEnabled,
            options.ConfirmActions,
            environment,
            rules,
            goals,
            missions,
            autonomy,
            // Faltaba, y por eso «descansar» nunca descansaba: el host pasaba su callback a la
            // fábrica, la fábrica no lo reenviaba, y la herramienta se construía sin nada que
            // llamar. El modelo entendía perfecto que le pedían parar, invocaba la herramienta y
            // recibía «no tengo control del micrófono en este contexto».
            rest);
        var tools = new ToolExecutor(
            extraTools is { Count: > 0 } ? [.. builtIn, .. extraTools] : builtIn,
            new SafeToolPolicy());
        IChatCompletionClient chatClient = new OpenRouterChatClient(httpClient, options);
        if (usageLedger is not null)
        {
            chatClient = new UsageTrackingChatCompletionClient(chatClient, usageLedger);
        }

        // El recetario viene puesto por defecto: que mejore con el uso no debería ser algo que haya
        // que acordarse de encender en cada host.
        return new ConversationOrchestrator(
            chatClient,
            tools,
            options,
            systemPrompt: null,
            actionMemory ?? new JsonActionMemory(),
            environment,
            rules,
            goals,
            personalContext,
            missions,
            // Faltaba, y por eso los permisos aprendidos no valían para NADA en el camino escrito.
            // La fábrica los recibía, armaba con ellos la herramienta de permisos —o sea que el
            // usuario podía enseñarlos— y después los tiraba: el orquestador, que es quien los mete
            // en el pedido de cada turno, se construía con el suyo en nulo. Se enseñaba un permiso y
            // el modelo nunca se enteraba de que existía.
            autonomy);
    }

    /// <summary>
    /// Creates the optional persistent, content-free accounting ledger. The chat client does not
    /// attach it implicitly: hosts must call its guard and record methods explicitly.
    /// </summary>
    public static UsageLedger CreateUsageLedger(
        ViernesOptions options,
        string? filePath = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new UsageLedger(options.UsageBudgets, options.RateCard, filePath, timeProvider);
    }
}
