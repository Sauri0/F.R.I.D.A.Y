using System.Net;
using System.Text.Json;
using Viernes.Core.Configuration;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Tests.TestDoubles;
using Xunit;

namespace Viernes.Core.Tests.OpenRouter;

public sealed class AutoRouterTests
{
    [Fact]
    public async Task CompleteAsync_WithAutoModel_SendsAutoRouterPluginAndStickySession()
    {
        var (client, handler, options) = CreateClient();

        await client.CompleteAsync([ConversationMessage.User("hola")], []);

        var root = await ReadRequestJsonAsync(Assert.Single(handler.Requests));
        Assert.Equal("openrouter/auto", root.GetProperty("model").GetString());
        Assert.Equal(options.SessionId, root.GetProperty("session_id").GetString());

        var plugin = Assert.Single(root.GetProperty("plugins").EnumerateArray().ToArray());
        Assert.Equal("auto-router", plugin.GetProperty("id").GetString());
        Assert.Equal("low", plugin.GetProperty("cost_tier").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MapsEachRoleToItsOwnCostTier()
    {
        var (client, handler, _) = CreateClient();

        await client.CompleteAsync([ConversationMessage.User("hola")], [], new ModelSelectionRequest(ModelRole.Fast));
        await client.CompleteAsync([ConversationMessage.User("hola")], [], new ModelSelectionRequest(ModelRole.Agent));
        await client.CompleteAsync(
            [ConversationMessage.User("hola")],
            [],
            new ModelSelectionRequest(ModelRole.Reasoning));

        var tiers = new List<string>();
        foreach (var request in handler.Requests)
        {
            var root = await ReadRequestJsonAsync(request);
            tiers.Add(root.GetProperty("plugins")[0].GetProperty("cost_tier").GetString()!);
        }

        Assert.Equal(["low", "medium", "high"], tiers);
    }

    [Fact]
    public async Task CompleteAsync_WithExplicitModel_SendsNoRouterPluginOrSession()
    {
        var (client, handler, _) = CreateClient(new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            model: "provider/fixed-model"));

        await client.CompleteAsync([ConversationMessage.User("hola")], []);

        var root = await ReadRequestJsonAsync(Assert.Single(handler.Requests));
        Assert.Equal("provider/fixed-model", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("plugins", out _));
        Assert.False(root.TryGetProperty("session_id", out _));
    }

    [Fact]
    public async Task CompleteAsync_WithPreset_SendsPresetSlugAndLetsTheServerOwnRouting()
    {
        var (client, handler, _) = CreateClient(new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            preset: "viernes-fast"));

        await client.CompleteAsync([ConversationMessage.User("hola")], []);

        var root = await ReadRequestJsonAsync(Assert.Single(handler.Requests));
        Assert.Equal("@preset/viernes-fast", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("plugins", out _));
    }

    [Fact]
    public async Task CompleteAsync_ForwardsAllowedAndExcludedModelPatterns()
    {
        var (client, handler, _) = CreateClient(new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            model: AutoRouterOptions.AutoModelSlug,
            autoRouter: new AutoRouterOptions(
                allowedModels: ["anthropic/*", "openai/*"],
                excludedModels: ["openai/o1-pro"])));

        await client.CompleteAsync([ConversationMessage.User("hola")], []);

        var plugin = (await ReadRequestJsonAsync(Assert.Single(handler.Requests))).GetProperty("plugins")[0];
        Assert.Equal(
            ["anthropic/*", "openai/*"],
            plugin.GetProperty("allowed_models").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["openai/o1-pro"],
            plugin.GetProperty("excluded_models").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task CompleteAsync_SendsPriceCeilingAsProviderPreference()
    {
        var (client, handler, _) = CreateClient(new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            autoRouter: new AutoRouterOptions(
                maxPromptPriceUsdPerMillion: 1.5m,
                maxCompletionPriceUsdPerMillion: 6m)));

        await client.CompleteAsync([ConversationMessage.User("hola")], []);

        var maxPrice = (await ReadRequestJsonAsync(Assert.Single(handler.Requests)))
            .GetProperty("provider")
            .GetProperty("max_price");
        Assert.Equal(1.5m, maxPrice.GetProperty("prompt").GetDecimal());
        Assert.Equal(6m, maxPrice.GetProperty("completion").GetDecimal());
    }

