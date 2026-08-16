namespace Viernes.Core.Configuration;

public sealed record ModelSelection(
    ModelRole Role,
    string? Model,
    ModelSelectionStatus Status,
    string Message)
{
    public bool CanSendRemoteRequest => Status == ModelSelectionStatus.Ready && Model is not null;
}
