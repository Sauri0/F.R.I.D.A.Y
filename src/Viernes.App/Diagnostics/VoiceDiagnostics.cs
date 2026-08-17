using System.Net.Http;
using System.Text;
using Viernes.Core.Configuration;
using Viernes.Core.Voice;
using Viernes.Platform.Windows.Speech;
using Viernes.Platform.Windows.Speech.Recognition;
using Viernes.Platform.Windows.Speech.WakeWord;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Ejercita los caminos reales de voz y reporta dónde se cortan. Existe porque «no habló» y «no me
/// reconoce» son síntomas, y sin ver el punto exacto de falla lo único que queda es adivinar.
/// </summary>
/// <remarks>Se invoca con <c>Viernes.exe --check-voice</c> y termina el proceso al finalizar.</remarks>
internal static class VoiceDiagnostics
{
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        var options = ViernesOptions.FromEnvironment();

        report.AppendLine("== CLAVE Y ROUTING ==");
        report.AppendLine($"OPENROUTER_API_KEY presente : {options.HasApiKey}");
        report.AppendLine($"Modelo configurado          : {options.Model}");

        await CheckRecognitionAsync(report);
        await CheckWakeWordAsync(report);
        await CheckLocalVoiceAsync(report);
        await CheckNeuralVoiceAsync(report, options);

