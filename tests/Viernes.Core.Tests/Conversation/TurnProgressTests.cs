using System.Text.Json;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Conversation;

public sealed class TurnProgressTests
{
    [Fact]
    public async Task ProcessAsync_ReportsWhatItUnderstoodBeforeAnythingElse()
    {
        var (orchestrator, snapshots) = CreateOrchestrator(new StubChatClient([
            ChatCompletionResult.LocalMode()
        ]));

        await orchestrator.ProcessAsync("recordame comprar pan");

        var first = snapshots[0];
        Assert.StartsWith("Entendí: «recordame comprar pan»", first[0].Label, StringComparison.Ordinal);
        Assert.Equal(TurnStepStatus.Done, first[0].Status);
        Assert.Equal(TurnStepStatus.Running, first[1].Status);
    }

    [Fact]
    public async Task ProcessAsync_ShowsEachToolAsItsOwnStepAndMarksItDone()
    {
        var call = new ToolCall(
            "call-1",
            "agenda_list",
            JsonSerializer.SerializeToElement(new { }));
        var (orchestrator, snapshots) = CreateOrchestrator(new StubChatClient([
            new ChatCompletionResult(null, [call], "provider/model"),
            new ChatCompletionResult("Tenés dos eventos.", [], "provider/model")
        ]));

        await orchestrator.ProcessAsync("mostrame mi agenda");

        var final = snapshots[^1];
        var toolStep = Assert.Single(final, step => step.Label == "Leyendo tu agenda");
        Assert.Equal(TurnStepStatus.Done, toolStep.Status);
        Assert.All(final, step => Assert.NotEqual(TurnStepStatus.Running, step.Status));
    }

    [Fact]
    public async Task ProcessAsync_MarksAToolStoppedByThePolicyAsBlocked()
    {
        // pc_action con una acción sensible: la política la detiene aunque el modelo la pida.
        var call = new ToolCall(
            "call-1",
            "pc_action",
            JsonSerializer.SerializeToElement(new { action = "shutdown" }));
        var (orchestrator, snapshots) = CreateOrchestrator(new StubChatClient([
            new ChatCompletionResult(null, [call], "provider/model"),
            new ChatCompletionResult("No puedo apagar la PC.", [], "provider/model")
        ]));

        await orchestrator.ProcessAsync("apagá la computadora");

        var blocked = Assert.Single(snapshots[^1], step => step.Label == "Revisando la acción de PC");
        Assert.Equal(TurnStepStatus.Blocked, blocked.Status);
    }

    [Fact]
    public async Task ProcessAsync_TruncatesALongInputInsteadOfSpillingItIntoTheBubble()
    {
        var (orchestrator, snapshots) = CreateOrchestrator(new StubChatClient([
            ChatCompletionResult.LocalMode()
        ]));

        await orchestrator.ProcessAsync(new string('a', 300));

        var label = snapshots[0][0].Label;
        Assert.EndsWith("…»", label, StringComparison.Ordinal);
        Assert.True(label.Length < 60, $"La etiqueta quedó en {label.Length} caracteres.");
    }

    [Fact]
    public async Task ProcessAsync_StartsAFreshListOnEveryTurn()
    {
        var (orchestrator, snapshots) = CreateOrchestrator(new StubChatClient([
            ChatCompletionResult.LocalMode(),
            ChatCompletionResult.LocalMode()
        ]));

        await orchestrator.ProcessAsync("primero");
        var afterFirst = snapshots.Count;
        await orchestrator.ProcessAsync("segundo");

        Assert.StartsWith("Entendí: «segundo»", snapshots[afterFirst][0].Label, StringComparison.Ordinal);
        Assert.Equal(2, snapshots[afterFirst].Count);
    }

    private static (ConversationOrchestrator Orchestrator, List<IReadOnlyList<TurnStep>> Snapshots)
        CreateOrchestrator(IChatCompletionClient chatClient)
    {
        var executor = new ToolExecutor(
            BuiltInTools.Create(new InMemoryUserDataStore()),
            new SafeToolPolicy());
        var orchestrator = new ConversationOrchestrator(chatClient, executor, new ViernesOptions());
        var snapshots = new List<IReadOnlyList<TurnStep>>();
        orchestrator.ProgressChanged += (_, args) => snapshots.Add(args.Steps);
        return (orchestrator, snapshots);
    }

    private sealed class StubChatClient(IReadOnlyList<ChatCompletionResult> responses) : IChatCompletionClient
    {
        private int _index;

        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(responses[Math.Min(_index++, responses.Count - 1)]);
    }
}
