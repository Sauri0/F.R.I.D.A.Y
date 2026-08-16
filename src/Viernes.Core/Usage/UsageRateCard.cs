using System.Collections.ObjectModel;
using System.Text.Json;
using Viernes.Core.Models;

namespace Viernes.Core.Usage;

/// <summary>
/// Runtime rate card. No built-in prices are supplied because provider prices are mutable; exact
/// provider-reported cost always takes precedence over estimates.
/// </summary>
public sealed class UsageRateCard
{
    private readonly ReadOnlyDictionary<string, ModelTokenRate> _rates;

    public UsageRateCard(IEnumerable<ModelTokenRate>? rates = null)
    {
        var dictionary = new Dictionary<string, ModelTokenRate>(StringComparer.OrdinalIgnoreCase);
        foreach (var rate in rates ?? [])
        {
            ArgumentNullException.ThrowIfNull(rate);
            if (!dictionary.TryAdd(rate.Model, rate))
            {
                throw new ArgumentException($"Duplicate rate for model '{rate.Model}'.", nameof(rates));
            }
        }

        _rates = new ReadOnlyDictionary<string, ModelTokenRate>(dictionary);
    }

    public IReadOnlyDictionary<string, ModelTokenRate> Rates => _rates;

    public decimal? EstimateCostUsd(string model, TokenUsage usage)
    {
        if (!_rates.TryGetValue(model, out var rate))
        {
            return null;
        }

        return decimal.Round(
            (usage.PromptTokens * rate.InputUsdPerMillionTokens +
             usage.CompletionTokens * rate.OutputUsdPerMillionTokens) / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Parses a JSON object keyed by exact model id. Each value needs
    /// <c>inputUsdPerMillion</c> and <c>outputUsdPerMillion</c>.
    /// </summary>
    public static UsageRateCard ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UsageRateCard();
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The rate card must be a JSON object.");
            }

            var rates = new List<ModelTokenRate>();
            foreach (var modelProperty in document.RootElement.EnumerateObject())
            {
                if (modelProperty.Value.ValueKind != JsonValueKind.Object ||
                    !TryReadDecimal(modelProperty.Value, "inputUsdPerMillion", out var input) ||
                    !TryReadDecimal(modelProperty.Value, "outputUsdPerMillion", out var output))
                {
                    throw new FormatException($"The rate for '{modelProperty.Name}' is incomplete.");
                }

                rates.Add(new ModelTokenRate(modelProperty.Name, input, output));
            }

            return new UsageRateCard(rates);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The configured rate card is not valid JSON.", exception);
        }
    }

    private static bool TryReadDecimal(JsonElement element, string name, out decimal value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) && property.TryGetDecimal(out value);
    }
}
