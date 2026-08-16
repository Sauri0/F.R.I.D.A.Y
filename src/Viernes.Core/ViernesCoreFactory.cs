using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
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
        IPcActionExecutor? pcActions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= ViernesOptions.FromEnvironment();
        dataStore ??= new JsonUserDataStore();

        var tools = new ToolExecutor(
            BuiltInTools.Create(dataStore, pcActions, options.WebSearchEnabled),
            new SafeToolPolicy());
        IChatCompletionClient chatClient = new OpenRouterChatClient(httpClient, options);
        if (usageLedger is not null)
        {
            chatClient = new UsageTrackingChatCompletionClient(chatClient, usageLedger);
        }

        return new ConversationOrchestrator(chatClient, tools, options);
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
