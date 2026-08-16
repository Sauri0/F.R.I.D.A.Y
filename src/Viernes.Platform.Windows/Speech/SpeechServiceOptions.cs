namespace Viernes.Platform.Windows.Speech;

public sealed record SpeechServiceOptions
{
    public string RecognitionCulture { get; init; } = "es-AR";

    public string SynthesisCulture { get; init; } = "es-AR";

    public string? PreferredVoiceName { get; init; }

    public float MinimumConfidence { get; init; } = 0.30f;

    public bool EmitPartialTranscriptions { get; init; } = true;

    public TimeSpan InitialSilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan EndSilenceTimeout { get; init; } = TimeSpan.FromMilliseconds(700);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(4);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RecognitionCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(SynthesisCulture);

        if (MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumConfidence), "La confianza debe estar entre 0 y 1.");
        }

        if (InitialSilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialSilenceTimeout));
        }

        if (EndSilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(EndSilenceTimeout));
        }

        if (StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopTimeout));
        }
    }
}
