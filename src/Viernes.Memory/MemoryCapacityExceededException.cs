namespace Viernes.Memory;

public sealed class MemoryCapacityExceededException : InvalidOperationException
{
    public MemoryCapacityExceededException(string message)
        : base(message)
    {
    }
}
