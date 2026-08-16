using Viernes.Core.Tools;

namespace Viernes.Core.Models;

/// <summary>Complete result of processing one user utterance.</summary>
public sealed record ConversationTurnResult(
    string Text,
    AssistantState State,
    bool IsLocalMode,
    IReadOnlyList<ToolExecutionResult> ToolResults,
    string? Model = null,
    TokenUsage Usage = default,
    UsageCost Cost = default)
{
    public bool NeedsConfirmation => ToolResults.Any(result =>
        result.Status == ToolExecutionStatus.NeedsConfirmation);
}
