namespace Viernes.Platform.Windows.Speech.Recognition;

public sealed record SpeechRecognitionSelectionOptions
{
    public bool PreferWhisperLocal { get; init; } = true;

    public WhisperSpeechRecognitionOptions Whisper { get; init; } = new();

    public SpeechServiceOptions Sapi { get; init; } = new();
}
