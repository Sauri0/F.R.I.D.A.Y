namespace Viernes.Memory;

public sealed class MemoryItemNotFoundException : KeyNotFoundException
{
    public MemoryItemNotFoundException(Guid itemId)
        : base($"No existe una memoria revisable con id {itemId}.")
    {
        ItemId = itemId;
    }

    public Guid ItemId { get; }
}
