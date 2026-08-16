using Viernes.Memory.Models;

namespace Viernes.Memory;

/// <summary>
/// Memoria local revisable. Los argumentos son hechos breves ya destilados, nunca mensajes completos.
/// </summary>
public interface IPersonalMemoryStore
{
    string FilePath { get; }

    Task<MemoryReview> ReviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PersonalMemoryItem>> ListAsync(
        PersonalMemoryKind? kind = null,
        CancellationToken cancellationToken = default);

    Task<ExplicitMemory> AddExplicitAsync(
        string fact,
        CancellationToken cancellationToken = default);

    Task<ObservationCaptureResult> ObserveAsync(
        string distilledFact,
        double confidence,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    Task<MemorySuggestion> SuggestAsync(
        string distilledFact,
        Guid? basedOnObservationId = null,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default);

    Task<ExplicitMemory> ApproveSuggestionAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default);

    Task<bool> RejectSuggestionAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default);

    Task<PersonalMemoryItem> EditAsync(
        Guid itemId,
        string revisedFact,
        CancellationToken cancellationToken = default);

    Task<bool> ForgetAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<MemoryDeletionSummary> DeleteAllAsync(CancellationToken cancellationToken = default);

    Task PauseObservationAsync(CancellationToken cancellationToken = default);

    Task ResumeObservationAsync(CancellationToken cancellationToken = default);
}
