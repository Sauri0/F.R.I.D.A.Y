namespace Viernes.Memory.Privacy;

public sealed class MemoryContentRejectedException : ArgumentException
{
    public MemoryContentRejectedException(
        MemoryContentRejectionReason reason,
        string message,
        string? parameterName = null)
        : base(message, parameterName)
    {
        Reason = reason;
    }

    public MemoryContentRejectionReason Reason { get; }
}
