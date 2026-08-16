namespace Viernes.Platform.Windows.Speech;

public sealed record InstalledSpeechVoice(string Name, string Culture);

public sealed record SpeechCapabilities(
    bool IsRecognitionAvailable,
    bool IsSynthesisAvailable,
    IReadOnlyList<string> RecognitionCultures,
    IReadOnlyList<InstalledSpeechVoice> Voices,
    string? ErrorMessage = null);
