using System.Text.Json;
using Viernes.Core.Configuration;
using Xunit;

namespace Viernes.Core.Tests.Configuration;

public sealed class ViernesOptionsTests
{
    [Fact]
    public void FromEnvironment_WithoutApiKey_UsesSafeLocalModeAndDefaults()
    {
        var options = ViernesOptions.FromEnvironment(_ => null);

        Assert.False(options.HasApiKey);
        Assert.True(options.IsLocalMode);
        Assert.Equal("openrouter/auto", ViernesOptions.DefaultModel);
        Assert.Equal("openrouter/auto", ViernesOptions.DefaultAgentModel);
        Assert.Equal("openrouter/auto", ViernesOptions.DefaultPlanningModel);
        Assert.Equal("openrouter/auto", ViernesOptions.DefaultReasoningModel);
        Assert.Equal(ViernesOptions.DefaultModel, options.Model);
        Assert.Equal(ViernesOptions.DefaultPlanningModel, options.PlanningModel);
        Assert.Equal(ViernesOptions.DefaultAgentModel, options.AgentModel);
        Assert.Equal(ViernesOptions.DefaultReasoningModel, options.ReasoningModel);
        Assert.Null(options.PremiumModel);
        Assert.Equal(options.Model, options.ResolveModel(ModelRole.Fast));
        Assert.Equal(options.PlanningModel, options.ResolveModel(ModelRole.Planning));
        Assert.Equal(options.AgentModel, options.ResolveModel(ModelRole.Agent));
        Assert.Equal(options.ReasoningModel, options.ResolveModel(ModelRole.Reasoning));
        Assert.True(options.UsesAutoRouter);
        Assert.Null(options.Preset);

        // Sin cadena de fallback hardcodeada: el router resuelve alternativas del lado del servidor.
        Assert.Empty(options.FallbackModels);
        Assert.Null(options.DailyBudgetUsd);
        Assert.Null(options.MonthlyBudgetUsd);
        Assert.False(options.HasConfiguredSpendingBudget);
        Assert.Equal(3, options.MaxDeepTasksPerDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_TreatsEmptyCredentialsAsMissing(string? apiKey)
    {
        var options = new ViernesOptions(apiKey);

        Assert.False(options.HasApiKey);
        Assert.True(options.IsLocalMode);
    }

    [Fact]
    public void FromEnvironment_ReadsModelAndNormalizesFallbacks()
    {
        var values = new Dictionary<string, string?>
        {
            [ViernesOptions.ApiKeyEnvironmentVariable] = null,
            [ViernesOptions.ModelEnvironmentVariable] = "  vendor/fast-model  ",
            [ViernesOptions.FallbackModelsEnvironmentVariable] =
                "vendor/backup, vendor/fast-model, VENDOR/BACKUP, vendor/last",
        };

        var options = ViernesOptions.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.Equal("vendor/fast-model", options.Model);
        Assert.Equal(["vendor/backup", "vendor/last"], options.FallbackModels);
    }

    [Fact]
    public void FromEnvironment_ReadsPlanningLaneAndSpendingGuardrails()
    {
        var values = new Dictionary<string, string?>
        {
            [ViernesOptions.PlanningModelEnvironmentVariable] = "  ~vendor/planning-latest  ",
            [ViernesOptions.DailyBudgetEnvironmentVariable] = "1.25",
            [ViernesOptions.MonthlyBudgetEnvironmentVariable] = "19.90",
            [ViernesOptions.MaxDeepTasksEnvironmentVariable] = "7",
        };

        var options = ViernesOptions.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.Equal("~vendor/planning-latest", options.PlanningModel);
        Assert.Equal("~vendor/planning-latest", options.AgentModel);
        Assert.Equal("~vendor/planning-latest", options.ResolveModel(ModelRole.Planning));
        Assert.Equal(1.25m, options.DailyBudgetUsd);
        Assert.Equal(19.90m, options.MonthlyBudgetUsd);
        Assert.True(options.HasConfiguredSpendingBudget);
        Assert.Equal(7, options.MaxDeepTasksPerDay);
    }

    [Theory]
    [InlineData(ViernesOptions.DailyBudgetEnvironmentVariable, "-0.01")]
    [InlineData(ViernesOptions.MonthlyBudgetEnvironmentVariable, "100000.01")]
    [InlineData(ViernesOptions.MaxDeepTasksEnvironmentVariable, "101")]
    public void FromEnvironment_RejectsOutOfRangeGuardrails(string variableName, string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ViernesOptions.FromEnvironment(name =>
            string.Equals(name, variableName, StringComparison.Ordinal) ? value : null));
    }

    [Fact]
    public void FromEnvironment_RejectsNonNumericBudget()
    {
        Assert.Throws<InvalidOperationException>(() => ViernesOptions.FromEnvironment(name =>
            string.Equals(name, ViernesOptions.DailyBudgetEnvironmentVariable, StringComparison.Ordinal)
                ? "not-a-number"
                : null));
    }

    [Fact]
    public void DiagnosticsAndSerialization_NeverExposeApiKey()
    {
        const string sentinelSecret = "unit-test-secret-that-must-never-leak";
        var options = new ViernesOptions(sentinelSecret, model: "vendor/fast-model");

        var diagnosticText = options.ToString();
        var serialized = JsonSerializer.Serialize(options);

        Assert.True(options.HasApiKey);
        Assert.False(options.IsLocalMode);
        Assert.DoesNotContain(sentinelSecret, diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinelSecret, serialized, StringComparison.Ordinal);
        Assert.Contains("HasApiKey = True", diagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicSurface_DoesNotExposeCredentialAsAProperty()
    {
        var publicProperties = typeof(ViernesOptions).GetProperties();

        Assert.DoesNotContain(publicProperties, property =>
            property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) &&
            property.PropertyType == typeof(string));
    }

    [Fact]
    public void Constructor_RejectsInsecureRemoteEndpoint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ViernesOptions(openRouterEndpoint: new Uri("http://example.test/chat")));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Constructor_RejectsUnsafeToolIterationLimits(int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ViernesOptions(maxToolIterations: iterations));
    }
}
