namespace Viernes.Memory.Models;

/// <summary>Hecho inferido temporal; nunca se convierte en permanente sin opt-in.</summary>
public sealed record TemporaryObservation(
    Guid Id,
    string Content,
    double Confidence,
    DateTimeOffset ObservedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt)
    : PersonalMemoryItem(Id, Content, UpdatedAt)
{
    public override PersonalMemoryKind Kind => PersonalMemoryKind.TemporaryObservation;

    public override DateTimeOffset RecordedAt => ObservedAt;
}
