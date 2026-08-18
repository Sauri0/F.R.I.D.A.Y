using System.Buffers.Binary;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Viernes.Platform.Windows.Speech.Recognition;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El detector entrenado contra audio de verdad, cuando el modelo está instalado.
/// </summary>
/// <remarks>
/// Ésta es la prueba que contesta la pregunta del usuario —«si aplaudo o se cae algo no debería
/// interrumpirse»— sin tener que tirar nada al piso: se le da voz sintetizada por Windows, un
/// retumbe grave, un ruido de banda ancha y silencio, y se mira qué contesta el modelo.
/// <para>
/// Si el modelo no está instalado, no falla: no hay nada que probar y no se puede descargar solo. Es
/// la misma regla que rige para los modelos de Whisper. Para correrla apuntando a otra copia se usa
/// <c>VIERNES_VAD_MODEL</c>.
/// </para>
/// <para>
/// Vale decir de dónde sale la voz de la prueba: la sintetiza Windows, no es una grabación. Alcanza
/// para lo que se está midiendo —que el modelo separe habla de golpes— pero no reemplaza escuchar
/// cómo se porta con la voz del usuario en su cuarto. Para eso está la comparación contra la
/// heurística corriendo en paralelo.
/// </para>
/// </remarks>
public sealed class SileroModelTests
{
    private const int SampleRate = 16_000;
    private const int Window = 512;

    private static IVadModelRunner? Runner()
    {
        var configured = Environment.GetEnvironmentVariable("VIERNES_VAD_MODEL");
        var path = string.IsNullOrWhiteSpace(configured)
            ? OnnxVadModelRunner.GetDefaultModelPath()
            : configured;
        return OnnxVadModelRunner.TryCreate(path, out _);
    }

    /// <summary>Qué dijo el modelo sobre una tira de audio.</summary>
    private sealed record Medicion(int Ventanas, double Proporcion, double ProbabilidadMaxima)
    {
        public override string ToString() =>
            $"{Ventanas} ventanas · voz {Proporcion:P0} · pico {ProbabilidadMaxima:0.000}";
    }

    private static Medicion Medir(IVadModelRunner runner, short[] samples)
    {
        // Sin «using»: el detector es dueño del runner y desecharlo dejaría sin modelo a la medición
        // siguiente. Acá el dueño del runner es la prueba.
        var detector = new SileroVoiceActivityDetector(runner);
        var windows = 0;
        var voiced = 0;
        double peak = 0;
        for (var offset = 0; offset + Window <= samples.Length; offset += Window)
        {
            windows++;
            if (detector.Analyze(samples.AsSpan(offset, Window), insideUtterance: voiced > 0).IsVoice)
            {
                voiced++;
            }

            peak = Math.Max(peak, detector.LastProbability);
        }

        return new Medicion(windows, windows == 0 ? 0 : (double)voiced / windows, peak);
    }

    private static short[]? VozSintetizada()
    {
        using var synthesizer = new SpeechSynthesizer();
        if (!synthesizer.GetInstalledVoices().Any(voice => voice.Enabled))
        {
            return null;
        }

        using var pcm = new MemoryStream();
        synthesizer.SetOutputToAudioStream(
            pcm,
            new SpeechAudioFormatInfo(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Speak("Viernes, creame una carpeta en el escritorio y abrila, por favor.");
        synthesizer.SetOutputToNull();

        var bytes = pcm.ToArray();
        var samples = new short[bytes.Length / sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)));
        }

        return samples;
    }

    private static short[] Tono(double frequency, double amplitude, int seconds)
    {
        var samples = new short[SampleRate * seconds];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)(Math.Sin(2 * Math.PI * frequency * index / SampleRate)
                * amplitude * short.MaxValue);
        }

        return samples;
    }

    private static short[] RuidoDeBandaAncha(double amplitude, int seconds)
    {
        var random = new Random(11);
        var samples = new short[SampleRate * seconds];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (short)(((random.NextDouble() * 2) - 1) * amplitude * short.MaxValue);
        }

        return samples;
    }

    [Fact]
    public void ConElModeloInstalado_SeparaLaVozDeLosGolpes()
    {
        using var runner = Runner();
        if (runner is null)
        {
            // Sin modelo no hay nada que medir. No es una falla: el modelo lo instala el usuario.
            return;
        }

        var voz = VozSintetizada();
        if (voz is null)
        {
            return;
        }

        var conVoz = Medir(runner, voz);
        runner.Reset();
        var conGolpe = Medir(runner, Tono(60, 0.5, 2));
        runner.Reset();
        var conAplauso = Medir(runner, RuidoDeBandaAncha(0.5, 2));
        runner.Reset();
        var conSilencio = Medir(runner, new short[SampleRate * 2]);

        // La voz tiene pausas entre palabras, así que no se le pide el 100 %; sí que sea la mayoría.
        // MEDIDO el 18/08/2026 con silero_vad.onnx de la rama principal, sobre una frase de 5,25 s
        // sintetizada por Windows (nivel medio 0,097):
        //
        //     voz ....... 164 ventanas · 73 % dadas por voz · pico 1,000
        //     golpe ..... 62 ventanas ·  0 % · pico 0,012
        //     aplauso ... 62 ventanas ·  0 % · pico 0,028
        //     silencio .. 62 ventanas ·  0 % · pico 0,009
        //
        // El 73 % y no más porque la frase tiene pausas entre palabras y el modelo las marca como
        // silencio, que es lo correcto.
        Assert.True(conVoz.Proporcion > 0.5, $"voz: {conVoz}");

        // Y lo que el usuario pidió expresamente: que un golpe o un aplauso no la interrumpan.
        Assert.True(conGolpe.Proporcion < 0.05, $"golpe: {conGolpe}");
        Assert.True(conAplauso.Proporcion < 0.05, $"aplauso: {conAplauso}");
        Assert.True(conSilencio.Proporcion < 0.05, $"silencio: {conSilencio}");
    }

    [Fact]
    public void LaComparacionEntreLosDosDetectoresSeLlenaConAudioDeVerdad()
    {
        // El usuario pidió poder comparar antes de confiar en el detector nuevo. Esto comprueba que
        // el marcador efectivamente se llena cuando los dos escuchan lo mismo; los números del cuarto
        // real salen de dejarlo corriendo, no de acá.
        var runner = Runner();
        if (runner is null)
        {
            return;
        }

        var voz = VozSintetizada();
        if (voz is null)
        {
            runner.Dispose();
            return;
        }

        using var scoreboard = new VoiceActivityScoreboard(
            new SileroVoiceActivityDetector(runner),
            new HeuristicVoiceActivityDetector());
        for (var offset = 0; offset + Window <= voz.Length; offset += Window)
        {
            scoreboard.Analyze(voz.AsSpan(offset, Window), insideUtterance: false);
        }

        var agreement = scoreboard.Agreement;
        Assert.True(agreement.Frames > 100, agreement.ToString());
        Assert.True(agreement.BothVoice > 0, agreement.ToString());
    }
}
