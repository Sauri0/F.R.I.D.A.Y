namespace Viernes.Core.Scheduling;

/// <summary>Tuning for <see cref="ReminderScheduler"/>. Every value is local and non-secret.</summary>
public sealed class ReminderSchedulerOptions
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _lateGrace = TimeSpan.FromHours(12);
    private readonly int _maxAlertsPerPass = 3;

    /// <summary>How often the store is inspected. Between one second and fifteen minutes.</summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        init => _pollInterval = value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromMinutes(15)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Use a poll interval between 1s and 15m.");
    }

    /// <summary>
    /// A reminder overdue by more than this is stamped without alerting. It keeps a machine that was
    /// off for a week from replaying every missed reminder at once; the items stay listed in
    /// <c>/recordatorios</c>.
    /// </summary>
    public TimeSpan LateGrace
    {
        get => _lateGrace;
        init => _lateGrace = value >= TimeSpan.Zero && value <= TimeSpan.FromDays(30)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Use a grace window between 0 and 30 days.");
    }

    /// <summary>Upper bound of alerts raised by a single pass, so a backlog never floods the shell.</summary>
    public int MaxAlertsPerPass
    {
        get => _maxAlertsPerPass;
        init => _maxAlertsPerPass = value is >= 1 and <= 20
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Use between 1 and 20 alerts per pass.");
    }
}
