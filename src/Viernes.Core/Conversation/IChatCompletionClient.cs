using Viernes.Core.Models;
using Viernes.Core.Tools;

namespace Viernes.Core.Conversation;

public interface IChatCompletionClient
{
    Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default);
}
