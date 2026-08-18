namespace Viernes.Core.Persistence;

public sealed class InMemoryUserDataStore : IUserDataStore
{
    private readonly Lock _gate = new();
    private readonly List<Reminder> _reminders = [];
    private readonly List<AgendaItem> _agendaItems = [];

    public Task<Reminder> AddReminderAsync(
        string title,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTitle(title);
        var reminder = new Reminder(Guid.NewGuid(), title.Trim(), dueAt, DateTimeOffset.Now);
        lock (_gate)
        {
            _reminders.Add(reminder);
        }

        return Task.FromResult(reminder);
    }

    public Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Reminder>>(
                _reminders.OrderBy(item => item.DueAt).ToArray());
        }
    }

    public Task<bool> MarkReminderNotifiedAsync(
        Guid reminderId,
        DateTimeOffset notifiedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var index = _reminders.FindIndex(item => item.Id == reminderId);
            if (index < 0 || _reminders[index].NotifiedAt is not null)
            {
                return Task.FromResult(false);
            }

            _reminders[index] = _reminders[index] with { NotifiedAt = notifiedAt };
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteReminderAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var index = _reminders.FindIndex(item => item.Id == reminderId);
            if (index < 0 || _reminders[index].IsCompleted)
            {
                return Task.FromResult(false);
            }

            // Se estampa también NotifiedAt: un recordatorio que se dio por hecho antes de vencer no
            // tiene por qué sonar después. Sin esto, marcarlo como listo a las tres de la tarde no
            // impedía el aviso de las cuatro.
            _reminders[index] = _reminders[index] with
            {
                IsCompleted = true,
                NotifiedAt = _reminders[index].NotifiedAt ?? DateTimeOffset.Now
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteReminderAsync(Guid reminderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_reminders.RemoveAll(item => item.Id == reminderId) > 0);
        }
    }

    public Task<AgendaItem> AddAgendaItemAsync(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAgenda(title, startsAt, endsAt, notes);
        var item = new AgendaItem(
            Guid.NewGuid(),
            title.Trim(),
            startsAt,
            endsAt,
            DateTimeOffset.Now,
            NormalizeNotes(notes));
        lock (_gate)
        {
            _agendaItems.Add(item);
        }

        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<AgendaItem>> GetAgendaItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AgendaItem>>(
                _agendaItems.OrderBy(item => item.StartsAt).ToArray());
        }
    }

    public Task<bool> MarkAgendaItemNotifiedAsync(
        Guid agendaItemId,
        DateTimeOffset notifiedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var index = _agendaItems.FindIndex(item => item.Id == agendaItemId);
            if (index < 0 || _agendaItems[index].NotifiedAt is not null)
            {
                return Task.FromResult(false);
            }

            _agendaItems[index] = _agendaItems[index] with { NotifiedAt = notifiedAt };
            return Task.FromResult(true);
        }
    }

    internal static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("El título es obligatorio.", nameof(title));
        }

        if (title.Trim().Length > 200)
        {
            throw new ArgumentException("El título no puede superar 200 caracteres.", nameof(title));
        }
    }

    internal static void ValidateAgenda(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt,
        string? notes)
    {
        ValidateTitle(title);
        if (endsAt is not null && endsAt < startsAt)
        {
            throw new ArgumentException("La finalización no puede ser anterior al inicio.", nameof(endsAt));
        }

        if (notes?.Length > 2_000)
        {
            throw new ArgumentException("Las notas no pueden superar 2000 caracteres.", nameof(notes));
        }
    }

    internal static string? NormalizeNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}
