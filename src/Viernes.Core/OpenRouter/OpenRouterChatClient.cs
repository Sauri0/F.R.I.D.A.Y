using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.Tools;
using Viernes.Core.Usage;

namespace Viernes.Core.OpenRouter;

/// <summary>Minimal OpenRouter chat-completions client with model fallback and tool calling.</summary>
public sealed class OpenRouterChatClient : IRoleAwareChatCompletionClient
{
    private const long MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    private readonly HttpClient _httpClient;
    private readonly ViernesOptions _options;

    public OpenRouterChatClient(HttpClient httpClient, ViernesOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var selection = _options.SelectModel(new ModelSelectionRequest(ModelRole.Fast));
        return await CompleteSelectedAsync(
            messages,
            tools,
            selection,
            _options.FallbackModels,
            cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyList<string> NoFallbacks = Array.Empty<string>();

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelSelectionRequest selectionRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectionRequest);
        if (selectionRequest.Role == ModelRole.Embeddings)
        {
            throw new ArgumentException(
                "The Embeddings lane is a configuration contract, not a chat-completions lane.",
                nameof(selectionRequest));
        }

        var selection = _options.SelectModel(selectionRequest);
        if (!selection.CanSendRemoteRequest)
        {
            throw new ModelSelectionException(selection);
        }

        var fallbacks = selectionRequest.Role == ModelRole.Fast
            ? _options.FallbackModels
            : Array.Empty<string>();
        return await CompleteSelectedAsync(
            messages,
            tools,
            selection,
            fallbacks,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatCompletionResult> CompleteSelectedAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelSelection selection,
        IReadOnlyList<string> fallbackModels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        cancellationToken.ThrowIfCancellationRequested();

        var apiKey = _options.GetApiKey();
        if (apiKey is null)
        {
            // Local mode is a first-class non-error state and performs no network request.
            return ChatCompletionResult.LocalMode();
        }

        // Con preset o router automático, OpenRouter ya elige alternativas del lado del servidor:
        // encadenar slugs locales sólo agregaría candidatos que pueden haber sido deprecados.
        var effectiveFallbacks = _options.Preset is not null || _options.UsesAutoRouter
            ? NoFallbacks
            : fallbackModels;
        var candidates = new[] { selection.Model! }
            .Concat(effectiveFallbacks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var attempted = new List<string>(candidates.Length);
        HttpStatusCode? lastStatusCode = null;
        Exception? lastTransportError = null;

        foreach (var model in candidates)
        {
            attempted.Add(model);
            using var request = CreateRequest(apiKey, model, selection.Role, messages, tools);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                lastTransportError = exception;
                if (attempted.Count < candidates.Length)
                {
                    continue;
                }

                throw new OpenRouterException(
                    "No se pudo contactar a OpenRouter.",
                    attemptedModels: attempted.ToArray(),
                    innerException: exception);
            }

            using (response)
            {
                lastStatusCode = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return await ParseResponseAsync(
                        response,
                        model,
                        _options.RateCard,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!IsFallbackEligible(response.StatusCode) || attempted.Count == candidates.Length)
                {
                    throw new OpenRouterException(
                        SafeStatusMessage(response.StatusCode),
                        response.StatusCode,
                        attempted.ToArray());
                }
            }
        }

        throw new OpenRouterException(
            "OpenRouter no pudo completar la solicitud con los modelos configurados.",
            lastStatusCode,
            attempted.ToArray(),
            lastTransportError);
    }

    private HttpRequestMessage CreateRequest(
        string apiKey,
        string model,
        ModelRole role,
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var requestModel = _options.ResolveRequestModel(model);
        var plugins = BuildAutoRouterPlugins(requestModel, role);
        var payload = new
        {
            model = requestModel,
            messages = messages.Select(MapMessage).ToArray(),
            tools = tools.Count == 0 ? null : tools.Select(MapTool).ToArray(),
            tool_choice = tools.Count == 0 ? null : "auto",
            // El router prefiere el modelo ya elegido para esta conversación mientras siga entre los
            // mejores candidatos, así un diálogo no salta de voz entre turnos.
            session_id = AutoRouterOptions.IsAutoRouted(requestModel) ? _options.SessionId : null,
            plugins,
            provider = BuildProviderPreferences()
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _options.OpenRouterEndpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("X-Title", _options.ApplicationName);
        return request;
    }

    /// <summary>
    /// Configura el Auto Router sólo cuando el slug lo delega. Un preset ya lleva su propio routing
    /// del lado del servidor, así que no se pisa desde acá.
    /// </summary>
    private object[]? BuildAutoRouterPlugins(string requestModel, ModelRole role)
    {
        var plugins = new List<object>(2);

        if (AutoRouterOptions.IsAutoRouted(requestModel))
        {
            var router = _options.AutoRouter;
            plugins.Add(new
            {
                id = AutoRouterOptions.ResolvePluginId(requestModel),
                cost_tier = AutoRouterOptions.ToWireValue(router.ResolveCostTier(role)),
                allowed_models = router.AllowedModels.Count == 0 ? null : router.AllowedModels.ToArray(),
                excluded_models = router.ExcludedModels.Count == 0 ? null : router.ExcludedModels.ToArray()
            });
        }

        // Búsqueda real: el proveedor inyecta resultados en el contexto del turno. Tiene costo
        // propio por búsqueda, por eso viene apagada salvo que se la pida explícitamente.
        if (_options.WebSearchEnabled)
        {
            plugins.Add(new { id = "web", max_results = _options.WebSearchMaxResults });
        }

        return plugins.Count == 0 ? null : plugins.ToArray();
    }

    /// <summary>Techo de precio por millón de tokens; los endpoints por encima quedan descartados.</summary>
    private object? BuildProviderPreferences()
    {
        var router = _options.AutoRouter;
        if (!router.HasPriceCeiling)
        {
            return null;
        }

        return new
        {
            max_price = new
            {
                prompt = router.MaxPromptPriceUsdPerMillion,
                completion = router.MaxCompletionPriceUsdPerMillion
            }
        };
    }

    private static object MapMessage(ConversationMessage message) => message.Role switch
    {
        ConversationRole.System => new { role = "system", content = message.Content },
        ConversationRole.User => new { role = "user", content = message.Content },
        ConversationRole.Assistant => new
        {
            role = "assistant",
            content = message.Content,
            tool_calls = message.ToolCalls?.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = call.Arguments.GetRawText()
                }
            }).ToArray()
        },
        ConversationRole.Tool => new
        {
            role = "tool",
            content = message.Content,
            tool_call_id = message.ToolCallId,
            name = message.Name
        },
        _ => throw new ArgumentOutOfRangeException(nameof(message), "Unsupported conversation role.")
    };

    private static object MapTool(ToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = tool.Parameters
        }
    };

    private static async Task<ChatCompletionResult> ParseResponseAsync(
        HttpResponseMessage response,
        string requestedModel,
        UsageRateCard rateCard,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new OpenRouterException("OpenRouter devolvió una respuesta demasiado grande.", response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limitedStream = new LengthLimitedReadStream(stream, MaximumResponseBytes);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                limitedStream,
                new JsonDocumentOptions { MaxDepth = 64 },
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new OpenRouterException("OpenRouter devolvió JSON no válido.", response.StatusCode, innerException: exception);
        }
        catch (InvalidDataException exception)
        {
            throw new OpenRouterException("OpenRouter devolvió una respuesta demasiado grande.", response.StatusCode, innerException: exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message))
            {
                throw new OpenRouterException("OpenRouter devolvió una respuesta incompleta.", response.StatusCode);
            }

            string? content = null;
            if (message.TryGetProperty("content", out var contentElement) &&
                contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString();
            }

            var calls = ParseToolCalls(message);
            var model = root.TryGetProperty("model", out var modelElement) &&
                        modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : requestedModel;
            var usage = ParseUsage(root);
            var cost = new UsageCost(
                ParseExactCost(root),
                model is null || !HasUsageObject(root)
                    ? null
                    : rateCard.EstimateCostUsd(model, usage));

            if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
            {
                throw new OpenRouterException("OpenRouter no devolvió texto ni herramientas.", response.StatusCode);
            }

            var requestId = root.TryGetProperty("id", out var idElement) &&
                            idElement.ValueKind == JsonValueKind.String &&
                            idElement.GetString() is { Length: > 0 and <= 128 } id
                ? id
                : null;
            return new ChatCompletionResult(
                content,
                calls,
                model,
                Usage: usage,
                Cost: cost,
                RequestId: requestId);
        }
    }

    private static TokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Zero;
        }

        var promptTokens = ReadNonNegativeTokenCount(usage, "prompt_tokens");
        var completionTokens = ReadNonNegativeTokenCount(usage, "completion_tokens");
        return new TokenUsage(promptTokens, completionTokens);
    }

    private static bool HasUsageObject(JsonElement root) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;

    private static long ReadNonNegativeTokenCount(JsonElement usage, string propertyName)
    {
        if (!usage.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (!value.TryGetInt64(out var count) || count < 0)
        {
            throw new OpenRouterException("OpenRouter devolvió métricas de uso no válidas.");
        }

        return count;
    }

    private static decimal? ParseExactCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("cost", out var costElement))
        {
            return null;
        }

        decimal cost;
        if (costElement.ValueKind == JsonValueKind.Number)
        {
            if (!costElement.TryGetDecimal(out cost))
            {
                throw new OpenRouterException("OpenRouter devolvió un costo de uso no válido.");
            }
        }
        else if (costElement.ValueKind == JsonValueKind.String &&
                 decimal.TryParse(
                     costElement.GetString(),
                     System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out cost))
        {
            // Some provider adapters serialize the normalized cost as text.
        }
        else
        {
            throw new OpenRouterException("OpenRouter devolvió un costo de uso no válido.");
        }

        if (cost is < 0 or > 1_000_000)
        {
            throw new OpenRouterException("OpenRouter devolvió un costo de uso fuera de rango.");
        }

        return cost;
    }

    private static IReadOnlyList<ToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement) ||
            callsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<ToolCall>();
        }

        if (callsElement.ValueKind != JsonValueKind.Array)
        {
            throw new OpenRouterException("OpenRouter devolvió herramientas no válidas.");
        }

        var result = new List<ToolCall>();
        foreach (var callElement in callsElement.EnumerateArray())
        {
            if (!callElement.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !callElement.TryGetProperty("function", out var functionElement) ||
                !functionElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !functionElement.TryGetProperty("arguments", out var argumentsElement) ||
                argumentsElement.ValueKind != JsonValueKind.String)
            {
                throw new OpenRouterException("OpenRouter devolvió una llamada de herramienta incompleta.");
            }

            var id = idElement.GetString();
            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                throw new OpenRouterException("OpenRouter devolvió una llamada de herramienta incompleta.");
            }

            try
            {
                using var argumentsDocument = JsonDocument.Parse(
                    argumentsElement.GetString() ?? "{}",
                    new JsonDocumentOptions { MaxDepth = 32 });
                result.Add(new ToolCall(id, name, argumentsDocument.RootElement.Clone()));
            }
            catch (JsonException exception)
            {
                throw new OpenRouterException(
                    "OpenRouter devolvió argumentos de herramienta no válidos.",
                    innerException: exception);
            }
        }

        return result;
    }

    private static bool IsFallbackEligible(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static string SafeStatusMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "OpenRouter rechazó la credencial configurada.",
        HttpStatusCode.BadRequest =>
            "OpenRouter rechazó la solicitud. Revisá el modelo y las herramientas configuradas.",
        (HttpStatusCode)429 =>
            "OpenRouter está limitando temporalmente las solicitudes.",
        _ => $"OpenRouter no pudo completar la solicitud (HTTP {(int)statusCode})."
    };

    private sealed class LengthLimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, LimitCount(count));
            Track(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..LimitCount(buffer.Length)]);
            Track(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..LimitCount(buffer.Length)], cancellationToken).ConfigureAwait(false);
            Track(read);
            return read;
        }

        private int LimitCount(int requested)
        {
            if (_bytesRead >= maximumBytes)
            {
                // A final one-byte read distinguishes an exact-limit payload from an oversized one.
                return 1;
            }

            return (int)Math.Min(requested, maximumBytes - _bytesRead);
        }

        private void Track(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maximumBytes)
            {
                throw new InvalidDataException("Response limit exceeded.");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
