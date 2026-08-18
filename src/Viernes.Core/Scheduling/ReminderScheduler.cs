using Viernes.Core.Persistence;

namespace Viernes.Core.Scheduling;

/// <summary>
/// Turns stored reminders and agenda items into a signal the shell can surface. It owns no UI, sends
/// nothing over the network and reads only the local data store; the host decides how an alert is
/// presented.
/// </summary>
/// <remarks>
/// A reminder is stamped as notified <em>before</em> the event is raised. A crash between the stamp
/// and the alert loses one notification, which is preferable to replaying the same alert on every
/// restart; the reminder itself stays visible through <c>/recordatorios</c>.
/// <para>
/// La agenda pasa por exactamente el mismo camino desde que existe <c>AgendaItem.NotifiedAt</c>.
/// Antes esta clase sólo llamaba a <c>GetRemindersAsync</c>, así que un evento anotado para las
/// 15:30 llegaba a las 15:30 y no pasaba nada: la agenda se podía escribir y leer, pero no avisaba.
/// El nombre de la clase quedó por compatibilidad; lo que hace es vigilar las dos cosas.
/// </para>
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

    /// <summary>Raised once per agenda item, on the polling thread. Handlers must not block.</summary>
    public event EventHandler<AgendaItemDueEventArgs>? AgendaItemDue;

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
    /// Runs a single inspection over reminders and agenda. Exposed so hosts can force a pass right
    /// after writing something and so the behaviour is testable without waiting on a timer.
    /// </summary>
    public async Task<SchedulerPass> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var reminders = await RaiseDueRemindersAsync(now, _options.MaxAlertsPerPass, cancellationToken)
                .ConfigureAwait(false);

            // El techo de avisos por pasada se reparte entre las dos fuentes en vez de aplicarse a
            // cada una: si no, un lunes con seis pendientes y tres reuniones podía escupir el doble
            // del máximo que dice la opción.
            var agenda = await RaiseDueAgendaItemsAsync(
                now,
                _options.MaxAlertsPerPass - reminders.Count,
                cancellationToken).ConfigureAwait(false);

            return reminders.Count == 0 && agenda.Count == 0
                ? SchedulerPass.Empty
                : new SchedulerPass(reminders, agenda);
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async Task<IReadOnlyList<Reminder>> RaiseDueRemindersAsync(
        DateTimeOffset now,
        int budget,
        CancellationToken cancellationToken)
    {
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

            if (!withinGrace || raised.Count >= budget)
            {
                continue;
            }

            raised.Add(reminder);
            Raise(ReminderDue, new ReminderDueEventArgs(reminder, lateness));
        }

        return raised;
    }

    private async Task<IReadOnlyList<AgendaItem>> RaiseDueAgendaItemsAsync(
        DateTimeOffset now,
        int budget,
        CancellationToken cancellationToken)
    {
        var items = await _store.GetAgendaItemsAsync(cancellationToken).ConfigureAwait(false);
        var due = items
            .Where(item => item.NotifiedAt is null && item.StartsAt <= now)
            .OrderBy(item => item.StartsAt)
            .ToArray();

        var raised = new List<AgendaItem>();
        foreach (var item in due)
        {
            var lateness = now - item.StartsAt;

            // Un evento que ya terminó se estampa sin anunciar aunque entre en la ventana de gracia:
            // avisar de una reunión de una hora cuando hace veinte minutos que se acabó no es un
            // aviso atrasado, es ruido.
            var worthAnnouncing = lateness <= _options.LateGrace &&
                                  (item.EndsAt is null || item.EndsAt > now);

            if (!await _store.MarkAgendaItemNotifiedAsync(item.Id, now, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (!worthAnnouncing || raised.Count >= budget)
            {
                continue;
            }

            raised.Add(item);
            Raise(AgendaItemDue, new AgendaItemDueEventArgs(item, lateness));
        }

        return raised;
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

    /// <summary>
    /// Avisa a cada suscriptor por separado. Genérico porque recordatorios y agenda necesitan
    /// exactamente la misma garantía y duplicarla era duplicar también el <c>catch</c> que la sostiene.
    /// </summary>
    private void Raise<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // A presentation handler must not stop the remaining alerts in this pass.
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
