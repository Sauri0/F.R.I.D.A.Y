using Viernes.Platform.Windows.Speech.Recognition;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>Detector de mentira: devuelve lo que se le diga, para probar lo que va encima.</summary>
internal sealed class FakeVoiceActivityDetector(params bool[] answers) : IVoiceActivityDetector
{
    private int _index;

    public VoiceActivityDetectorInfo Info { get; } = new("De prueba", false, "Devuelve lo que se le pidió.");

    public VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance)
    {
        var answer = answers.Length == 0 || _index >= answers.Length ? false : answers[_index++];
        return new VoiceActivityDecision(answer, answer ? 1 : 0, 0.5);
    }

    public void Reset() => _index = 0;

    public void Dispose()
    {
    }
}

/// <summary>Modelo de mentira: entrega la secuencia de probabilidades que se le cargó.</summary>
internal sealed class FakeVadModelRunner(params float[] probabilities) : IVadModelRunner
{
    private int _index;

    public int WindowSamples => 512;

    public int SampleRate => 16_000;

    public int Calls { get; private set; }

    public float Probability(ReadOnlySpan<float> window)
    {
        Calls++;
        return _index < probabilities.Length ? probabilities[_index++] : 0f;
    }

    public void Reset() => _index = 0;

    public void Dispose()
    {
    }
}

/// <summary>
/// Qué se considera voz y qué no, sobre señales armadas a propósito.
/// </summary>
/// <remarks>
/// El pedido textual del usuario era «solo debería entender voz no sonidos, si aplaudo o se cae algo
/// no debería interrumpirse». Estas pruebas son la forma de comprobarlo sin tirar cosas al piso: se
/// arman las tres señales con las que se confundía —voz, golpe grave que resuena, ruido de banda
/// ancha— y se le pregunta al detector.
/// <para>
/// Van todas en una sola clase a propósito. El piso de ruido es estático y compartido entre
/// capturas —así el cuarto no se reaprende en cada turno—, y xunit corre clases distintas en
/// paralelo: repartirlas en varias clases haría que una prueba le moviera el piso a otra.
/// </para>
/// </remarks>
public sealed class VoiceActivityDetectorTests
{
    private const int SampleRate = 16_000;
    private const int FrameSamples = 480;

    /// <summary>Voz: fundamental grave con sus armónicos, como una vocal sostenida.</summary>
    private static short[] VozArmonica(double amplitude = 0.3, double fundamental = 150)
    {
        var frame = new short[FrameSamples];
        for (var index = 0; index < frame.Length; index++)
        {
            double value = 0;
            for (var harmonic = 1; harmonic <= 8; harmonic++)
            {
                value += Math.Sin(2 * Math.PI * fundamental * harmonic * index / SampleRate) / harmonic;
            }

            frame[index] = (short)(value / 2 * amplitude * short.MaxValue);
        }

        return frame;
    }

    /// <summary>Algo pesado que se cae y resuena: un tono grave y fuerte, sin armónicos altos.</summary>
    private static short[] GolpeGrave(double amplitude = 0.5, double frequency = 60)
    {
        var frame = new short[FrameSamples];
        for (var index = 0; index < frame.Length; index++)
        {
            frame[index] = (short)(Math.Sin(2 * Math.PI * frequency * index / SampleRate) * amplitude * short.MaxValue);
        }

        return frame;
    }

    /// <summary>Aplauso, teclas, siseo: energía repartida por todo el espectro.</summary>
    private static short[] RuidoDeBandaAncha(double amplitude = 0.5, int seed = 7)
    {
        var random = new Random(seed);
        var frame = new short[FrameSamples];
        for (var index = 0; index < frame.Length; index++)
        {
            frame[index] = (short)(((random.NextDouble() * 2) - 1) * amplitude * short.MaxValue);
        }

        return frame;
    }

    private static short[] Silencio() => new short[FrameSamples];

    /// <summary>
    /// Deja el piso de ruido en el suelo antes de medir.
    /// </summary>
    /// <remarks>
    /// El piso baja un 10 % por bloque, así que doscientos bloques de silencio lo dejan en
    /// prácticamente cero venga de donde venga. Es lo que hace que cada prueba arranque igual aunque
    /// el piso sea compartido.
    /// </remarks>
    private static HeuristicVoiceActivityDetector Asentado()
    {
        var detector = new HeuristicVoiceActivityDetector();
        var silence = Silencio();
        for (var index = 0; index < 200; index++)
        {
            detector.Analyze(silence, insideUtterance: false);
        }

        return detector;
    }

