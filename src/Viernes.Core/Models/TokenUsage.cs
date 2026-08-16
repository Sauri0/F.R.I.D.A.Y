namespace Viernes.Core.Models;

/// <summary>Aggregate token counts only; no prompt or conversation content is retained.</summary>
public readonly record struct TokenUsage(long PromptTokens, long CompletionTokens)
{
    public static TokenUsage Zero => default;

    public long TotalTokens => checked(PromptTokens + CompletionTokens);

    public static TokenUsage operator +(TokenUsage left, TokenUsage right) => new(
        checked(left.PromptTokens + right.PromptTokens),
        checked(left.CompletionTokens + right.CompletionTokens));
}
