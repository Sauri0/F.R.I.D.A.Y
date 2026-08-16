namespace Viernes.Core.Intelligence;

/// <summary>
/// Contract only. A local implementation is preferred; configuring a remote model does not cause
/// this service to be created or called automatically.
/// </summary>
public interface IEmbeddingService
{
    bool IsLocal { get; }

    Task<EmbeddingResult> CreateAsync(string text, CancellationToken cancellationToken = default);
}
