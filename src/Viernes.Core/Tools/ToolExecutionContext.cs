namespace Viernes.Core.Tools;

/// <summary>Explicit consent and timing context for a single execution attempt.</summary>
public sealed record ToolExecutionContext(
    string ToolCallId,
    bool ConfirmationGranted = false,
    DateTimeOffset? RequestedAt = null)
{
    public DateTimeOffset EffectiveRequestedAt => RequestedAt ?? DateTimeOffset.Now;
}
