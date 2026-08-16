using Viernes.Memory.Privacy;

namespace Viernes.Memory.Models;

public sealed record MemoryReview(
    IReadOnlyList<ExplicitMemory> Explicit,
    IReadOnlyList<TemporaryObservation> TemporaryObservations,
    IReadOnlyList<MemorySuggestion> Suggestions,
    bool IsObservationPaused,
    DateTimeOffset ReviewedAt)
{
    public int TotalCount => Explicit.Count + TemporaryObservations.Count + Suggestions.Count;

    public string PrivacyNotice => MemoryPrivacy.Notice;
}
