namespace Viernes.Core.Persistence;

/// <summary>
/// A local reminder. <paramref name="NotifiedAt"/> is stamped once the host has actually surfaced
/// the reminder to the user, so a restart never replays an alert that already fired.
/// </summary>
public sealed record Reminder(
    Guid Id,
    string Title,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    bool IsCompleted = false,
    DateTimeOffset? NotifiedAt = null);
