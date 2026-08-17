using System.Text.Json;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Persistence;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Conversation;

public sealed class ConversationOrchestratorTests
{
    [Fact]
    public async Task ProcessAsync_LocalCompletion_ReturnsOfflineResponseAndExpectedStateCycle()
    {
        var chatClient = new QueueChatClient(ChatCompletionResult.LocalMode());
        var orchestrator = new ConversationOrchestrator(chatClient, new ToolExecutor([]));
        var states = ObserveStates(orchestrator);

        var result = await orchestrator.ProcessAsync("  hola viernes  ");

        Assert.True(result.IsLocalMode);
        Assert.Equal(AssistantState.Idle, result.State);
        Assert.Contains("modo local", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [AssistantState.Thinking, AssistantState.Speaking, AssistantState.Idle],
            states);
        Assert.Equal(AssistantState.Idle, orchestrator.CurrentState);

        var request = Assert.Single(chatClient.Requests);
        Assert.Equal(ConversationRole.System, request.Messages[0].Role);
        Assert.Equal("hola viernes", request.Messages[1].Content);
    }

    [Fact]
    public async Task ProcessAsync_TextCompletion_ReturnsTrimmedTextAndStoresConversation()
    {
        var chatClient = new QueueChatClient(
            new ChatCompletionResult("  Todo listo.  ", [], "test/model"));
        var orchestrator = new ConversationOrchestrator(chatClient, new ToolExecutor([]));

        var result = await orchestrator.ProcessAsync("consulta");

        Assert.False(result.IsLocalMode);
        Assert.Equal("Todo listo.", result.Text);
        Assert.Empty(result.ToolResults);
        var history = orchestrator.GetHistorySnapshot();
        Assert.Equal([ConversationRole.System, ConversationRole.User, ConversationRole.Assistant],
            history.Select(message => message.Role));
        Assert.Equal("  Todo listo.  ", history[^1].Content);
    }

    [Fact]
    public async Task ProcessAsync_SafeToolCall_ExecutesThroughPolicyAndReturnsResultToModel()
    {
        var call = CreateToolCall("call-safe", "safe_tool");
        var chatClient = new QueueChatClient(
            new ChatCompletionResult(
                null,
                [call],
                "first/model",
                Usage: new TokenUsage(11, 4),
                Cost: new UsageCost(0.001m, 0.0012m)),
            new ChatCompletionResult(
                "Operación terminada.",
                [],
                "final/model",
                Usage: new TokenUsage(7, 3),
                Cost: new UsageCost(0.002m, 0.0025m)));
        var tool = new RecordingTool("safe_tool", ToolRiskLevel.Safe);
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor([tool]));

        var result = await orchestrator.ProcessAsync("hacelo");

