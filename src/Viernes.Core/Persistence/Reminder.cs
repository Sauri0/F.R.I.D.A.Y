namespace Viernes.Core.Persistence;

public sealed record Reminder(
    Guid Id,
    string Title,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    bool IsCompleted = false);
