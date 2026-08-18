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

    /// <summary>
    /// Da un recordatorio por hecho. Devuelve false si no existe o si ya estaba completado, así que
    /// repetir la llamada es inofensivo.
    /// </summary>
    /// <remarks>
    /// <c>Reminder.IsCompleted</c> estaba declarado y lo leía el vigía para no avisar de algo ya
    /// hecho, pero no había forma de escribirlo: la marca nunca podía volverse true y completar un
    /// recordatorio era imposible desde la aplicación.
    /// </remarks>
    Task<bool> CompleteReminderAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra un recordatorio. Devuelve false si no existe, para que borrar dos veces no falle.
    /// </summary>
    /// <remarks>
    /// A diferencia de <see cref="CompleteReminderAsync"/>, esto lo saca del archivo: completar deja
    /// el rastro de algo que se hizo, borrar es para lo que nunca tendría que haber estado anotado.
    /// </remarks>
    Task<bool> DeleteReminderAsync(
        Guid reminderId,
        CancellationToken cancellationToken = default);

    Task<AgendaItem> AddAgendaItemAsync(
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset? endsAt = null,
        string? notes = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgendaItem>> GetAgendaItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Estampa un evento de agenda como ya anunciado. Mismo contrato que
    /// <see cref="MarkReminderNotifiedAsync"/>: false cuando no existe o cuando ya estaba estampado.
    /// </summary>
    Task<bool> MarkAgendaItemNotifiedAsync(
        Guid agendaItemId,
        DateTimeOffset notifiedAt,
        CancellationToken cancellationToken = default);
}
