namespace Viernes.Core.Models;

/// <summary>Visual and conversational state exposed to the Windows shell.</summary>
public enum AssistantState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Error
}