        var execution = Assert.Single(result.ToolResults);
        Assert.Equal(ToolExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.False(tool.LastContext!.ConfirmationGranted);
        Assert.Equal("Operación terminada.", result.Text);
        Assert.Equal("final/model", result.Model);
        Assert.Equal(new TokenUsage(18, 7), result.Usage);
        Assert.Equal(25, result.Usage.TotalTokens);
        Assert.Equal(0.003m, result.Cost.ExactUsd);
        Assert.Equal(0.0037m, result.Cost.EstimatedUsd);

        Assert.Equal(2, chatClient.Requests.Count);
        var followUpMessages = chatClient.Requests[1].Messages;
        var assistantToolMessage = Assert.Single(followUpMessages, message =>
            message.Role == ConversationRole.Assistant && message.ToolCalls?.Count > 0);
        Assert.Equal("call-safe", Assert.Single(assistantToolMessage.ToolCalls!).Id);
        var toolMessage = Assert.Single(followUpMessages, message =>
            message.Role == ConversationRole.Tool);
        Assert.Equal("call-safe", toolMessage.ToolCallId);
        Assert.Contains("Succeeded", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteLocalToolAsync_CreatesReminderAndAgendaWithoutCallingModel()
    {
        var dataStore = new InMemoryUserDataStore();
        var chatClient = new QueueChatClient();
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor(BuiltInTools.Create(dataStore)));
        var startsAt = new DateTimeOffset(2032, 4, 5, 10, 0, 0, TimeSpan.FromHours(-3));

        var reminder = await orchestrator.ExecuteLocalToolAsync(
            ReminderCreateTool.ToolName,
            JsonSerializer.SerializeToElement(new
            {
                title = "Comprar pan",
                due_at = startsAt.ToString("O"),
            }));
        var agenda = await orchestrator.ExecuteLocalToolAsync(
            AgendaCreateTool.ToolName,
            JsonSerializer.SerializeToElement(new
            {
                title = "Reunión",
                starts_at = startsAt.AddDays(1).ToString("O"),
            }));

        Assert.Equal(ToolExecutionStatus.Succeeded, reminder.Status);
        Assert.Equal(ToolExecutionStatus.Succeeded, agenda.Status);
        Assert.Single(await dataStore.GetRemindersAsync());
        Assert.Single(await dataStore.GetAgendaItemsAsync());
        Assert.Empty(chatClient.Requests);
    }

    [Fact]
    public async Task ExecuteLocalToolAsync_RunsAllowlistedActionsAndStillGuardsDestructiveOnes()
    {
        var chatClient = new QueueChatClient();
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor([new PcActionTool()]));

        var allowed = await orchestrator.ExecuteLocalToolAsync(
            PcActionTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "show_desktop" }));
        var destructive = await orchestrator.ExecuteLocalToolAsync(
            PcActionTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "delete_file", target = "test.txt" }),
            confirmationGranted: true);

        // Lo permitido llega a ejecutarse; lo destructivo no pasa ni con la confirmación ya dada.
        // Esa asimetría es el punto: la preferencia afloja el permiso, nunca la lista blanca.
        // Lo permitido termina en Failed porque acá no hay ejecutor de sistema: sin él no se puede
        // hacer nada, y decir que se hizo sería exactamente el reporte falso que se sacó.
        Assert.Equal(ToolExecutionStatus.Failed, allowed.Status);
        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, destructive.Status);
        Assert.Null(destructive.Data);
        Assert.Empty(chatClient.Requests);
    }

    [Fact]
    public async Task ConfirmToolAsync_ExecutesOnlyAStoredConfirmableCallAndOnlyOnce()
    {
        var call = CreateToolCall("call-confirm", "confirmable_tool");
        var chatClient = new QueueChatClient(
            new ChatCompletionResult(null, [call], "test/model"),
            new ChatCompletionResult("Quedó pendiente.", [], "test/model"));
        var tool = new RecordingTool("confirmable_tool", ToolRiskLevel.RequiresConfirmation);
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor([tool]));

        var turn = await orchestrator.ProcessAsync("preparalo");
        var beforeConsent = Assert.Single(turn.ToolResults);
        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, beforeConsent.Status);
        Assert.True(turn.NeedsConfirmation);
        Assert.Equal(0, tool.ExecutionCount);

        var confirmed = await orchestrator.ConfirmToolAsync("call-confirm");
        var repeated = await orchestrator.ConfirmToolAsync("call-confirm");

        Assert.Equal(ToolExecutionStatus.Succeeded, confirmed.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(tool.LastContext!.ConfirmationGranted);
        Assert.Equal(ToolExecutionStatus.Denied, repeated.Status);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task ConfirmToolAsync_HighRiskPendingCall_RemainsUnexecuted()
    {
        var call = CreateToolCall("call-danger", "dangerous_tool");
        var chatClient = new QueueChatClient(
            new ChatCompletionResult(null, [call], "test/model"),
            new ChatCompletionResult("No se ejecutó.", [], "test/model"));
        var tool = new RecordingTool("dangerous_tool", ToolRiskLevel.Destructive);
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor([tool]));

        await orchestrator.ProcessAsync("borrá eso");
        var confirmed = await orchestrator.ConfirmToolAsync("call-danger");

        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, confirmed.Status);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateToolCallId_IsBlockedWithoutSecondExecution()
    {
        var first = CreateToolCall("duplicate", "safe_tool");
        var second = CreateToolCall("duplicate", "safe_tool");
        var chatClient = new QueueChatClient(
            new ChatCompletionResult(null, [first], "test/model"),
            new ChatCompletionResult(null, [second], "test/model"),
            new ChatCompletionResult("terminado", [], "test/model"));
        var tool = new RecordingTool("safe_tool", ToolRiskLevel.Safe);
        var orchestrator = new ConversationOrchestrator(
            chatClient,
            new ToolExecutor([tool]));

        var result = await orchestrator.ProcessAsync("repetí");

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(
            [ToolExecutionStatus.Succeeded, ToolExecutionStatus.Denied],
            result.ToolResults.Select(toolResult => toolResult.Status));
    }

    [Fact]
    public async Task ProcessAsync_OpenRouterFailure_ReturnsSanitizedErrorAndErrorState()
    {
        const string sensitiveDetail = "unit-test-provider-detail";
        var chatClient = new QueueChatClient(
            new OpenRouterException(sensitiveDetail));
        var orchestrator = new ConversationOrchestrator(chatClient, new ToolExecutor([]));
        var states = ObserveStates(orchestrator);

        var result = await orchestrator.ProcessAsync("hola");

        Assert.Equal(AssistantState.Error, result.State);
        Assert.Equal(AssistantState.Error, orchestrator.CurrentState);
        Assert.Equal([AssistantState.Thinking, AssistantState.Error], states);
        Assert.DoesNotContain(sensitiveDetail, result.Text, StringComparison.Ordinal);
        Assert.Contains("servicio de conversación", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_RejectsEmptyInputBeforeCallingProvider(string input)
    {
        var chatClient = new QueueChatClient(ChatCompletionResult.LocalMode());
        var orchestrator = new ConversationOrchestrator(chatClient, new ToolExecutor([]));

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.ProcessAsync(input));

        Assert.Empty(chatClient.Requests);
        Assert.Equal(AssistantState.Idle, orchestrator.CurrentState);
    }

    [Fact]
    public async Task ProcessAsync_RejectsOversizedInputBeforeCallingProvider()
    {
        var chatClient = new QueueChatClient(ChatCompletionResult.LocalMode());
        var orchestrator = new ConversationOrchestrator(chatClient, new ToolExecutor([]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.ProcessAsync(new string('x', 12_001)));

        Assert.Empty(chatClient.Requests);
    }

    [Fact]
    public void SetListening_PublishesPrivacyRelevantStateChanges()
    {
        var orchestrator = new ConversationOrchestrator(
            new QueueChatClient(ChatCompletionResult.LocalMode()),
            new ToolExecutor([]));
        var states = ObserveStates(orchestrator);

        orchestrator.SetListening(true);
        orchestrator.SetListening(false);

        Assert.Equal([AssistantState.Listening, AssistantState.Idle], states);
    }

    private static List<AssistantState> ObserveStates(ConversationOrchestrator orchestrator)
    {
        var states = new List<AssistantState>();
        orchestrator.StateChanged += (_, args) => states.Add(args.CurrentState);
        return states;
    }

    private static ToolCall CreateToolCall(string id, string name) =>
        new(id, name, JsonSerializer.SerializeToElement(new { value = "test" }));

    private sealed record CompletionRequest(
        IReadOnlyList<ConversationMessage> Messages,
        IReadOnlyList<ToolDefinition> Tools);

    private sealed class QueueChatClient : IChatCompletionClient
    {
        private readonly Queue<object> _responses;

        public QueueChatClient(params object[] responses)
        {
            _responses = new Queue<object>(responses);
        }

        public List<CompletionRequest> Requests { get; } = [];

        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new CompletionRequest(messages.ToArray(), tools.ToArray()));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No completion was queued for the test.");
            }

            return _responses.Dequeue() switch
            {
                ChatCompletionResult completion => Task.FromResult(completion),
                Exception exception => Task.FromException<ChatCompletionResult>(exception),
                var unexpected => throw new InvalidOperationException(
                    $"Unsupported queued response: {unexpected.GetType().Name}"),
            };
        }
    }

    private sealed class RecordingTool : IAssistantTool
    {
        public RecordingTool(string name, ToolRiskLevel riskLevel)
        {
            Definition = ToolDefinition.Create(
                name,
                "Test tool",
                new { type = "object" },
                riskLevel);
        }

        public ToolDefinition Definition { get; }

        public int ExecutionCount { get; private set; }

        public ToolExecutionContext? LastContext { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            LastContext = context;
            return Task.FromResult(ToolExecutionResult.Success(
                context.ToolCallId,
                Definition.Name,
                "ok"));
        }
    }
}
