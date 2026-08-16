namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>Prefiere Whisper local configurado y usa SAPI cuando no está disponible.</summary>
public sealed class SpeechRecognitionProviderSelector
{
    public SpeechRecognitionProviderSelection Select(SpeechRecognitionSelectionOptions? options = null)
    {
        var effectiveOptions = options ?? new SpeechRecognitionSelectionOptions();
        if (effectiveOptions.PreferWhisperLocal)
        {
            var whisper = new WhisperSpeechRecognitionProvider(effectiveOptions.Whisper);
            var whisperAvailability = whisper.GetAvailability();
            if (whisperAvailability.IsAvailable)
            {
                return new SpeechRecognitionProviderSelection(
                    whisper,
                    whisperAvailability,
                    UsedFallback: false);
            }

            whisper.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var sapiFallback = new SapiSpeechRecognitionProvider(effectiveOptions.Sapi);
            return new SpeechRecognitionProviderSelection(
                sapiFallback,
                sapiFallback.GetAvailability(),
                UsedFallback: true,
                FallbackReason: whisperAvailability.UnavailableReason);
        }

        var sapi = new SapiSpeechRecognitionProvider(effectiveOptions.Sapi);
        return new SpeechRecognitionProviderSelection(
            sapi,
            sapi.GetAvailability(),
            UsedFallback: false);
    }
}
