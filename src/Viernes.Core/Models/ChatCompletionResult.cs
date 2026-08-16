namespace Viernes.Core.Models;

/// <summary>Provider-neutral result from one assistant completion.</summary>
public sealed record ChatCompletionResult(
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls,
    string? Model,
    bool IsLocalMode = false,
    TokenUsage Usage = default,
    UsageCost Cost = default,
    string? RequestId = null)
{
    public static ChatCompletionResult LocalMode() =>
        new(null, Array.Empty<ToolCall>(), null, IsLocalMode: true);
}
