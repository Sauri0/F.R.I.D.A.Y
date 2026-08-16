using Viernes.Core.Conversation;

namespace Viernes.App.ViewModels;

/// <summary>Un paso del turno listo para pintar: sin lógica, sólo el estado que decide el color.</summary>
internal sealed class TurnStepViewModel(TurnStep step)
{
    public string Label { get; } = step.Label;

    public TurnStepStatus Status { get; } = step.Status;

    public bool IsRunning => Status == TurnStepStatus.Running;

    public bool IsDone => Status == TurnStepStatus.Done;

    public bool IsBlocked => Status == TurnStepStatus.Blocked;
}
