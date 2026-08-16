namespace Viernes.Memory;

public sealed record PersonalMemoryStoreOptions
{
    public int MaximumExplicitItems { get; init; } = 250;

    public int MaximumTemporaryObservations { get; init; } = 250;

    public int MaximumSuggestions { get; init; } = 100;

    public int MaximumTotalItems { get; init; } = 500;

    public int MaximumContentLength { get; init; } = 500;

    public long MaximumFileSizeBytes { get; init; } = 1024 * 1024;

    public double MinimumObservationConfidence { get; init; } = 0.60;

    public TimeSpan MinimumTemporaryLifetime { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan DefaultObservationLifetime { get; init; } = TimeSpan.FromDays(7);

    public TimeSpan MaximumObservationLifetime { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan DefaultSuggestionLifetime { get; init; } = TimeSpan.FromDays(14);

    public TimeSpan MaximumSuggestionLifetime { get; init; } = TimeSpan.FromDays(30);

    internal void Validate()
    {
        if (MaximumExplicitItems <= 0 || MaximumTemporaryObservations <= 0 ||
            MaximumSuggestions <= 0 || MaximumTotalItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalItems), "Los límites de elementos deben ser positivos.");
        }

        if (MaximumContentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumContentLength));
        }

        if (MaximumFileSizeBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileSizeBytes));
        }

        if (MinimumObservationConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumObservationConfidence));
        }

        if (MinimumTemporaryLifetime <= TimeSpan.Zero ||
            DefaultObservationLifetime < MinimumTemporaryLifetime ||
            MaximumObservationLifetime < DefaultObservationLifetime ||
            DefaultSuggestionLifetime < MinimumTemporaryLifetime ||
            MaximumSuggestionLifetime < DefaultSuggestionLifetime)
        {
            throw new ArgumentException("Los límites de expiración de memoria no son coherentes.");
        }
    }
}
