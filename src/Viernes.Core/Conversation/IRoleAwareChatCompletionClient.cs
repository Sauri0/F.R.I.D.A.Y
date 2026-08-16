using Viernes.Core.Configuration;
using Viernes.Core.Models;
using Viernes.Core.Tools;

namespace Viernes.Core.Conversation;

/// <summary>
/// Optional explicit-lane extension; it never chooses a role from prompt content. This interface
/// performs model-approval checks, but callers must separately evaluate and record an
/// <see cref="Usage.UsageLedger"/> when budget enforcement is desired.
/// </summary>
public interface IRoleAwareChatCompletionClient : IChatCompletionClient
{
    Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelSelectionRequest selectionRequest,
        CancellationToken cancellationToken = default);
}
