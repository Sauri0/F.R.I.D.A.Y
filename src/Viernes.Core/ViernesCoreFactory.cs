using Viernes.Core.Configuration;
using Viernes.Core.Awareness;
using Viernes.Core.Conversation;
using Viernes.Core.Goals;
using Viernes.Core.Learning;
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
        GoalBook? goals = null)
    {
        rules ??= new RuleBook();
        goals ??= new GoalBook();
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
            goals);
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
            goals);
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
