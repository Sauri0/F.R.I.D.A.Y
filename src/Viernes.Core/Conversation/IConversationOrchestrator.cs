using Viernes.Core.Models;
using Viernes.Core.Tools;
using System.Text.Json;

namespace Viernes.Core.Conversation;

public interface IConversationOrchestrator
{
    AssistantState CurrentState { get; }

    event EventHandler<AssistantStateChangedEventArgs>? StateChanged;

    Task<ConversationTurnResult> ProcessAsync(
        string input,
        CancellationToken cancellationToken = default);

    Task<ToolExecutionResult> ConfirmToolAsync(
        string toolCallId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a user/UI-issued command without a model. It still passes through the complete tool
    /// policy and therefore cannot bypass confirmation or high-risk blocking.
    /// </summary>
    Task<ToolExecutionResult> ExecuteLocalToolAsync(
        string toolName,
        JsonElement arguments,
        bool confirmationGranted = false,
        CancellationToken cancellationToken = default);

    void SetListening(bool isListening);

    void ClearHistory();
}
