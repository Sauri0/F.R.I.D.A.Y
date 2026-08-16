namespace Viernes.Memory.Models;

public sealed record MemoryDeletionSummary(
    int ExplicitDeleted,
    int TemporaryObservationsDeleted,
    int SuggestionsDeleted)
{
    public int TotalDeleted => ExplicitDeleted + TemporaryObservationsDeleted + SuggestionsDeleted;
}
