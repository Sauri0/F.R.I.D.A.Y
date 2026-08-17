using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Speech.WakeWord;

public sealed record WakeWordHandoffResult(
    SpeechRecognitionResult Recognition,
    bool WakeWordWasStopped,
    bool WakeWordWasResumed,
    string? CoordinationError = null);

/// <summary>
/// Cede el micrófono de wake-word al reconocimiento de una sola frase y lo devuelve después.
/// Nunca mantiene ambos motores escuchando al mismo tiempo.
/// </summary>
public sealed class WakeWordRecognitionCoordinator
{
    public async Task<WakeWordHandoffResult> RecognizeAfterWakeAsync(
        IWakeWordService wakeWordService,
        ISpeechRecognitionProvider recognitionProvider,
        SingleUtteranceRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wakeWordService);
        ArgumentNullException.ThrowIfNull(recognitionProvider);

        var shouldResume = wakeWordService.State == WakeWordServiceState.Listening && !wakeWordService.IsMuted;
        var stopped = await wakeWordService.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded || wakeWordService.IsMicrophoneActive)
        {
            var message = stopped.ErrorMessage ?? "El detector wake-word no liberó el micrófono.";
            return new WakeWordHandoffResult(
                SpeechRecognitionResult.Failure(SpeechErrorCode.DeviceError, message),
                WakeWordWasStopped: false,
                WakeWordWasResumed: false,
                CoordinationError: message);
        }

        SpeechRecognitionResult recognition;
        var resumed = false;
        string? coordinationError = null;
        try
        {
            try
            {
                // SAPI informa que soltó el micrófono antes de que el driver termine de liberarlo.
                // Sin esta pausa, la captura abre el dispositivo y no recibe una sola muestra.
                await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken).ConfigureAwait(false);

                recognition = await recognitionProvider
                    .RecognizeSingleUtteranceAsync(options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                recognition = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Cancelled,
                    "La captura posterior a la activación fue cancelada.");
            }
            catch (Exception exception)
            {
                recognition = SpeechRecognitionResult.Failure(
                    SpeechErrorCode.Failed,
                    $"La captura posterior a la activación falló: {exception.Message}");
            }
        }
        finally
        {
            if (recognitionProvider.IsMicrophoneActive)
            {
                await recognitionProvider.CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (shouldResume && !wakeWordService.IsMuted)
            {
                var resumeResult = await wakeWordService.StartAsync(CancellationToken.None).ConfigureAwait(false);
                resumed = resumeResult.Succeeded;
                coordinationError = resumeResult.Succeeded ? null : resumeResult.ErrorMessage;
            }
        }

        return new WakeWordHandoffResult(
            recognition,
            WakeWordWasStopped: true,
            WakeWordWasResumed: resumed,
            CoordinationError: coordinationError);
    }
}
