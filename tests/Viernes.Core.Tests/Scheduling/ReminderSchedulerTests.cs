using Viernes.Core.Persistence;
using Viernes.Core.Scheduling;
using Xunit;

namespace Viernes.Core.Tests.Scheduling;

public sealed class ReminderSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PollOnceAsync_RaisesReminderThatReachedItsDueTime()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Llamar a Ana", Now.AddMinutes(-2));
        var scheduler = CreateScheduler(store, out var raised);

        var fired = await scheduler.PollOnceAsync();

        Assert.Equal(reminder.Id, Assert.Single(fired).Id);
        Assert.Equal("Llamar a Ana", Assert.Single(raised).Reminder.Title);
    }

    [Fact]
    public async Task PollOnceAsync_IgnoresReminderStillInTheFuture()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Tomar mate", Now.AddMinutes(5));
        var scheduler = CreateScheduler(store, out var raised);

        Assert.Empty(await scheduler.PollOnceAsync());
        Assert.Empty(raised);
    }

    [Fact]
    public async Task PollOnceAsync_DoesNotRepeatAnAlreadySurfacedReminder()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Revisar el horno", Now.AddMinutes(-1));
        var scheduler = CreateScheduler(store, out var raised);

        await scheduler.PollOnceAsync();
        var second = await scheduler.PollOnceAsync();

        Assert.Empty(second);
        Assert.Single(raised);
    }

    [Fact]
    public async Task PollOnceAsync_StampsStaleRemindersWithoutAlerting()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Recordatorio de la semana pasada", Now.AddDays(-6));
        var scheduler = CreateScheduler(store, out var raised);

        Assert.Empty(await scheduler.PollOnceAsync());
        Assert.Empty(raised);

        // Stamped, so a later pass stays quiet too.
        Assert.Empty(await scheduler.PollOnceAsync());
        var stored = Assert.Single(await store.GetRemindersAsync());
        Assert.NotNull(stored.NotifiedAt);
    }

    [Fact]
    public async Task PollOnceAsync_LimitsHowManyAlertsASinglePassRaises()
    {
        var store = new InMemoryUserDataStore();
        for (var index = 0; index < 6; index++)
        {
            await store.AddReminderAsync($"Pendiente {index}", Now.AddMinutes(-index - 1));
        }

        var scheduler = CreateScheduler(store, out var raised, new ReminderSchedulerOptions
        {
            MaxAlertsPerPass = 2
        });

        Assert.Equal(2, (await scheduler.PollOnceAsync()).Count);
        Assert.Equal(2, raised.Count);

        // Everything due was stamped, so the backlog does not leak into the following pass.
        Assert.Empty(await scheduler.PollOnceAsync());
    }

    [Fact]
    public async Task PollOnceAsync_ReportsLatenessForAnOverdueReminder()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Reunión", Now.AddMinutes(-45));
        var scheduler = CreateScheduler(store, out var raised);

        await scheduler.PollOnceAsync();

        var alert = Assert.Single(raised);
        Assert.True(alert.IsLate);
        Assert.Equal(TimeSpan.FromMinutes(45), alert.Lateness);
    }

    [Fact]
    public async Task PollOnceAsync_SkipsCompletedReminders()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Ya hecho", Now.AddMinutes(-3));
        await store.MarkReminderNotifiedAsync(reminder.Id, Now.AddMinutes(-2));
        var scheduler = CreateScheduler(store, out var raised);

        Assert.Empty(await scheduler.PollOnceAsync());
        Assert.Empty(raised);
    }

    [Fact]
    public async Task MarkReminderNotifiedAsync_IsIdempotent()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Una vez", Now);

        Assert.True(await store.MarkReminderNotifiedAsync(reminder.Id, Now));
        Assert.False(await store.MarkReminderNotifiedAsync(reminder.Id, Now));
        Assert.False(await store.MarkReminderNotifiedAsync(Guid.NewGuid(), Now));
    }

    [Fact]
    public async Task ReminderDue_HandlerFailureDoesNotStopTheRemainingReminders()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Primero", Now.AddMinutes(-3));
        await store.AddReminderAsync("Segundo", Now.AddMinutes(-2));

        var scheduler = new ReminderScheduler(store, timeProvider: new FixedTimeProvider(Now));
        var seen = new List<string>();
        scheduler.ReminderDue += (_, _) => throw new InvalidOperationException("El shell falló.");
        scheduler.ReminderDue += (_, args) => seen.Add(args.Reminder.Title);

        var fired = await scheduler.PollOnceAsync();

        Assert.Equal(2, fired.Count);
        Assert.Equal(["Primero", "Segundo"], seen);
    }

    private static ReminderScheduler CreateScheduler(
        IUserDataStore store,
        out List<ReminderDueEventArgs> raised,
        ReminderSchedulerOptions? options = null)
    {
        var scheduler = new ReminderScheduler(store, options, new FixedTimeProvider(Now));
        var collected = new List<ReminderDueEventArgs>();
        scheduler.ReminderDue += (_, args) => collected.Add(args);
        raised = collected;
        return scheduler;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
