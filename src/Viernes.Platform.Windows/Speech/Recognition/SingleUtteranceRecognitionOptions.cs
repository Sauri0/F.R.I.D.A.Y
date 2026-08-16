namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Límites del modo hands-free. El umbral energético es un VAD simple de demostración,
/// sensible al micrófono y al ruido ambiente; no se presenta como detector robusto.
/// </summary>
public sealed record SingleUtteranceRecognitionOptions
{
    public TimeSpan InitialSilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan EndSilenceTimeout { get; init; } = TimeSpan.FromMilliseconds(850);

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(15);

    public float VoiceEnergyThreshold { get; init; } = 0.018f;

    internal void Validate()
    {
        if (InitialSilenceTimeout <= TimeSpan.Zero ||
            EndSilenceTimeout < TimeSpan.FromMilliseconds(200) ||
            EndSilenceTimeout > TimeSpan.FromSeconds(3) ||
            MaximumDuration <= InitialSilenceTimeout || MaximumDuration > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("Los timeouts de reconocimiento de una frase no son válidos.");
        }

        if (VoiceEnergyThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(VoiceEnergyThreshold));
        }
    }
}
