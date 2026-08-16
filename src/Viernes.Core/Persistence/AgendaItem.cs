namespace Viernes.Core.Persistence;

public sealed record AgendaItem(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt,
    string? Notes = null);
