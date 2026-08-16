using Viernes.Core.Persistence;

namespace Viernes.Core.Scheduling;

/// <summary>A reminder that reached its due time and has not been surfaced before.</summary>
public sealed class ReminderDueEventArgs(Reminder reminder, TimeSpan lateness) : EventArgs
{
    public Reminder Reminder { get; } = reminder ?? throw new ArgumentNullException(nameof(reminder));

    /// <summary>How long the reminder had been due when it was raised. Never negative.</summary>
    public TimeSpan Lateness { get; } = lateness > TimeSpan.Zero ? lateness : TimeSpan.Zero;

    /// <summary>True when the alert did not arrive within the first minute after the due time.</summary>
    public bool IsLate => Lateness > TimeSpan.FromMinutes(1);
}
