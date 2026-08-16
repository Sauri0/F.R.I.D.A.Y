using System.Collections.ObjectModel;

namespace Viernes.Core.Configuration;

/// <summary>
/// Configuración del Auto Router de OpenRouter. No contiene credenciales ni precios compilados:
/// sólo expresa qué banda de costo corresponde a cada rol y qué familias están permitidas.
/// </summary>
/// <remarks>
/// El router elige por gasto agregado de la comunidad en los últimos días, así que Viernes se
/// mantiene actualizado sin tocar código. A cambio, el modelo puede cambiar entre turnos: por eso
/// el ledger guarda el modelo realmente resuelto de cada completion.
/// </remarks>
public sealed class AutoRouterOptions
{
    /// <summary>Slug del router automático de OpenRouter.</summary>
    public const string AutoModelSlug = "openrouter/auto";

    private const int MaximumPatterns = 16;
    private const int MaximumPatternLength = 120;

    private static readonly IReadOnlyDictionary<ModelRole, ModelCostTier> DefaultTiers =
        new Dictionary<ModelRole, ModelCostTier>
        {
            [ModelRole.Fast] = ModelCostTier.Low,
            [ModelRole.Agent] = ModelCostTier.Medium,
            [ModelRole.Reasoning] = ModelCostTier.High,
            [ModelRole.Premium] = ModelCostTier.Max
        };

    private readonly ReadOnlyCollection<string> _allowedModels;
    private readonly ReadOnlyCollection<string> _excludedModels;
    private readonly IReadOnlyDictionary<ModelRole, ModelCostTier> _configuredTiers;

    public AutoRouterOptions(
        IEnumerable<string>? allowedModels = null,
        IEnumerable<string>? excludedModels = null,
        IEnumerable<KeyValuePair<ModelRole, ModelCostTier>>? costTiers = null,
        decimal? maxPromptPriceUsdPerMillion = null,
        decimal? maxCompletionPriceUsdPerMillion = null)
    {
        _allowedModels = NormalizePatterns(allowedModels);
        _excludedModels = NormalizePatterns(excludedModels);
        _configuredTiers = costTiers is null
            ? new Dictionary<ModelRole, ModelCostTier>()
            : costTiers
                .Where(pair => Enum.IsDefined(pair.Key) && Enum.IsDefined(pair.Value))
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Last().Value);
        MaxPromptPriceUsdPerMillion = ValidatePrice(maxPromptPriceUsdPerMillion, nameof(maxPromptPriceUsdPerMillion));
        MaxCompletionPriceUsdPerMillion = ValidatePrice(
            maxCompletionPriceUsdPerMillion,
            nameof(maxCompletionPriceUsdPerMillion));
    }

    /// <summary>Patrones permitidos, por ejemplo <c>anthropic/*</c>. Vacío significa sin restricción.</summary>
    public IReadOnlyList<string> AllowedModels => _allowedModels;

    public IReadOnlyList<string> ExcludedModels => _excludedModels;

    /// <summary>Techo duro de precio de entrada; los endpoints por encima se descartan.</summary>
    public decimal? MaxPromptPriceUsdPerMillion { get; }

    public decimal? MaxCompletionPriceUsdPerMillion { get; }

    public bool HasPriceCeiling =>
        MaxPromptPriceUsdPerMillion is not null || MaxCompletionPriceUsdPerMillion is not null;

    /// <summary>Banda configurada para el rol, o la predeterminada del rol si no se configuró.</summary>
    public ModelCostTier ResolveCostTier(ModelRole role)
    {
        var normalized = RoleBudgetNormalize(role);
        if (_configuredTiers.TryGetValue(normalized, out var configured))
        {
            return configured;
        }

        return DefaultTiers.TryGetValue(normalized, out var fallback) ? fallback : ModelCostTier.Low;
    }

    /// <summary>Valor textual que espera la API (<c>low</c>, <c>xhigh</c>, …).</summary>
    public static string ToWireValue(ModelCostTier tier) => tier switch
    {
        ModelCostTier.Low => "low",
        ModelCostTier.Medium => "medium",
        ModelCostTier.High => "high",
        ModelCostTier.XHigh => "xhigh",
        ModelCostTier.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public static bool TryParseCostTier(string? value, out ModelCostTier tier)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "low":
                tier = ModelCostTier.Low;
                return true;
            case "medium":
                tier = ModelCostTier.Medium;
                return true;
            case "high":
                tier = ModelCostTier.High;
                return true;
            case "xhigh":
                tier = ModelCostTier.XHigh;
                return true;
            case "max":
                tier = ModelCostTier.Max;
                return true;
            default:
                tier = ModelCostTier.Low;
                return false;
        }
    }

    /// <summary>True cuando el slug delega la elección en el router de OpenRouter.</summary>
    public static bool IsAutoRouted(string? model) =>
        model is not null &&
        model.Trim().StartsWith(AutoModelSlug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Id de plugin correspondiente al slug. Los ajustes enviados con el id del otro router se
    /// aceptan pero se ignoran en silencio, así que no deben mezclarse.
    /// </summary>
    public static string ResolvePluginId(string model) =>
        model.Trim().Equals("openrouter/auto-beta", StringComparison.OrdinalIgnoreCase)
            ? "auto-beta-router"
            : "auto-router";

    private static ModelRole RoleBudgetNormalize(ModelRole role) =>
        role == ModelRole.Planning ? ModelRole.Agent : role;

    private static ReadOnlyCollection<string> NormalizePatterns(IEnumerable<string>? patterns) =>
        Array.AsReadOnly((patterns ?? [])
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim())
            .Where(pattern => pattern.Length <= MaximumPatternLength && !pattern.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPatterns)
            .ToArray());

    private static decimal? ValidatePrice(decimal? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return value is >= 0 and <= 10_000
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Usá un techo entre 0 y 10000 USD por millón.");
    }
}