    [Fact]
    public void Heuristica_UnaVozArmonica_EsVoz()
    {
        var detector = Asentado();

        Assert.True(detector.Analyze(VozArmonica(), insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Heuristica_UnaVozGraveYBaja_TambienEsVoz()
    {
        // El corte de inclinación espectral es lo nuevo, y lo peligroso de un corte nuevo es que
        // deje afuera a la persona. Una voz grave hablando bajo es el caso límite.
        var detector = Asentado();

        Assert.True(detector.Analyze(VozArmonica(amplitude: 0.05, fundamental: 95), insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Heuristica_UnGolpeGraveQueResuena_NoEsVoz()
    {
        // Es el caso que el usuario reportó y que la tasa de cruces por cero no puede resolver: a
        // 60 Hz cruza el cero 3,6 veces por bloque, o sea una tasa de 0,0075, adentro de la banda de
        // voz. Lo que lo separa es la inclinación espectral: 0,00055 contra el mínimo de 0,003.
        var detector = Asentado();

        Assert.False(detector.Analyze(GolpeGrave(), insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Heuristica_UnAplauso_NoEsVoz()
    {
        var detector = Asentado();

        Assert.False(detector.Analyze(RuidoDeBandaAncha(), insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Heuristica_ElSilencio_NoEsVoz()
    {
        var detector = Asentado();

        Assert.False(detector.Analyze(Silencio(), insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Heuristica_ElNivelParaLaInterfazSeMueveAunqueNoLlegueAVoz()
    {
        // La forma en pantalla tiene que reaccionar antes de que algo califique como voz: si no, no
        // hay manera de ver que el micrófono está entrando.
        var detector = Asentado();

        var flojo = detector.Analyze(VozArmonica(amplitude: 0.002), insideUtterance: false);

        Assert.True(flojo.Level > 0);
    }

    [Fact]
    public void Heuristica_MientrasHabla_ElPisoDeRuidoNoSeTrepaEncimaDeLaVoz()
    {
        // Éste es el error que cortaba a la gente a mitad de frase: el piso subía mientras hablabas,
        // el umbral pasaba el nivel de tu voz y desde ahí tu propia voz contaba como fondo. Medido,
        // el umbral superaba la voz a los 4,5 segundos de hablar seguido.
        var detector = Asentado();
        var voz = VozArmonica();

        // Diez segundos de voz continua a 30 ms por bloque.
        for (var index = 0; index < 333; index++)
        {
            Assert.True(detector.Analyze(voz, insideUtterance: true).IsVoice);
        }
    }

    [Fact]
    public void Silero_ConLaProbabilidadAlta_EsVoz()
    {
        using var detector = new SileroVoiceActivityDetector(new FakeVadModelRunner(0.9f));

        Assert.True(detector.Analyze(new short[512], insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Silero_LaHisteresisEvitaQueParpadeeAlrededorDelUmbral()
    {
        // Con un solo umbral, una probabilidad que oscila alrededor de él prende y apaga la voz
        // varias veces por segundo y el fin de frase se dispara en medio de una palabra.
        using var detector = new SileroVoiceActivityDetector(new FakeVadModelRunner(0.6f, 0.4f, 0.2f, 0.4f));
        var window = new short[512];

        Assert.True(detector.Analyze(window, insideUtterance: false).IsVoice);
        Assert.True(detector.Analyze(window, insideUtterance: true).IsVoice);
        Assert.False(detector.Analyze(window, insideUtterance: true).IsVoice);
        Assert.False(detector.Analyze(window, insideUtterance: false).IsVoice);
    }

    [Fact]
    public void Silero_ConBloquesQueNoLlenanLaVentana_NoConsultaAlModeloYSostieneElVeredicto()
    {
        // Los bloques de la captura son de 30 ms (480 muestras) y la ventana del modelo de 512: no
        // coinciden nunca. Consultar con media ventana daría basura.
        var runner = new FakeVadModelRunner(0.9f);
        using var detector = new SileroVoiceActivityDetector(runner);

        var primero = detector.Analyze(new short[480], insideUtterance: false);

        Assert.Equal(0, runner.Calls);
        Assert.False(primero.IsVoice);

        var segundo = detector.Analyze(new short[480], insideUtterance: false);

        Assert.Equal(1, runner.Calls);
        Assert.True(segundo.IsVoice);
    }

    [Fact]
    public void Comparacion_CuentaDondeSeSeparanLosDosDetectores()
    {
        // Sin esto, cambiar de detector es una apuesta: acá ya pasó que una constante se movió por
        // una sola medición y hubo que volver atrás.
        using var scoreboard = new VoiceActivityScoreboard(
            new FakeVoiceActivityDetector(true, true, false, false),
            new FakeVoiceActivityDetector(true, false, true, false));
        var frame = new short[480];

        for (var index = 0; index < 4; index++)
        {
            scoreboard.Analyze(frame, insideUtterance: false);
        }

        var agreement = scoreboard.Agreement;
        Assert.Equal(4, agreement.Frames);
        Assert.Equal(1, agreement.BothVoice);
        Assert.Equal(1, agreement.BothSilence);
        Assert.Equal(1, agreement.OnlyPrimary);
        Assert.Equal(1, agreement.OnlyBackup);
        Assert.Equal(0.5, agreement.Agreement);
    }

    [Fact]
    public void Comparacion_ElQueMandaEsElPrimeroYElOtroSoloSeAnota()
    {
        using var scoreboard = new VoiceActivityScoreboard(
            new FakeVoiceActivityDetector(true),
            new FakeVoiceActivityDetector(false));

        Assert.True(scoreboard.Analyze(new short[480], insideUtterance: false).IsVoice);
    }

    [Fact]
    public void ModeloEntrenado_SiFaltaElArchivo_NoRompeYExplicaPorQue()
    {
        // Que falte el modelo no puede dejar sorda a la asistente: se informa y sigue la heurística.
        var runner = OnnxVadModelRunner.TryCreate(
            Path.Combine(Path.GetTempPath(), "viernes-modelo-que-no-existe.onnx"),
            out var reason);

        Assert.Null(runner);
        Assert.NotNull(reason);
        Assert.Contains("Falta el modelo", reason, StringComparison.OrdinalIgnoreCase);
    }
}
