using System.Text.Json;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

public sealed class ToolExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SafeTool_ExecutesWithoutConfirmation()
    {
        var tool = new RecordingTool(ToolRiskLevel.Safe);
        var executor = new ToolExecutor([tool]);

        var result = await executor.ExecuteAsync(CreateCall(tool.Definition.Name));

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.NotNull(tool.LastContext);
        Assert.False(tool.LastContext.ConfirmationGranted);
    }

    [Fact]
    public async Task ExecuteAsync_ConfirmableTool_DoesNotExecuteBeforeConsent()
    {
        var tool = new RecordingTool(ToolRiskLevel.RequiresConfirmation);
        var executor = new ToolExecutor([tool]);

        var result = await executor.ExecuteAsync(CreateCall(tool.Definition.Name));

        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, result.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains("confirmación", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ConfirmableTool_ExecutesAfterExplicitConsent()
    {
        var tool = new RecordingTool(ToolRiskLevel.RequiresConfirmation);
        var executor = new ToolExecutor([tool]);

        var result = await executor.ExecuteAsync(
            CreateCall(tool.Definition.Name),
            confirmationGranted: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.NotNull(tool.LastContext);
        Assert.True(tool.LastContext.ConfirmationGranted);
    }

    [Theory]
    [InlineData(ToolRiskLevel.Sensitive)]
    [InlineData(ToolRiskLevel.Destructive)]
    public async Task ExecuteAsync_HighRiskTool_NeverExecutesEvenWhenConfirmationFlagIsTrue(
        ToolRiskLevel riskLevel)
    {
        var tool = new RecordingTool(riskLevel);
        var executor = new ToolExecutor([tool]);

        var result = await executor.ExecuteAsync(
            CreateCall(tool.Definition.Name),
            confirmationGranted: true);

        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, result.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains("no puede ejecutarla", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("delete_file")]
    [InlineData("format_disk")]
    [InlineData("run_command")]
    [InlineData("shutdown")]
    [InlineData("unrecognized_action")]
    public async Task ExecuteAsync_PcSensitiveOrDestructiveAction_RemainsBlockedAfterConfirmation(
        string action)
    {
        var executor = new ToolExecutor([new PcActionTool()]);
        var call = new ToolCall(
            "pc-call",
            PcActionTool.ToolName,
            JsonSerializer.SerializeToElement(new { action, target = "test-only-target" }));

        var result = await executor.ExecuteAsync(call, confirmationGranted: true);

        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ExecuteAsync_AllowlistedPcAction_FollowsTheConfirmationPreference()
    {
        var call = new ToolCall(
            "pc-call",
            PcActionTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "show_desktop" }));

        var silent = await new ToolExecutor([new PcActionTool(confirmActions: false)]).ExecuteAsync(call);
        var asking = await new ToolExecutor([new PcActionTool(confirmActions: true)]).ExecuteAsync(call);

        // La preferencia gobierna la barrera blanda, y sólo esa: la lista blanca no la toca nadie.
        // Sin preguntar, la acción llega a ejecutarse —y falla porque en la prueba no hay ejecutor
        // de sistema conectado, que es lo correcto: sin ejecutor no hay nada que dar por hecho—.
        Assert.Equal(ToolExecutionStatus.Failed, silent.Status);
        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, asking.Status);
    }

    [Fact]
    public async Task ExecuteAsync_PcActionOutsideTheAllowlist_StillNeedsConsent()
    {
        var executor = new ToolExecutor([new PcActionTool()]);
        var call = new ToolCall(
            "pc-call",
            PcActionTool.ToolName,
            JsonSerializer.SerializeToElement(new { action = "run_command", target = "whoami" }));

        var result = await executor.ExecuteAsync(call);

        // Quitar la confirmación de lo permitido no puede abrirle la puerta a lo que no lo está.
        Assert.Equal(ToolExecutionStatus.NeedsConfirmation, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_IsDenied()
    {
        var executor = new ToolExecutor([]);

        var result = await executor.ExecuteAsync(CreateCall("not_registered"));

        Assert.Equal(ToolExecutionStatus.Denied, result.Status);
        Assert.Equal("not_registered", result.ToolName);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedToolFailure_DoesNotLeakExceptionDetails()
    {
        const string sensitiveDetail = "unit-test-sensitive-local-detail";
        var tool = new RecordingTool(
            ToolRiskLevel.Safe,
            _ => throw new InvalidOperationException(sensitiveDetail));
        var executor = new ToolExecutor([tool]);

        var result = await executor.ExecuteAsync(CreateCall(tool.Definition.Name));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.DoesNotContain(sensitiveDetail, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ReminderWithCredentialLikeText_IsRejectedWithoutPersistence()
    {
        var store = new InMemoryUserDataStore();
        var executor = new ToolExecutor([new ReminderCreateTool(store)]);
        var call = new ToolCall(
            "reminder-secret",
            ReminderCreateTool.ToolName,
            JsonSerializer.SerializeToElement(new
            {
                title = "rotar api_key = sk-example-value-that-must-not-be-stored",
                due_at = "2026-08-17T09:00:00-03:00"
            }));

        var result = await executor.ExecuteAsync(call);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Empty(await store.GetRemindersAsync());
        Assert.DoesNotContain("sk-example", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AgendaNotesWithCredentialLikeText_IsRejectedWithoutPersistence()
    {
        var store = new InMemoryUserDataStore();
        var executor = new ToolExecutor([new AgendaCreateTool(store)]);
        var call = new ToolCall(
            "agenda-secret",
            AgendaCreateTool.ToolName,
            JsonSerializer.SerializeToElement(new
            {
                title = "Reunión",
                starts_at = "2026-08-17T09:00:00-03:00",
                notes = "password: never-store-this-value"
            }));

        var result = await executor.ExecuteAsync(call);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Empty(await store.GetAgendaItemsAsync());
        Assert.DoesNotContain("never-store", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsDuplicateToolNames()
    {
        var first = new RecordingTool(ToolRiskLevel.Safe, name: "same_name");
        var second = new RecordingTool(ToolRiskLevel.Safe, name: "same_name");

        Assert.Throws<ArgumentException>(() => new ToolExecutor([first, second]));
    }

    private static ToolCall CreateCall(string name) =>
        new("call-1", name, JsonSerializer.SerializeToElement(new { value = "test" }));

    private sealed class RecordingTool : IAssistantTool
    {
        private readonly Func<ToolExecutionContext, ToolExecutionResult> _execute;

        public RecordingTool(
            ToolRiskLevel riskLevel,
            Func<ToolExecutionContext, ToolExecutionResult>? execute = null,
            string name = "test_tool")
        {
            Definition = ToolDefinition.Create(
                name,
                "A test-only tool.",
                new { type = "object" },
                riskLevel);
            _execute = execute ?? (context =>
                ToolExecutionResult.Success(context.ToolCallId, name, "ok"));
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
            return Task.FromResult(_execute(context));
        }
    }
}
