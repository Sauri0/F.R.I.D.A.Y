using System.Net;
using System.Text.Json;
using Viernes.Core.Configuration;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Tests.TestDoubles;
using Viernes.Core.Tools;
using Viernes.Core.Usage;
using Xunit;

namespace Viernes.Core.Tests.OpenRouter;

public sealed class OpenRouterChatClientTests
{
    [Fact]
    public async Task CompleteAsync_WithoutApiKey_ReturnsLocalModeWithoutNetworkRequest()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(httpClient, new ViernesOptions());

        var result = await client.CompleteAsync(
            [ConversationMessage.User("hola")],
            []);

        Assert.True(result.IsLocalMode);
        Assert.Null(result.Content);
        Assert.Empty(result.ToolCalls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_MapsConversationAndToolDefinitionsToOpenRouterSchema()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("respuesta", "provider/model"));
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient);
        var priorCall = new ToolCall(
            "call-prior",
            "reminder_create",
            JsonSerializer.SerializeToElement(new { title = "Comprar pan" }));
        var definition = ToolDefinition.Create(
            "reminder_create",
            "Crea un recordatorio local.",
            new
            {
                type = "object",
                properties = new { title = new { type = "string" } },
                required = new[] { "title" },
            });

        await client.CompleteAsync(
            [
                ConversationMessage.System("sistema"),
                ConversationMessage.User("recordame"),
                ConversationMessage.Assistant(null, [priorCall]),
                ConversationMessage.Tool("call-prior", "reminder_create", "{\"status\":\"Succeeded\"}"),
            ],
            [definition]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        var root = await ReadRequestJsonAsync(request);
        Assert.Equal("primary/model", root.GetProperty("model").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool"],
            messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        var mappedCall = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call-prior", mappedCall.GetProperty("id").GetString());
        Assert.Equal("reminder_create", mappedCall.GetProperty("function").GetProperty("name").GetString());
        using (var arguments = JsonDocument.Parse(
            mappedCall.GetProperty("function").GetProperty("arguments").GetString()!))
        {
            Assert.Equal("Comprar pan", arguments.RootElement.GetProperty("title").GetString());
        }

        Assert.Equal("call-prior", messages[3].GetProperty("tool_call_id").GetString());
        var mappedTool = root.GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("reminder_create", mappedTool.GetProperty("name").GetString());
        Assert.Equal("object", mappedTool.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MapsOpenRouterToolCallsToProviderNeutralResult()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "model": "provider/actual-model",
              "choices": [{
                "message": {
                  "content": null,
                  "tool_calls": [{
                    "id": "call-42",
                    "type": "function",
                    "function": {
                      "name": "agenda_list",
                      "arguments": "{\"limit\":3,\"include_notes\":false}"
                    }
                  }]
                }
              }]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient);

        var result = await client.CompleteAsync([ConversationMessage.User("mi agenda")], []);

        Assert.False(result.IsLocalMode);
        Assert.Equal("provider/actual-model", result.Model);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call-42", call.Id);
        Assert.Equal("agenda_list", call.Name);
        Assert.Equal(3, call.Arguments.GetProperty("limit").GetInt32());
        Assert.False(call.Arguments.GetProperty("include_notes").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_ParsesPromptAndCompletionTokenUsage()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "model": "provider/actual-model",
              "usage": { "prompt_tokens": 123, "completion_tokens": 45 },
              "choices": [{ "message": { "content": "listo" } }]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient);

        var result = await client.CompleteAsync([ConversationMessage.User("hola")], []);

        Assert.Equal(123, result.Usage.PromptTokens);
        Assert.Equal(45, result.Usage.CompletionTokens);
        Assert.Equal(168, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task CompleteAsync_ParsesExactCostAndConfiguredEstimateWithoutContent()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {
              "id": "provider-request-cost-1",
              "model": "provider/costed-model",
              "usage": { "prompt_tokens": 1000, "completion_tokens": 200, "cost": 0.0091 },
              "choices": [{ "message": { "content": "listo" } }]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var options = new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            model: "provider/costed-model",
            fallbackModels: [],
            usageRateCard: new UsageRateCard([
                new ModelTokenRate("provider/costed-model", 2m, 5m)
            ]));
        var client = new OpenRouterChatClient(httpClient, options);

        var result = await client.CompleteAsync([ConversationMessage.User("hola")], []);

        Assert.Equal(0.0091m, result.Cost.ExactUsd);
        Assert.Equal(0.003m, result.Cost.EstimatedUsd);
        Assert.Equal(0.0091m, result.Cost.EffectiveUsd);
        Assert.Equal("provider-request-cost-1", result.RequestId);
    }

    [Theory]
    [InlineData(ModelRole.Agent, "provider/agent")]
    [InlineData(ModelRole.Planning, "provider/agent")]
    [InlineData(ModelRole.Reasoning, "provider/reasoning")]
    public async Task CompleteAsync_ExplicitRole_UsesOnlyRequestedLane(
        ModelRole role,
        string expectedModel)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("ok", expectedModel));
        using var httpClient = new HttpClient(handler);
        var options = new ViernesOptions(
            apiKey: "unit-test-placeholder-token",
            model: "provider/fast",
            fallbackModels: ["provider/fast-backup"],
            agentModel: "provider/agent",
            reasoningModel: "provider/reasoning");
        var client = new OpenRouterChatClient(httpClient, options);

        await client.CompleteAsync(
            [ConversationMessage.User("tarea explícita")],
            [],
            new ModelSelectionRequest(role));

        Assert.Equal([expectedModel], await ReadRequestedModelsAsync(handler.Requests));
    }

    [Fact]
    public async Task CompleteAsync_PremiumWithoutApproval_IsBlockedBeforeNetwork()
    {
        var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new ViernesOptions(
                apiKey: "unit-test-placeholder-token",
                premiumModel: "provider/premium"));

        var exception = await Assert.ThrowsAsync<ModelSelectionException>(() => client.CompleteAsync(
            [ConversationMessage.User("usar premium")],
            [],
            new ModelSelectionRequest(ModelRole.Premium)));

        Assert.Equal(ModelSelectionStatus.RequiresExplicitApproval, exception.Selection.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompleteAsync_ApprovedPremium_UsesConfiguredPremiumModel()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("ok", "provider/premium"));
        using var httpClient = new HttpClient(handler);
        var client = new OpenRouterChatClient(
            httpClient,
            new ViernesOptions(
                apiKey: "unit-test-placeholder-token",
                premiumModel: "provider/premium"));

        await client.CompleteAsync(
            [ConversationMessage.User("usar premium")],
            [],
            new ModelSelectionRequest(ModelRole.Premium, PremiumApproved: true));

        Assert.Equal(["provider/premium"], await ReadRequestedModelsAsync(handler.Requests));
    }

    [Fact]
    public async Task CompleteAsync_RetriableStatus_UsesFallbackModelInOrder()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporary\"}");
        handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("ok", "backup/model"));
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient, fallbackModels: ["backup/model"]);

        var result = await client.CompleteAsync([ConversationMessage.User("hola")], []);

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            ["primary/model", "backup/model"],
            await ReadRequestedModelsAsync(handler.Requests));
    }

    [Fact]
    public async Task CompleteAsync_TransportFailure_UsesFallbackModel()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("test transport failure"));
        handler.EnqueueJson(HttpStatusCode.OK, SuccessResponse("recuperado", "backup/model"));
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient, fallbackModels: ["backup/model"]);

        var result = await client.CompleteAsync([ConversationMessage.User("hola")], []);

        Assert.Equal("recuperado", result.Content);
        Assert.Equal(
            ["primary/model", "backup/model"],
            await ReadRequestedModelsAsync(handler.Requests));
    }

    [Fact]
    public async Task CompleteAsync_AuthenticationFailure_DoesNotFallbackOrExposeProviderBody()
    {
        const string sensitiveProviderBody = "unit-test-provider-detail-that-must-not-leak";
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, $"{{\"error\":\"{sensitiveProviderBody}\"}}");
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient, fallbackModels: ["backup/model"]);

        var exception = await Assert.ThrowsAsync<OpenRouterException>(() =>
            client.CompleteAsync([ConversationMessage.User("hola")], []));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(["primary/model"], exception.AttemptedModels);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(sensitiveProviderBody, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_AllModelsFail_ReportsAttemptedModelsWithoutResponseBodies()
    {
        const string sensitiveProviderBody = "unit-test-internal-provider-detail";
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.TooManyRequests, $"{{\"error\":\"{sensitiveProviderBody}\"}}");
        handler.EnqueueJson(HttpStatusCode.NotFound, $"{{\"error\":\"{sensitiveProviderBody}\"}}");
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient, fallbackModels: ["backup/model"]);

        var exception = await Assert.ThrowsAsync<OpenRouterException>(() =>
            client.CompleteAsync([ConversationMessage.User("hola")], []));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(["primary/model", "backup/model"], exception.AttemptedModels);
        Assert.DoesNotContain(sensitiveProviderBody, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_InvalidToolArguments_ReturnsSanitizedProviderError()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(
            HttpStatusCode.OK,
            """
            {"choices":[{"message":{"tool_calls":[{"id":"call-1","function":{"name":"tool","arguments":"not-json"}}]}}]}
            """);
        using var httpClient = new HttpClient(handler);
        var client = CreateConfiguredClient(httpClient);

        var exception = await Assert.ThrowsAsync<OpenRouterException>(() =>
            client.CompleteAsync([ConversationMessage.User("hola")], []));

        Assert.Contains("argumentos", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
    }

    private static OpenRouterChatClient CreateConfiguredClient(
        HttpClient httpClient,
        IEnumerable<string>? fallbackModels = null) =>
        new(
            httpClient,
            new ViernesOptions(
                apiKey: "unit-test-placeholder-token",
                model: "primary/model",
                fallbackModels: fallbackModels ?? []));

    private static async Task<JsonElement> ReadRequestJsonAsync(HttpRequestMessage request)
    {
        var json = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string[]> ReadRequestedModelsAsync(
        IEnumerable<HttpRequestMessage> requests)
    {
        var models = new List<string>();
        foreach (var request in requests)
        {
            var root = await ReadRequestJsonAsync(request);
            models.Add(root.GetProperty("model").GetString()!);
        }

        return models.ToArray();
    }

    private static string SuccessResponse(string content, string model) =>
        JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { content } } },
        });
}
