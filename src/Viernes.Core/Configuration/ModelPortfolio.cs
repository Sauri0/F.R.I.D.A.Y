using System.Collections.ObjectModel;

namespace Viernes.Core.Configuration;

/// <summary>
/// Immutable, explicitly selected model portfolio. It contains configuration only and performs no
/// routing, network requests, or budget decisions.
/// </summary>
public sealed class ModelPortfolio
{
    private readonly ReadOnlyCollection<string> _fastAlternatives;

    public ModelPortfolio(
        string fastModel,
        IEnumerable<string>? fastAlternatives,
        string agentModel,
        string reasoningModel,
        string? premiumModel = null,
        string? embeddingsModel = null,
        string? localSummaryModel = null,
        bool preferLocalEmbeddings = true,
        bool preferLocalSummary = true)
    {
        FastModel = RequiredModel(fastModel, nameof(fastModel));
        AgentModel = RequiredModel(agentModel, nameof(agentModel));
        ReasoningModel = RequiredModel(reasoningModel, nameof(reasoningModel));
        PremiumModel = OptionalModel(premiumModel);
        EmbeddingsModel = OptionalModel(embeddingsModel);
        LocalSummaryModel = OptionalModel(localSummaryModel);
        PreferLocalEmbeddings = preferLocalEmbeddings;
        PreferLocalSummary = preferLocalSummary;
        _fastAlternatives = Array.AsReadOnly((fastAlternatives ?? [])
            .Select(OptionalModel)
            .Where(model => model is not null &&
                            !string.Equals(model, FastModel, StringComparison.OrdinalIgnoreCase))
            .Select(model => model!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray());
    }

    public string FastModel { get; }

    public IReadOnlyList<string> FastAlternatives => _fastAlternatives;

    public string AgentModel { get; }

    public string ReasoningModel { get; }

    public string? PremiumModel { get; }

    public string? EmbeddingsModel { get; }

    public string? LocalSummaryModel { get; }

    public bool PreferLocalEmbeddings { get; }

    public bool PreferLocalSummary { get; }

    public ModelSelection Select(ModelSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Role switch
        {
            ModelRole.Fast => Ready(request.Role, FastModel),
            ModelRole.Planning or ModelRole.Agent => Ready(request.Role, AgentModel),
            ModelRole.Reasoning => Ready(request.Role, ReasoningModel),
            ModelRole.Premium => SelectPremium(request),
            ModelRole.Embeddings => SelectLocalPreferred(
                request,
                EmbeddingsModel,
                PreferLocalEmbeddings,
                "embeddings"),
            ModelRole.LocalSummary => SelectLocalPreferred(
                request,
                LocalSummaryModel,
                PreferLocalSummary,
                "resúmenes"),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown model role.")
        };
    }

    private ModelSelection SelectPremium(ModelSelectionRequest request)
    {
        if (PremiumModel is null)
        {
            return new ModelSelection(
                request.Role,
                null,
                ModelSelectionStatus.Unavailable,
                "No hay un modelo Premium configurado.");
        }

        return request.PremiumApproved
            ? Ready(request.Role, PremiumModel)
            : new ModelSelection(
                request.Role,
                PremiumModel,
                ModelSelectionStatus.RequiresExplicitApproval,
                "El lane Premium requiere aprobación explícita del usuario.");
    }

    private static ModelSelection SelectLocalPreferred(
        ModelSelectionRequest request,
        string? remoteModel,
        bool preferLocal,
        string capability)
    {
        if (preferLocal && !request.AllowRemoteForLocalPreferredRole)
        {
            return new ModelSelection(
                request.Role,
                remoteModel,
                ModelSelectionStatus.LocalPreferred,
                $"Viernes prefiere ejecutar {capability} localmente.");
        }

        return remoteModel is null
            ? new ModelSelection(
                request.Role,
                null,
                ModelSelectionStatus.Unavailable,
                $"No hay un modelo remoto configurado para {capability}.")
            : Ready(request.Role, remoteModel);
    }

    private static ModelSelection Ready(ModelRole role, string model) => new(
        role,
        model,
        ModelSelectionStatus.Ready,
        "Modelo seleccionado explícitamente.");

    private static string RequiredModel(string value, string parameterName) =>
        OptionalModel(value) ?? throw new ArgumentException("A model id is required.", parameterName);

    private static string? OptionalModel(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
