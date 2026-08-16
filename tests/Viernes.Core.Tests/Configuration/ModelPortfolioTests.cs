using Viernes.Core.Configuration;
using Viernes.Core.Models;
using Xunit;

namespace Viernes.Core.Tests.Configuration;

public sealed class ModelPortfolioTests
{
    [Fact]
    public void Select_DefaultPortfolio_IsExplicitAndPremiumHasNoDefault()
    {
        var options = new ViernesOptions();

        var fast = options.SelectModel(new ModelSelectionRequest(ModelRole.Fast));
        var agent = options.SelectModel(new ModelSelectionRequest(ModelRole.Agent));
        var reasoning = options.SelectModel(new ModelSelectionRequest(ModelRole.Reasoning));
        var premium = options.SelectModel(new ModelSelectionRequest(
            ModelRole.Premium,
            PremiumApproved: true));
        var embeddings = options.SelectModel(new ModelSelectionRequest(ModelRole.Embeddings));
        var summary = options.SelectModel(new ModelSelectionRequest(ModelRole.LocalSummary));

        Assert.Equal((ModelSelectionStatus.Ready, "openai/gpt-5.6-luna"),
            (fast.Status, fast.Model));
        Assert.Equal((ModelSelectionStatus.Ready, "openai/gpt-5.6-terra"),
            (agent.Status, agent.Model));
        Assert.Equal((ModelSelectionStatus.Ready, "~anthropic/claude-sonnet-latest"),
            (reasoning.Status, reasoning.Model));
        Assert.Equal(ModelSelectionStatus.Unavailable, premium.Status);
        Assert.Null(premium.Model);
        Assert.Equal(ModelSelectionStatus.LocalPreferred, embeddings.Status);
        Assert.Equal(ModelSelectionStatus.LocalPreferred, summary.Status);
        Assert.False(embeddings.CanSendRemoteRequest);
        Assert.False(summary.CanSendRemoteRequest);
    }

    [Fact]
    public void Select_PremiumAndLocalPreferredRemoteModels_RequireSeparateExplicitFlags()
    {
        var options = new ViernesOptions(
            premiumModel: "vendor/premium",
            embeddingsModel: "vendor/embeddings",
            localSummaryModel: "vendor/summary");

        var premiumPending = options.SelectModel(new ModelSelectionRequest(ModelRole.Premium));
        var premiumApproved = options.SelectModel(new ModelSelectionRequest(
            ModelRole.Premium,
            PremiumApproved: true));
        var embeddingsLocal = options.SelectModel(new ModelSelectionRequest(ModelRole.Embeddings));
        var embeddingsRemote = options.SelectModel(new ModelSelectionRequest(
            ModelRole.Embeddings,
            AllowRemoteForLocalPreferredRole: true));

        Assert.Equal(ModelSelectionStatus.RequiresExplicitApproval, premiumPending.Status);
        Assert.False(premiumPending.CanSendRemoteRequest);
        Assert.Equal(ModelSelectionStatus.Ready, premiumApproved.Status);
        Assert.True(premiumApproved.CanSendRemoteRequest);
        Assert.Equal(ModelSelectionStatus.LocalPreferred, embeddingsLocal.Status);
        Assert.Equal(ModelSelectionStatus.Ready, embeddingsRemote.Status);
        Assert.Equal("vendor/embeddings", embeddingsRemote.Model);
    }

    [Fact]
    public void FromEnvironment_ConfiguresPortfolioBudgetsAndMutableRateCardWithoutRecompile()
    {
        var fastDaily = ViernesOptions.GetRoleDailyBudgetEnvironmentVariable(ModelRole.Fast);
        var agentRequests = ViernesOptions.GetRoleMaxRequestsEnvironmentVariable(ModelRole.Agent);
        var values = new Dictionary<string, string?>
        {
            [ViernesOptions.FastModelEnvironmentVariable] = "vendor/fast-explicit",
            [ViernesOptions.ModelEnvironmentVariable] = "vendor/fast-legacy-ignored",
            [ViernesOptions.FastFallbackModelsEnvironmentVariable] = "vendor/fast-backup",
            [ViernesOptions.AgentModelEnvironmentVariable] = "vendor/agent",
            [ViernesOptions.PlanningModelEnvironmentVariable] = "vendor/legacy-ignored",
            [ViernesOptions.ReasoningModelEnvironmentVariable] = "vendor/reasoning",
            [ViernesOptions.PremiumModelEnvironmentVariable] = "vendor/premium",
            [ViernesOptions.EmbeddingsModelEnvironmentVariable] = "vendor/embed",
            [ViernesOptions.LocalSummaryModelEnvironmentVariable] = "vendor/summary",
            [ViernesOptions.PreferLocalEmbeddingsEnvironmentVariable] = "false",
            [ViernesOptions.PreferLocalSummaryEnvironmentVariable] = "YES",
            [ViernesOptions.MaxRequestsEnvironmentVariable] = "40",
            [fastDaily] = "0.75",
            [agentRequests] = "5",
            [ViernesOptions.RateCardEnvironmentVariable] =
                "{\"vendor/agent\":{\"inputUsdPerMillion\":2.5,\"outputUsdPerMillion\":7.5}}"
        };

        var options = ViernesOptions.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.Equal("vendor/fast-explicit", options.Model);
        Assert.Equal(["vendor/fast-backup"], options.FallbackModels);
        Assert.Equal("vendor/agent", options.AgentModel);
        Assert.Equal("vendor/agent", options.PlanningModel);
        Assert.Equal("vendor/reasoning", options.ReasoningModel);
        Assert.Equal("vendor/premium", options.PremiumModel);
        Assert.False(options.PreferLocalEmbeddings);
        Assert.True(options.PreferLocalSummary);
        Assert.Equal(40, options.MaxRequestsPerDay);
        Assert.Equal(0.75m, options.UsageBudgets.GetRoleLimits(ModelRole.Fast).DailyBudgetUsd);
        Assert.Equal(5, options.UsageBudgets.GetRoleLimits(ModelRole.Planning).MaxRequestsPerDay);
        Assert.Equal(
            0.0000175m,
            options.RateCard.EstimateCostUsd("vendor/agent", new TokenUsage(4, 1)));
    }

    [Fact]
    public void FromEnvironment_RejectsInvalidLocalPreferenceAndRateCard()
    {
        Assert.Throws<InvalidOperationException>(() => ViernesOptions.FromEnvironment(name =>
            name == ViernesOptions.PreferLocalEmbeddingsEnvironmentVariable ? "perhaps" : null));
        Assert.Throws<FormatException>(() => ViernesOptions.FromEnvironment(name =>
            name == ViernesOptions.RateCardEnvironmentVariable ? "{bad json" : null));
    }

    [Fact]
    public void RoleEnvironmentNames_NormalizeLegacyPlanningToAgent()
    {
        Assert.Equal(
            ViernesOptions.GetRoleDailyBudgetEnvironmentVariable(ModelRole.Agent),
            ViernesOptions.GetRoleDailyBudgetEnvironmentVariable(ModelRole.Planning));
        Assert.Contains("_AGENT_", ViernesOptions.GetRoleMaxRequestsEnvironmentVariable(ModelRole.Agent));
    }
}