        return report.ToString();
    }

    private static Task CheckRecognitionAsync(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("== RECONOCIMIENTO (STT) ==");
        try
        {
            var selection = new SpeechRecognitionProviderSelector().Select(new SpeechRecognitionSelectionOptions
            {
                PreferWhisperLocal = true,
                Whisper = new WhisperSpeechRecognitionOptions
                {
                    ModelPath = WhisperSpeechRecognitionOptions.GetDefaultModelPath(),
                    Language = "es"
                },
                Sapi = new SpeechServiceOptions { RecognitionCulture = "es-AR", SynthesisCulture = "es-AR" }
            });

            report.AppendLine($"Proveedor elegido : {selection.Provider.Info.DisplayName}");
            report.AppendLine($"Disponible        : {selection.Availability.IsAvailable}");
            report.AppendLine($"Usó respaldo      : {selection.UsedFallback}");
            if (!string.IsNullOrWhiteSpace(selection.FallbackReason))
            {
                report.AppendLine($"Motivo del respaldo: {selection.FallbackReason}");
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"FALLÓ: {exception.GetType().Name} · {exception.Message}");
        }

        return Task.CompletedTask;
    }

    private static async Task CheckWakeWordAsync(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("== ACTIVACIÓN POR VOZ ==");

        await using var wake = new SapiWakeWordService(new WakeWordServiceOptions
        {
            Phrases = ["Viernes", "Hola Viernes"],
            RecognitionCulture = "es-AR",
            MinimumConfidence = 0.78f
        });

        var errors = new List<string>();
        wake.ServiceError += (_, e) => errors.Add($"{e.ErrorCode}: {e.Message}");

        var started = await wake.StartAsync();
        report.AppendLine($"StartAsync         : {(started.Succeeded ? "OK" : "FALLÓ")}");
        if (!started.Succeeded)
        {
            report.AppendLine($"  código  : {started.ErrorCode}");
            report.AppendLine($"  mensaje : {started.ErrorMessage}");
        }

        report.AppendLine($"Estado             : {wake.State}");
        report.AppendLine($"Micrófono activo   : {wake.IsMicrophoneActive}");
        report.AppendLine($"Frases             : {string.Join(" · ", wake.Phrases)}");

        // Cinco segundos escuchando de verdad: si algo se cae, se cae acá.
        await Task.Delay(TimeSpan.FromSeconds(5));
        report.AppendLine($"Estado tras 5 s    : {wake.State}");
        foreach (var error in errors)
        {
            report.AppendLine($"  error: {error}");
        }

        await wake.StopAsync();
    }

    private static async Task CheckLocalVoiceAsync(StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("== VOZ LOCAL (SAPI) ==");
        await using var speech = new SpeechService(new SpeechServiceOptions
        {
            RecognitionCulture = "es-AR",
            SynthesisCulture = "es-AR",
            EmitPartialTranscriptions = false
        });

        var spoken = await speech.SpeakAsync("Probando la voz local de Viernes.");
        report.AppendLine($"SpeakAsync         : {(spoken.Succeeded ? "OK (deberías haberlo escuchado)" : "FALLÓ")}");
        if (!spoken.Succeeded)
        {
            report.AppendLine($"  código  : {spoken.ErrorCode}");
            report.AppendLine($"  mensaje : {spoken.ErrorMessage}");
        }
    }

    private static async Task CheckNeuralVoiceAsync(StringBuilder report, ViernesOptions options)
    {
        report.AppendLine();
        report.AppendLine("== VOZ NEURAL (OpenRouter) ==");

        var speechOptions = SpeechSynthesisOptions.FromEnvironment();
        report.AppendLine($"Habilitada  : {speechOptions.IsEnabled}");
        report.AppendLine($"Modelo      : {speechOptions.Model}");
        report.AppendLine($"Voz         : {speechOptions.Voice}");
        report.AppendLine($"Endpoint    : {OpenRouterSpeechClient.ResolveSpeechEndpoint(options.OpenRouterEndpoint)}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var client = new OpenRouterSpeechClient(http, options, speechOptions);
        report.AppendLine($"IsAvailable : {client.IsAvailable}");

        if (!client.IsAvailable)
        {
            return;
        }

        // Llamada cruda: el cliente se traga el motivo del fallo, y acá el motivo es justamente lo
        // que hace falta ver.
        await ProbeRawAsync(report, http, options, speechOptions);

        var audio = await client.SynthesizeAsync("Probando la voz neural de Viernes.");
        if (audio is null)
        {
            report.AppendLine($"SynthesizeAsync: falló — {client.LastFailure ?? "sin motivo informado"}");
            return;
        }

        report.AppendLine($"Audio recibido : {audio.Pcm.Length} bytes a {audio.SampleRate} Hz");

        await using var player = new NeuralSpeechPlayer();
        var played = await player.PlayAsync(audio.Pcm, audio.SampleRate);
        report.AppendLine($"PlayAsync      : {(played ? "OK (deberías haberlo escuchado)" : "FALLÓ")}");
    }

    private static async Task ProbeRawAsync(
        StringBuilder report,
        HttpClient http,
        ViernesOptions options,
        SpeechSynthesisOptions speech)
    {
        report.AppendLine();
        report.AppendLine("-- llamada cruda a /audio/speech --");

        foreach (var format in new[] { "pcm", "mp3" })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                OpenRouterSpeechClient.ResolveSpeechEndpoint(options.OpenRouterEndpoint))
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    model = speech.Model,
                    input = "Probando.",
                    voice = speech.Voice,
                    response_format = format
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));

            try
            {
                using var response = await http.SendAsync(request);
                report.AppendLine($"[{format}] HTTP {(int)response.StatusCode} {response.StatusCode}");
                report.AppendLine($"[{format}] Content-Type: {response.Content.Headers.ContentType}");

                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    report.AppendLine($"[{format}] bytes: {bytes.Length}");
                    report.AppendLine($"[{format}] head : {string.Join(" ", bytes.Take(12).Select(b => b.ToString("X2")))}");
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    report.AppendLine($"[{format}] cuerpo: {Trim(body)}");
                }
            }
            catch (Exception exception)
            {
                report.AppendLine($"[{format}] EXCEPCIÓN: {exception.GetType().Name} · {exception.Message}");
            }
        }
    }

    private static string Trim(string value) =>
        value.Length <= 400 ? value : value[..400] + "…";
}
