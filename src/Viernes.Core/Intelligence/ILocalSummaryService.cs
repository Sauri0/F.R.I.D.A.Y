namespace Viernes.Core.Intelligence;

/// <summary>
/// Contract for an on-device summarizer. Implementations must not send content to remote services.
/// </summary>
public interface ILocalSummaryService
{
    bool IsAvailable { get; }

    Task<string> SummarizeAsync(
        string content,
        int maximumOutputCharacters,
        CancellationToken cancellationToken = default);
}
