using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.Tools;
using Viernes.Core.Usage;
using Xunit;

namespace Viernes.Core.Tests.Usage;

public sealed class UsageLedgerTests
{
    private static readonly DateTimeOffset Now =
        new(2035, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UsageCost_AggregateDoesNotMislabelPartialExactOrEstimatedTotals()
    {
        var aggregate = new UsageCost(ExactUsd: 0.10m) +
                        new UsageCost(EstimatedUsd: 0.20m);

        Assert.Null(aggregate.ExactUsd);
        Assert.Null(aggregate.EstimatedUsd);
        Assert.Equal(0.30m, aggregate.EffectiveUsd);
        Assert.Equal(0.30m, aggregate.EffectiveTotalUsd);
    }

    [Fact]
    public async Task RecordAsync_TracksContentFreeDailyMonthlyAndRoleTotals()
    {
        var ledger = UsageLedger.CreateInMemory(
            new UsageBudgetConfiguration(),
            new UsageRateCard([
                new ModelTokenRate("model/fast", 2m, 4m)
            ]),
            new FixedTimeProvider(Now));

        await ledger.RecordAsync(
            ModelRole.Fast,
            "model/fast",
            new TokenUsage(1_000, 500),
            requestId: "request-fast",
            isDeepTask: false);
        await ledger.RecordAsync(
            ModelRole.Reasoning,
            "model/reasoning",
            new TokenUsage(100, 20),
            new UsageCost(ExactUsd: 0.25m),
            isDeepTask: true,
            requestId: "request-reasoning");

        var daily = await ledger.GetDailyTotalsAsync();
        var fast = await ledger.GetMonthlyTotalsAsync(ModelRole.Fast);
        var entries = await ledger.GetEntriesSnapshotAsync();

        Assert.Equal(2, daily.RequestCount);
        Assert.Equal(1, daily.DeepTaskCount);
        Assert.Equal(new TokenUsage(1_100, 520), daily.Tokens);
        Assert.Equal(0.25m, daily.ExactCostUsd);
        Assert.Equal(0.004m, daily.EstimatedCostUsd);
        Assert.Equal(0.254m, daily.EffectiveCostUsd);
        Assert.Equal(1, fast.RequestCount);
        Assert.Equal(0.004m, fast.EffectiveCostUsd);
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("prompt", entry.GetType().GetProperties()
                .Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("response", entry.GetType().GetProperties()
                .Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task EvaluateAsync_AppliesGlobalRoleRequestAndDeepLimitsButAllowsExplicitOverride()
    {
        var budgets = new UsageBudgetConfiguration(
            dailyBudgetUsd: 1m,
            monthlyBudgetUsd: 5m,
            maxRequestsPerDay: 5,
            maxDeepTasksPerDay: 1,
            roleLimits:
            [
                new RoleBudgetLimits(
                    ModelRole.Fast,
                    dailyBudgetUsd: 0.5m,
                    maxRequestsPerDay: 1)
            ]);
        var ledger = UsageLedger.CreateInMemory(
            budgets,
            timeProvider: new FixedTimeProvider(Now));
        await ledger.RecordAsync(
            ModelRole.Fast,
            "model/fast",
            new TokenUsage(10, 5),
            new UsageCost(ExactUsd: 0.4m),
            isDeepTask: true,
            requestId: "existing");

        var blocked = await ledger.EvaluateAsync(new BudgetCheckRequest(
            ModelRole.Fast,
            EstimatedRequestCostUsd: 0.2m,
            IsDeepTask: true));
        var overridden = await ledger.EvaluateAsync(new BudgetCheckRequest(
            ModelRole.Fast,
            EstimatedRequestCostUsd: 0.2m,
            IsDeepTask: true,
            ExplicitBudgetOverride: true));

        Assert.Equal(BudgetGuardDecision.RequiresExplicitApproval, blocked.Decision);
        Assert.False(blocked.CanProceed);
        Assert.True(blocked.Reasons.Count >= 3);
        Assert.Equal(BudgetGuardDecision.Allow, overridden.Decision);
        Assert.True(overridden.CanProceed);
        Assert.True(overridden.WasExplicitlyOverridden);
        Assert.Equal(blocked.Reasons, overridden.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_SeparatesDailyMonthlyAndNormalizedPlanningRole()
    {
        var budgets = new UsageBudgetConfiguration(
            roleLimits: [new RoleBudgetLimits(ModelRole.Agent, maxRequestsPerDay: 2)]);
        var ledger = UsageLedger.CreateInMemory(
            budgets,
            timeProvider: new FixedTimeProvider(Now));
        await ledger.RecordAsync(
            ModelRole.Planning,
            "model/agent",
            new TokenUsage(1, 1),
            requestId: "today",
            timestampUtc: Now);
        await ledger.RecordAsync(
            ModelRole.Agent,
            "model/agent",
            new TokenUsage(2, 2),
            requestId: "yesterday",
            timestampUtc: Now.AddDays(-1));

        var result = await ledger.EvaluateAsync(new BudgetCheckRequest(ModelRole.Agent));

        Assert.Equal(1, result.RoleDailyTotals.RequestCount);
        Assert.Equal(2, result.RoleMonthlyTotals.RequestCount);
        Assert.Equal(BudgetGuardDecision.Allow, result.Decision);
    }

    [Fact]
    public async Task PersistentLedger_RoundTripsWithoutPromptsAndIsIdempotentByRequestId()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Viernes.Core.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "usage-ledger.json");

        try
        {
            var first = new UsageLedger(
                new UsageBudgetConfiguration(),
                filePath: path,
                timeProvider: new FixedTimeProvider(Now));
            var original = await first.RecordAsync(
                ModelRole.Fast,
                "model/fast",
                new TokenUsage(12, 3),
                new UsageCost(EstimatedUsd: 0.01m),
                requestId: "provider-request-1");
            var repeated = await first.RecordAsync(
                ModelRole.Fast,
                "model/fast",
                new TokenUsage(12, 3),
                new UsageCost(EstimatedUsd: 0.01m),
                requestId: "provider-request-1");

            var reloaded = new UsageLedger(
                new UsageBudgetConfiguration(),
                filePath: path,
                timeProvider: new FixedTimeProvider(Now));
            var entries = await reloaded.GetEntriesSnapshotAsync();
            var json = await File.ReadAllTextAsync(path);

            Assert.Equal(original, repeated);
            Assert.Single(entries);
            Assert.DoesNotContain("promptText", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("responseText", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("messages", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("content", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("conversation", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
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
    public async Task RecordAsync_RejectsConflictingDuplicateRequestId()
    {
        var ledger = UsageLedger.CreateInMemory(
            new UsageBudgetConfiguration(),
            timeProvider: new FixedTimeProvider(Now));
        await ledger.RecordAsync(
            ModelRole.Fast,
            "model/fast",
            new TokenUsage(1, 1),
            requestId: "duplicate");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RecordAsync(
            ModelRole.Fast,
            "model/fast",
            new TokenUsage(2, 1),
            requestId: "duplicate"));
    }

    [Fact]
    public async Task RecordCompletionAsync_RejectsLocalMode()
    {
        var ledger = UsageLedger.CreateInMemory(new UsageBudgetConfiguration());

        await Assert.ThrowsAsync<ArgumentException>(() => ledger.RecordCompletionAsync(
            ModelRole.Fast,
            ChatCompletionResult.LocalMode()));
    }

    [Fact]
    public async Task RecordCompletionAsync_UsesProviderRequestIdWhenAvailable()
    {
        var ledger = UsageLedger.CreateInMemory(new UsageBudgetConfiguration());
        var completion = new ChatCompletionResult(
            "ok",
            [],
            "model/fast",
            Usage: new TokenUsage(4, 2),
            RequestId: "provider-request-7");

        var entry = await ledger.RecordCompletionAsync(ModelRole.Fast, completion);

        Assert.Equal("provider-request-7", entry.RequestId);
    }

    [Fact]
    public async Task TrackingDecorator_RecordsExplicitRoleWithoutInspectingMessages()
    {
        var ledger = UsageLedger.CreateInMemory(new UsageBudgetConfiguration());
        var completion = new ChatCompletionResult(
            "respuesta que el ledger no recibe",
            [],
            "model/reasoning",
            Usage: new TokenUsage(8, 3),
            Cost: new UsageCost(ExactUsd: 0.02m),
            RequestId: "provider-request-tracked");
        var inner = new FakeRoleAwareClient(completion);
        var tracking = new UsageTrackingChatCompletionClient(
            inner,
            ledger,
            role => role == ModelRole.Reasoning);

        await tracking.CompleteAsync(
            [ConversationMessage.User("contenido privado")],
            [],
            new ModelSelectionRequest(ModelRole.Reasoning));

        var entry = Assert.Single(await ledger.GetEntriesSnapshotAsync());
        Assert.Equal(ModelRole.Reasoning, entry.Role);
        Assert.True(entry.IsDeepTask);
        Assert.Equal("provider-request-tracked", entry.RequestId);
        Assert.DoesNotContain("contenido privado", entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("respuesta que el ledger no recibe", entry.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRoleAwareClient(ChatCompletionResult completion)
        : IRoleAwareChatCompletionClient
    {
        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken = default) => Task.FromResult(completion);

        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            ModelSelectionRequest selectionRequest,
            CancellationToken cancellationToken = default) => Task.FromResult(completion);
    }
}
