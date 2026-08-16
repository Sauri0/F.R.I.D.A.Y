using Viernes.Core.Models;

namespace Viernes.Core.Conversation;

public sealed class AssistantStateChangedEventArgs(
    AssistantState previousState,
    AssistantState currentState) : EventArgs
{
    public AssistantState PreviousState { get; } = previousState;

    public AssistantState CurrentState { get; } = currentState;
}
