namespace Viernes.Core.Persistence;

/// <summary>Local, non-secret user data required by safe MVP tools.</summary>
public interface IUserDataStore
{
    Task<Reminder> AddReminderAsync(
        string title,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps a reminder as already surfaced. Returns false when the reminder is unknown or was
    /// stamped by an earlier pass, which makes the call safe to repeat.
    /// </summary>
    Task<bool> MarkReminderNotifiedAsync(
        Guid reminderId,
        DateTimeOffset notifiedAt,
        CancellationToken cancellationToken = default);

    Task<AgendaItem> AddAgendaItemAsync(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt = null,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgendaItem>> GetAgendaItemsAsync(CancellationToken cancellationToken = default);
}
