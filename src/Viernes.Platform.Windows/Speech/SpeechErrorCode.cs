namespace Viernes.Platform.Windows.Speech;

public enum SpeechErrorCode
{
    None = 0,
    MicrophoneMuted,
    Unavailable,
    InvalidInput,
    Cancelled,
    TimedOut,
    DeviceError,
    Failed,
    Disposed
}
