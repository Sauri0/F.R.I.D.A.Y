namespace Viernes.Memory.Models;

public sealed record ObservationCaptureResult(
    ObservationCaptureStatus Status,
    TemporaryObservation? Observation = null)
{
    public bool WasStored => Status is ObservationCaptureStatus.Captured or ObservationCaptureStatus.Refreshed;
}
