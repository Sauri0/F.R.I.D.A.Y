using Viernes.Core.Persistence;

namespace Viernes.Core.Scheduling;

/// <summary>Un evento de agenda que llegó a su hora de inicio y no se anunció antes.</summary>
public sealed class AgendaItemDueEventArgs(AgendaItem item, TimeSpan lateness) : EventArgs
{
    public AgendaItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));

    /// <summary>Cuánto hacía que había empezado cuando se anunció. Nunca negativo.</summary>
    public TimeSpan Lateness { get; } = lateness > TimeSpan.Zero ? lateness : TimeSpan.Zero;

    /// <summary>True cuando el aviso no llegó dentro del primer minuto desde el inicio.</summary>
    public bool IsLate => Lateness > TimeSpan.FromMinutes(1);
}
