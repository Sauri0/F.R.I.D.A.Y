namespace Viernes.Memory.Models;

/// <summary>Dato que el usuario pidió recordar de forma explícita.</summary>
public sealed record ExplicitMemory(
    Guid Id,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
    : PersonalMemoryItem(Id, Content, UpdatedAt)
{
    public override PersonalMemoryKind Kind => PersonalMemoryKind.Explicit;

    public override DateTimeOffset RecordedAt => CreatedAt;
}
