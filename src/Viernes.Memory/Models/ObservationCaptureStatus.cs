namespace Viernes.Memory.Models;

public enum ObservationCaptureStatus
{
    Captured = 0,
    Refreshed,
    Paused,
    BelowConfidenceThreshold,
    AlreadyExplicit
}
