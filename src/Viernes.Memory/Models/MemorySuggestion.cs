namespace Viernes.Memory.Models;

/// <summary>Propuesta pendiente; aprobarla explícitamente la convierte en memoria permanente.</summary>
public sealed record MemorySuggestion(
    Guid Id,
    string Content,
    DateTimeOffset SuggestedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt,
    Guid? BasedOnObservationId = null)
    : PersonalMemoryItem(Id, Content, UpdatedAt)
{
    public override PersonalMemoryKind Kind => PersonalMemoryKind.Suggestion;

    public override DateTimeOffset RecordedAt => SuggestedAt;
}
