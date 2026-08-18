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

        Assert.Equal(reminder.Id, Assert.Single(fired.Reminders).Id);
        Assert.Equal("Llamar a Ana", Assert.Single(raised).Reminder.Title);
    }

    [Fact]
    public async Task PollOnceAsync_IgnoresReminderStillInTheFuture()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Tomar mate", Now.AddMinutes(5));
        var scheduler = CreateScheduler(store, out var raised);

        Assert.Empty((await scheduler.PollOnceAsync()).Reminders);
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

        Assert.Empty(second.Reminders);
        Assert.Single(raised);
    }

    [Fact]
    public async Task PollOnceAsync_StampsStaleRemindersWithoutAlerting()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Recordatorio de la semana pasada", Now.AddDays(-6));
        var scheduler = CreateScheduler(store, out var raised);

        Assert.Empty((await scheduler.PollOnceAsync()).Reminders);
        Assert.Empty(raised);

        // Stamped, so a later pass stays quiet too.
        Assert.Empty((await scheduler.PollOnceAsync()).Reminders);
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

        Assert.Equal(2, (await scheduler.PollOnceAsync()).Reminders.Count);
        Assert.Equal(2, raised.Count);

        // Everything due was stamped, so the backlog does not leak into the following pass.
        Assert.Empty((await scheduler.PollOnceAsync()).Reminders);
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

        Assert.Empty((await scheduler.PollOnceAsync()).Reminders);
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

        Assert.Equal(2, fired.Reminders.Count);
        Assert.Equal(["Primero", "Segundo"], seen);
    }

    // La agenda no avisaba nunca: el vigía sólo miraba recordatorios y AgendaItem ni siquiera tenía
    // dónde anotar que ya se había anunciado. Estas pruebas fijan que un evento suena una vez, a su
    // hora, y con las mismas reglas de atraso que un recordatorio.

    [Fact]
    public async Task PollOnceAsync_RaisesAgendaItemThatAlreadyStarted()
    {
        var store = new InMemoryUserDataStore();
        var item = await store.AddAgendaItemAsync("Reunión de equipo", Now.AddMinutes(-1), Now.AddMinutes(30));
        var scheduler = CreateScheduler(store, out _, out var agendaAlerts);

        var pass = await scheduler.PollOnceAsync();

        Assert.Equal(item.Id, Assert.Single(pass.AgendaItems).Id);
        Assert.Equal("Reunión de equipo", Assert.Single(agendaAlerts).Item.Title);
    }

    [Fact]
    public async Task PollOnceAsync_IgnoresAgendaItemThatHasNotStartedYet()
    {
        var store = new InMemoryUserDataStore();
        await store.AddAgendaItemAsync("Dentista", Now.AddHours(3));
        var scheduler = CreateScheduler(store, out _, out var agendaAlerts);

        Assert.Empty((await scheduler.PollOnceAsync()).AgendaItems);
        Assert.Empty(agendaAlerts);
    }

    [Fact]
    public async Task PollOnceAsync_DoesNotRepeatAnAlreadyAnnouncedAgendaItem()
    {
        var store = new InMemoryUserDataStore();
        await store.AddAgendaItemAsync("Llamada", Now.AddMinutes(-2));
        var scheduler = CreateScheduler(store, out _, out var agendaAlerts);

        await scheduler.PollOnceAsync();

        Assert.Empty((await scheduler.PollOnceAsync()).AgendaItems);
        Assert.Single(agendaAlerts);
        Assert.NotNull(Assert.Single(await store.GetAgendaItemsAsync()).NotifiedAt);
    }

    [Fact]
    public async Task PollOnceAsync_StampsAnAgendaItemThatAlreadyEndedWithoutAlerting()
    {
        var store = new InMemoryUserDataStore();
        await store.AddAgendaItemAsync("Almuerzo", Now.AddHours(-2), Now.AddHours(-1));
        var scheduler = CreateScheduler(store, out _, out var agendaAlerts);

        Assert.Empty((await scheduler.PollOnceAsync()).AgendaItems);
        Assert.Empty(agendaAlerts);
        Assert.NotNull(Assert.Single(await store.GetAgendaItemsAsync()).NotifiedAt);
    }

    [Fact]
    public async Task PollOnceAsync_SharesTheAlertBudgetBetweenRemindersAndAgenda()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Pendiente", Now.AddMinutes(-5));
        await store.AddAgendaItemAsync("Evento uno", Now.AddMinutes(-4), Now.AddHours(1));
        await store.AddAgendaItemAsync("Evento dos", Now.AddMinutes(-3), Now.AddHours(1));

        var scheduler = CreateScheduler(store, out _, out _, new ReminderSchedulerOptions
        {
            MaxAlertsPerPass = 2
        });

        var pass = await scheduler.PollOnceAsync();

        Assert.Equal(2, pass.Count);
        Assert.Single(pass.Reminders);
        Assert.Single(pass.AgendaItems);

        // Lo que no entró en el presupuesto quedó estampado igual, así que no se acumula.
        Assert.Equal(0, (await scheduler.PollOnceAsync()).Count);
    }

    private static ReminderScheduler CreateScheduler(
        IUserDataStore store,
        out List<ReminderDueEventArgs> raised,
        ReminderSchedulerOptions? options = null) =>
        CreateScheduler(store, out raised, out _, options);

    private static ReminderScheduler CreateScheduler(
        IUserDataStore store,
        out List<ReminderDueEventArgs> raised,
        out List<AgendaItemDueEventArgs> agendaRaised,
        ReminderSchedulerOptions? options = null)
    {
        var scheduler = new ReminderScheduler(store, options, new FixedTimeProvider(Now));
        var collected = new List<ReminderDueEventArgs>();
        var collectedAgenda = new List<AgendaItemDueEventArgs>();
        scheduler.ReminderDue += (_, args) => collected.Add(args);
        scheduler.AgendaItemDue += (_, args) => collectedAgenda.Add(args);
        raised = collected;
        agendaRaised = collectedAgenda;
        return scheduler;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
