namespace Viernes.Platform.Windows.Speech.WakeWord;

public enum WakeWordServiceState
{
    Stopped = 0,
    Listening,
    Muted,
    Unavailable,
    Faulted
}