    [Fact]
    public async Task CompleteAsync_WithAutoModel_DoesNotChainLocalFallbackSlugs()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{}");
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new ViernesOptions(
                apiKey: "unit-test-placeholder-token",
                model: AutoRouterOptions.AutoModelSlug,
                fallbackModels: ["legacy/deprecated-model"]));

        await Assert.ThrowsAsync<OpenRouterException>(() =>
            client.CompleteAsync([ConversationMessage.User("hola")], []));

        // Una sola tentativa: reintentar contra un slug local sólo agregaría modelos deprecados.
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("low", ModelCostTier.Low)]
    [InlineData("  MEDIUM ", ModelCostTier.Medium)]
    [InlineData("xhigh", ModelCostTier.XHigh)]
    [InlineData("max", ModelCostTier.Max)]
    public void TryParseCostTier_AcceptsTheWireValues(string raw, ModelCostTier expected)
    {
        Assert.True(AutoRouterOptions.TryParseCostTier(raw, out var tier));
        Assert.Equal(expected, tier);
    }

    [Theory]
    [InlineData("cheap")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseCostTier_RejectsAnythingElse(string? raw) =>
        Assert.False(AutoRouterOptions.TryParseCostTier(raw, out _));

    [Fact]
    public void FromEnvironment_ReadsPresetAndPerRoleCostTiers()
    {
        var variables = new Dictionary<string, string?>
        {
            [ViernesOptions.PresetEnvironmentVariable] = "@preset/viernes-fast",
            [ViernesOptions.AllowedModelsEnvironmentVariable] = "anthropic/*, openai/*",
            [ViernesOptions.MaxPromptPriceEnvironmentVariable] = "2.5",
            [ViernesOptions.GetRoleCostTierEnvironmentVariable(ModelRole.Fast)] = "medium",
            [ViernesOptions.GetRoleCostTierEnvironmentVariable(ModelRole.Reasoning)] = "max"
        };

        var options = ViernesOptions.FromEnvironment(name =>
            variables.TryGetValue(name, out var value) ? value : null);

        // El prefijo @preset/ se acepta y se normaliza para no duplicarlo en el slug.
        Assert.Equal("viernes-fast", options.Preset);
        Assert.False(options.UsesAutoRouter);
        Assert.Equal(["anthropic/*", "openai/*"], options.AutoRouter.AllowedModels);
        Assert.Equal(2.5m, options.AutoRouter.MaxPromptPriceUsdPerMillion);
        Assert.Equal(ModelCostTier.Medium, options.AutoRouter.ResolveCostTier(ModelRole.Fast));
        Assert.Equal(ModelCostTier.Max, options.AutoRouter.ResolveCostTier(ModelRole.Reasoning));
        Assert.Equal(ModelCostTier.Medium, options.AutoRouter.ResolveCostTier(ModelRole.Agent));
    }

    [Fact]
    public void FromEnvironment_RejectsAnInvalidCostTier()
    {
        var variableName = ViernesOptions.GetRoleCostTierEnvironmentVariable(ModelRole.Fast);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ViernesOptions.FromEnvironment(name => name == variableName ? "barato" : null));

        Assert.Contains(variableName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePluginId_MatchesTheRouterSlug()
    {
        Assert.Equal("auto-router", AutoRouterOptions.ResolvePluginId("openrouter/auto"));
        Assert.Equal("auto-beta-router", AutoRouterOptions.ResolvePluginId("openrouter/auto-beta"));
    }

    private static (OpenRouterChatClient Client, StubHttpMessageHandler Handler, ViernesOptions Options)
        CreateClient(ViernesOptions? options = null)
    {
        // El router se pide explícitamente en vez de heredarlo del valor por defecto: estas pruebas
        // son sobre el comportamiento del router, y no tienen por qué romperse cuando el carril
        // rápido elige otro modelo. Declarar la premisa es parte de lo que la prueba afirma.
        options ??= new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            model: AutoRouterOptions.AutoModelSlug);
        var handler = new StubHttpMessageHandler();
        for (var index = 0; index < 4; index++)
        {
            handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("respuesta"));
        }

        var httpClient = new HttpClient(handler);
        return (new OpenRouterChatClient(httpClient, options), handler, options);
    }

    private static async Task<JsonElement> ReadRequestJsonAsync(HttpRequestMessage request)
    {
        var json = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SuccessResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            model = "provider/resolved-model",
            choices = new[] { new { message = new { content } } }
        });
}
