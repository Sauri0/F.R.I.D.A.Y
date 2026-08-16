using Viernes.Core.Persistence;

namespace Viernes.Core.Scheduling;

/// <summary>
/// Turns stored reminders into a signal the shell can surface. It owns no UI, sends nothing over the
/// network and reads only the local data store; the host decides how an alert is presented.
/// </summary>
/// <remarks>
/// A reminder is stamped as notified <em>before</em> the event is raised. A crash between the stamp
/// and the alert loses one notification, which is preferable to replaying the same alert on every
/// restart; the reminder itself stays visible through <c>/recordatorios</c>.
/// </remarks>
public sealed class ReminderScheduler : IAsyncDisposable
{
    private readonly IUserDataStore _store;
    private readonly ReminderSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;
    private bool _isDisposed;

    public ReminderScheduler(
        IUserDataStore store,
        ReminderSchedulerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new ReminderSchedulerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised once per reminder, on the polling thread. Handlers must not block.</summary>
    public event EventHandler<ReminderDueEventArgs>? ReminderDue;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (IsRunning)
        {
            return;
        }

        _loopCancellation?.Dispose();
        _loopCancellation = new CancellationTokenSource();
        _loop = RunLoopAsync(_loopCancellation.Token);
    }

    public async Task StopAsync()
    {
        var loop = _loop;
        _loopCancellation?.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during a normal stop.
            }
        }

        _loop = null;
        _loopCancellation?.Dispose();
        _loopCancellation = null;
    }

    /// <summary>
    /// Runs a single inspection. Exposed so hosts can force a pass right after writing a reminder and
    /// so the behaviour is testable without waiting on a timer.
    /// </summary>
    public async Task<IReadOnlyList<Reminder>> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var reminders = await _store.GetRemindersAsync(cancellationToken).ConfigureAwait(false);
            var due = reminders
                .Where(reminder => !reminder.IsCompleted &&
                                   reminder.NotifiedAt is null &&
                                   reminder.DueAt <= now)
                .OrderBy(reminder => reminder.DueAt)
                .ToArray();

            var raised = new List<Reminder>();
            foreach (var reminder in due)
            {
                var lateness = now - reminder.DueAt;
                var withinGrace = lateness <= _options.LateGrace;

                // Stale reminders are stamped silently so a long shutdown cannot flood the shell.
                if (!await _store.MarkReminderNotifiedAsync(reminder.Id, now, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (!withinGrace || raised.Count >= _options.MaxAlertsPerPass)
                {
                    continue;
                }

                raised.Add(reminder);
                RaiseDue(new ReminderDueEventArgs(reminder, lateness));
            }

            return raised;
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _timeProvider);
        do
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A transient store failure must not kill the loop; the next pass retries.
            }
        }
        while (await SafeWaitAsync(timer, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void RaiseDue(ReminderDueEventArgs eventArgs)
    {
        var handlers = ReminderDue;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ReminderDueEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // A presentation handler must not stop the remaining reminders in this pass.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await StopAsync().ConfigureAwait(false);
        _passGate.Dispose();
    }
}
