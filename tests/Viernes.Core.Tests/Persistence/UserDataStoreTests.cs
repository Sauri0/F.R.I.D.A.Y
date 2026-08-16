using System.Text.Json;
using Viernes.Core.Persistence;
using Xunit;

namespace Viernes.Core.Tests.Persistence;

public sealed class UserDataStoreTests
{
    [Fact]
    public async Task InMemoryStore_NormalizesAndOrdersLocalData()
    {
        var store = new InMemoryUserDataStore();
        var later = new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var earlier = later.AddHours(-2);

        await store.AddReminderAsync("  Más tarde  ", later);
        await store.AddReminderAsync("Antes", earlier);
        await store.AddAgendaItemAsync("  Reunión  ", later, later.AddHours(1), "  notas  ");

        var reminders = await store.GetRemindersAsync();
        var agenda = await store.GetAgendaItemsAsync();

        Assert.Equal(["Antes", "Más tarde"], reminders.Select(item => item.Title));
        var item = Assert.Single(agenda);
        Assert.Equal("Reunión", item.Title);
        Assert.Equal("notas", item.Notes);
    }

    [Fact]
    public async Task InMemoryStore_RejectsInvalidAgendaRangeWithoutStoringIt()
    {
        var store = new InMemoryUserDataStore();
        var startsAt = new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAgendaItemAsync(
            "Evento inválido",
            startsAt,
            startsAt.AddMinutes(-1)));

        Assert.Empty(await store.GetAgendaItemsAsync());
    }

    [Fact]
    public async Task JsonStore_RoundTripsRemindersAndAgendaWithoutConfigurationFields()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Viernes.Core.Tests",
            Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "assistant-data.json");

        try
        {
            var firstStore = new JsonUserDataStore(filePath);
            var dueAt = new DateTimeOffset(2031, 5, 10, 9, 30, 0, TimeSpan.FromHours(-3));
            var reminder = await firstStore.AddReminderAsync("Turno", dueAt);
            var agendaItem = await firstStore.AddAgendaItemAsync(
                "Consulta",
                dueAt.AddDays(1),
                dueAt.AddDays(1).AddMinutes(30),
                "Llevar documentación");

            var reloadedStore = new JsonUserDataStore(filePath);
            var reminders = await reloadedStore.GetRemindersAsync();
            var agenda = await reloadedStore.GetAgendaItemsAsync();

            Assert.Equal(reminder, Assert.Single(reminders));
            Assert.Equal(agendaItem, Assert.Single(agenda));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
            var serialized = document.RootElement.GetRawText();
            Assert.DoesNotContain("apiKey", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("openRouter", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
