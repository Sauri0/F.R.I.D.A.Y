using System.Text.Json;
using Viernes.Core.Models;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

public sealed class PcActionExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutExecutor_StaysASimulation()
    {
        var result = await RunAsync(new PcActionTool(), "open_settings", confirmed: true);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(result.Data!.Value.GetProperty("simulated").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_WithExecutor_RunsAPreviewableAction()
    {
        var executor = new RecordingExecutor();

        var result = await RunAsync(new PcActionTool(executor), "open_settings", confirmed: true, target: "sonido");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.False(result.Data!.Value.GetProperty("simulated").GetBoolean());
        Assert.Equal([("open_settings", "sonido")], executor.Calls);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("run_command")]
    [InlineData("change_setting")]
    [InlineData("delete_file")]
    [InlineData("format_disk")]
    public async Task ExecuteAsync_NeverReachesTheExecutorForASensitiveOrDestructiveAction(string action)
    {
        var executor = new RecordingExecutor();

        // Incluso declarando la confirmación otorgada, la política corta antes de ejecutar.
        var result = await RunAsync(new PcActionTool(executor), action, confirmed: true);

        Assert.NotEqual(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresAnActionTheExecutorDoesNotDeclare()
    {
        var executor = new RecordingExecutor(supported: []);

        var result = await RunAsync(new PcActionTool(executor), "open_settings", confirmed: true);

        Assert.True(result.Data!.Value.GetProperty("simulated").GetBoolean());
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsAFailureInsteadOfClaimingSuccess()
    {
        var executor = new RecordingExecutor(succeeds: false);

        var result = await RunAsync(new PcActionTool(executor), "open_application", confirmed: true, target: "calculadora");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public void WebSearchTool_SaysPlainlyWhichModeItIsIn()
    {
        Assert.Contains("desactivada", new WebSearchTool().Definition.Description, StringComparison.Ordinal);
        Assert.Contains("No hace falta", new WebSearchTool(true).Definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSearchTool_WithProviderSearch_DoesNotPretendToHaveSearched()
    {
        var tool = new WebSearchTool(providerSearchEnabled: true);
        var arguments = JsonSerializer.SerializeToElement(new { query = "clima en Buenos Aires" });

        var result = await tool.ExecuteAsync(arguments, new ToolExecutionContext("call-1"));

        Assert.True(result.Data!.Value.GetProperty("providerSearch").GetBoolean());
        Assert.False(result.Data!.Value.TryGetProperty("simulated", out _));
    }

    private static async Task<ToolExecutionResult> RunAsync(
        PcActionTool tool,
        string action,
        bool confirmed,
        string? target = null)
    {
        var executor = new ToolExecutor([tool], new SafeToolPolicy());
        var arguments = target is null
            ? JsonSerializer.SerializeToElement(new { action })
            : JsonSerializer.SerializeToElement(new { action, target });
        return await executor.ExecuteAsync(
            new ToolCall("call-1", PcActionTool.ToolName, arguments),
            confirmationGranted: confirmed);
    }

    private sealed class RecordingExecutor(
        string[]? supported = null,
        bool succeeds = true) : IPcActionExecutor
    {
        public List<(string Action, string? Target)> Calls { get; } = [];

        public IReadOnlySet<string> SupportedActions { get; } =
            new HashSet<string>(
                supported ?? ["open_settings", "open_application", "show_desktop"],
                StringComparer.OrdinalIgnoreCase);

        public Task<PcActionOutcome> ExecuteAsync(
            string action,
            string? target,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((action, target));
            return Task.FromResult(succeeds
                ? new PcActionOutcome(true, "Hecho.")
                : new PcActionOutcome(false, "Windows no dejó abrir eso."));
        }
    }
}
