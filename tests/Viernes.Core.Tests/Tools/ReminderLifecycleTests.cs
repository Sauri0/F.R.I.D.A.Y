using System.Text.Json;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Completar y borrar un recordatorio: hasta que existieron, <c>IsCompleted</c> se leía pero no lo
/// escribía nadie y la única forma de sacar algo de la lista era editar el JSON a mano.
/// </summary>
public sealed class ReminderLifecycleTests
{
    private static readonly DateTimeOffset DueAt = new(2026, 8, 20, 9, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task CompleteReminderAsync_MarksItDoneAndIsSafeToRepeat()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Pagar la luz", DueAt);

        Assert.True(await store.CompleteReminderAsync(reminder.Id));

        var stored = Assert.Single(await store.GetRemindersAsync());
        Assert.True(stored.IsCompleted);

        // Un pendiente ya cumplido no vuelve a sonar aunque llegue su hora.
        Assert.NotNull(stored.NotifiedAt);

        Assert.False(await store.CompleteReminderAsync(reminder.Id));
        Assert.False(await store.CompleteReminderAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteReminderAsync_RemovesItAndIsSafeToRepeat()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Anotado por error", DueAt);

        Assert.True(await store.DeleteReminderAsync(reminder.Id));
        Assert.Empty(await store.GetRemindersAsync());
        Assert.False(await store.DeleteReminderAsync(reminder.Id));
    }

    [Fact]
    public async Task JsonStore_KeepsCompletionAndDeletionAcrossReloads()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Viernes.Core.Tests",
            Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "assistant-data.json");

        try
        {
            var store = new JsonUserDataStore(filePath);
            var done = await store.AddReminderAsync("Turno médico", DueAt);
            var gone = await store.AddReminderAsync("Duplicado", DueAt.AddHours(1));

            Assert.True(await store.CompleteReminderAsync(done.Id));
            Assert.True(await store.DeleteReminderAsync(gone.Id));

            var reloaded = await new JsonUserDataStore(filePath).GetRemindersAsync();
            var stored = Assert.Single(reloaded);
            Assert.Equal(done.Id, stored.Id);
            Assert.True(stored.IsCompleted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReminderUpdateTool_CompletesByTitleAndHidesItFromTheList()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Llamar al banco", DueAt);
        var executor = new ToolExecutor([new ReminderUpdateTool(store), new ReminderListTool(store)]);

        var update = await executor.ExecuteAsync(new ToolCall(
            "update-1",
            ReminderUpdateTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "complete", title = "llamar al banco" })));

        Assert.Equal(ToolExecutionStatus.Succeeded, update.Status);
        Assert.True(Assert.Single(await store.GetRemindersAsync()).IsCompleted);

        var list = await executor.ExecuteAsync(new ToolCall(
            "list-1",
            ReminderListTool.ToolName,
            JsonSerializer.SerializeToElement(new { })));
        Assert.Equal(0, list.Data?.GetArrayLength());

        var listAll = await executor.ExecuteAsync(new ToolCall(
            "list-2",
            ReminderListTool.ToolName,
            JsonSerializer.SerializeToElement(new { include_completed = true })));
        Assert.Equal(1, listAll.Data?.GetArrayLength());
    }

    [Fact]
    public async Task ReminderUpdateTool_DeletesById()
    {
        var store = new InMemoryUserDataStore();
        var reminder = await store.AddReminderAsync("Sacar la basura", DueAt);
        var executor = new ToolExecutor([new ReminderUpdateTool(store)]);

        var result = await executor.ExecuteAsync(new ToolCall(
            "update-2",
            ReminderUpdateTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "borrar", id = reminder.Id.ToString() })));

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Empty(await store.GetRemindersAsync());
    }

    [Fact]
    public async Task ReminderUpdateTool_RefusesToGuessWhenTwoRemindersShareATitle()
    {
        var store = new InMemoryUserDataStore();
        await store.AddReminderAsync("Reunión", DueAt);
        await store.AddReminderAsync("Reunión", DueAt.AddDays(1));
        var executor = new ToolExecutor([new ReminderUpdateTool(store)]);

        var result = await executor.ExecuteAsync(new ToolCall(
            "update-3",
            ReminderUpdateTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "delete", title = "Reunión" })));

        // Elegir uno al azar sería el peor de los resultados: el usuario ve «listo» y se entera
        // recién cuando el que importaba no suena.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(2, (await store.GetRemindersAsync()).Count);
    }

    [Fact]
    public async Task ReminderUpdateTool_DoesNotClaimSuccessForSomethingItDidNotChange()
    {
        var store = new InMemoryUserDataStore();
        var executor = new ToolExecutor([new ReminderUpdateTool(store)]);

        var result = await executor.ExecuteAsync(new ToolCall(
            "update-4",
            ReminderUpdateTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "complete", id = Guid.NewGuid().ToString() })));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }
}
