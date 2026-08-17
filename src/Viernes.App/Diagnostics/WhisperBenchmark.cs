using System.IO;
using System.Speech.Synthesis;
using System.Text;
using Viernes.Platform.Windows.Speech.Recognition;
using Whisper.net;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Mide Whisper con audio sintético y conocido. No depende de que alguien hable, así que separa
/// limpiamente lo que tarda en cargar el modelo de lo que tarda en transcribir.
/// </summary>
/// <remarks>
/// Es la misma técnica con la que se validó la voz al principio del proyecto: SAPI sintetiza una
/// frase a WAV, Whisper la transcribe, y el WAV se borra. Nada sale de la máquina.
/// </remarks>
internal static class WhisperBenchmark
{
    private const string Phrase = "Viernes, recordame llamar a Sofía mañana a las nueve y media.";

    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        var wavPath = Path.Combine(Path.GetTempPath(), $"viernes-bench-{Guid.NewGuid():N}.wav");

        try
        {
            report.AppendLine("== BANCO DE PRUEBA DE WHISPER ==");
            report.AppendLine($"Frase: «{Phrase}»");
            report.AppendLine();

            var synthesis = System.Diagnostics.Stopwatch.StartNew();
            SynthesizeToWav(wavPath);
            synthesis.Stop();
            var audioSeconds = EstimateSeconds(wavPath);
            report.AppendLine($"WAV generado : {new FileInfo(wavPath).Length / 1024} KB · ~{audioSeconds:0.0} s de audio");

            foreach (var model in EnumerateInstalledModels())
            {
                report.AppendLine();
                report.AppendLine($"-- {Path.GetFileName(model)} ({new FileInfo(model).Length / 1024 / 1024} MB)");

                var load = System.Diagnostics.Stopwatch.StartNew();
                using var factory = WhisperFactory.FromPath(model);
                await using var processor = factory.CreateBuilder().WithLanguage("es").Build();
                load.Stop();
                report.AppendLine($"   carga        : {load.ElapsedMilliseconds} ms");

                for (var pass = 1; pass <= 2; pass++)
                {
                    var text = new StringBuilder();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    await using (var audio = File.OpenRead(wavPath))
                    {
                        await foreach (var segment in processor.ProcessAsync(audio))
                        {
                            text.Append(segment.Text);
                        }
                    }

                    clock.Stop();

                    // El factor contra tiempo real es lo que decide si sirve: debajo de 1 va bien.
                    var factor = clock.ElapsedMilliseconds / 1000.0 / Math.Max(0.1, audioSeconds);
                    report.AppendLine(
                        $"   pasada {pass}     : {clock.ElapsedMilliseconds} ms  (×{factor:0.00} tiempo real)");
                    if (pass == 2)
                    {
                        report.AppendLine($"   transcripción: «{text.ToString().Trim()}»");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"FALLÓ: {exception.GetType().Name} · {exception.Message}");
        }
        finally
        {
            TryDelete(wavPath);
        }

        return report.ToString();
    }

    private static void SynthesizeToWav(string path)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = synthesizer.GetInstalledVoices()
            .Where(candidate => candidate.Enabled)
            .FirstOrDefault(candidate =>
                candidate.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase));
        if (voice is not null)
        {
            synthesizer.SelectVoice(voice.VoiceInfo.Name);
        }

        // Whisper sólo acepta 16 kHz mono; SAPI por defecto escribe otra cosa.
        synthesizer.SetOutputToWaveFile(
            path,
            new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                16_000,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                System.Speech.AudioFormat.AudioChannel.Mono));
        synthesizer.Speak(Phrase);
        synthesizer.SetOutputToNull();
    }

    private static IEnumerable<string> EnumerateInstalledModels()
    {
        var directory = WhisperSpeechRecognitionOptions.GetDefaultModelDirectory();
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.bin").OrderBy(File.GetLastWriteTimeUtc))
        {
            yield return path;
        }
    }

    /// <summary>Estimación desde el tamaño del WAV; alcanza para calcular el factor de tiempo real.</summary>
    private static double EstimateSeconds(string path)
    {
        const int bytesPerSecond = 16_000 * 2;
        return Math.Max(0.1, (new FileInfo(path).Length - 44) / (double)bytesPerSecond);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Un temporal que queda no justifica romper el diagnóstico.
        }
    }
}
