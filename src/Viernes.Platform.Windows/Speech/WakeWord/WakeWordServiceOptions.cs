using System.Globalization;

namespace Viernes.Platform.Windows.Speech.WakeWord;

public sealed record WakeWordServiceOptions
{
    public IReadOnlyList<string> Phrases { get; init; } = ["Viernes", "Hola Viernes"];

    public string RecognitionCulture { get; init; } = "es-AR";

    public float MinimumConfidence { get; init; } = 0.78f;

    internal string[] ValidateAndNormalizePhrases()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RecognitionCulture);
        _ = CultureInfo.GetCultureInfo(RecognitionCulture);
        if (MinimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumConfidence));
        }

        if (Phrases is null || Phrases.Count is < 1 or > 8)
        {
            throw new ArgumentException("Configurá entre 1 y 8 frases de activación.", nameof(Phrases));
        }

        var normalized = Phrases
            .Select(phrase => string.Join(' ', (phrase ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();
        if (normalized.Any(phrase => phrase.Length is < 2 or > 40 || phrase.Any(char.IsControl)))
        {
            throw new ArgumentException("Cada frase debe contener entre 2 y 40 caracteres seguros.", nameof(Phrases));
        }

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
