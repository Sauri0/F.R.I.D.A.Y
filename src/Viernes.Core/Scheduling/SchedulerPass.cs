using Viernes.Core.Persistence;

namespace Viernes.Core.Scheduling;

/// <summary>Lo que una sola inspección del vigía sacó a la superficie.</summary>
/// <remarks>
/// Antes la pasada devolvía sólo la lista de recordatorios, porque era lo único que miraba. Devolver
/// un par nombrado en vez de agregar un segundo método es lo que impide que un host se acuerde de
/// pedir los recordatorios y se olvide de la agenda: hay una sola forma de correr una pasada.
/// </remarks>
public sealed record SchedulerPass(
    IReadOnlyList<Reminder> Reminders,
    IReadOnlyList<AgendaItem> AgendaItems)
{
    /// <summary>Una pasada que no anunció nada.</summary>
    public static SchedulerPass Empty { get; } = new([], []);

    /// <summary>Cuántos avisos se levantaron en total, entre recordatorios y agenda.</summary>
    public int Count => Reminders.Count + AgendaItems.Count;
}
