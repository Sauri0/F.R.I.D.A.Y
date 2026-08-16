namespace Viernes.Core.Persistence;

/// <summary>Local, non-secret user data required by safe MVP tools.</summary>
public interface IUserDataStore
{
    Task<Reminder> AddReminderAsync(
        string title,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default);

    Task<AgendaItem> AddAgendaItemAsync(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt = null,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgendaItem>> GetAgendaItemsAsync(CancellationToken cancellationToken = default);
}
