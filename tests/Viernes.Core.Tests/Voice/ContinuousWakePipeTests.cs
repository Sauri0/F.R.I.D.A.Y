using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using Viernes.Platform.Windows.Speech.WakeWord;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El único eslabón del oído continuo que nunca había corrido: SAPI leyendo del caño.
/// </summary>
/// <remarks>
/// Todo lo demás del oído tenía pruebas —la ventana rodante, el recorte de la juntura, el fin de
/// frase, el detector de voz, el caño como anillo de bytes— y aun así <b>el oído no detectaba
/// absolutamente nada</b>, porque lo que nadie había probado era el encuentro: qué le pide SAPI a un
/// <see cref="System.IO.Stream"/> cuando se lo pasan por <c>SetInputToAudioStream</c>, y qué hace si
/// no le contesta como espera.
/// <para>
/// Medido con un espía entre los dos, y son dos cosas, las dos silenciosas:
/// </para>
/// <para>
/// 1. Lo primero que hace, antes de leer un byte, es <c>Seek(0, Current)</c> —el modismo de siempre
/// para preguntar la posición—. Si eso lanza, SAPI abandona la entrada sin leer nunca, sin reconocer
/// nunca y sin fallar nunca.
/// </para>
/// <para>
/// 2. Después lee, y si le devuelven <em>menos</em> de lo pedido hace una sola lectura y no vuelve a
/// pedir. Un byte de menos lo lee igual que un cero, y un cero es fin de audio.
/// </para>
/// <para>
/// Por eso esta prueba mira el conjunto y no las piezas: es lo único que puede volver a atrapar esto
/// si alguien «limpia» el caño devolviendo lecturas parciales, que es lo que hace cualquier
/// implementación razonable de un <c>Stream</c>.
/// </para>
/// <para>
/// Necesita un reconocedor de español y una voz instalados en Windows. Si no están, no hay nada que
/// probar y no falla: la misma regla que rige para los modelos de Whisper y de Silero.
/// </para>
/// </remarks>
public sealed class ContinuousWakePipeTests
{
    private const int SampleRate = 16_000;
    private const int BytesPerSecond = SampleRate * 2;

    private static readonly SpeechAudioFormatInfo Formato =
        new(SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    private static RecognizerInfo? Reconocedor() =>
        SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(candidate =>
            candidate.Culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// El PCM crudo de una frase dicha por Windows, en el formato que espera el oído.
    /// </summary>
    /// <remarks>
    /// <c>SetOutputToAudioStream</c> y no <c>SetOutputToWaveStream</c>: aquél escribe PCM pelado en el
    /// formato que se le pide y éste escribe un WAV en <em>su</em> formato, que en este equipo es de
    /// 22 050 Hz. Pasarle a SAPI audio de 22 kHz diciéndole que es de 16 no falla —lo reconstruye
    /// igual, más grave y más lento—, así que la prueba pasaría o no por razones que no tienen nada
    /// que ver con lo que está midiendo.
    /// </remarks>
    private static byte[]? Decir(string frase)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            using var audio = new MemoryStream();
            synthesizer.SetOutputToAudioStream(audio, Formato);
            synthesizer.Speak(frase);
            synthesizer.SetOutputToNull();
            var bytes = audio.ToArray();
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception)
        {
            // Sin voz instalada no hay con qué probar. No es una falla del oído.
            return null;
        }
    }

    private static SpeechRecognitionEngine Motor(RecognizerInfo info)
    {
        var engine = new SpeechRecognitionEngine(info.Id)
        {
            InitialSilenceTimeout = TimeSpan.FromHours(1),
            BabbleTimeout = TimeSpan.FromHours(1)
        };

        var builder = new GrammarBuilder { Culture = info.Culture };
        builder.Append(new Choices(["Viernes", "Hola Viernes", "Che Viernes"]));
        engine.LoadGrammar(new Grammar(builder) { Name = "Viernes.Prueba" });
        return engine;
    }

    [Fact]
    public async Task ElNombreSeOyeALoLargoDelCano()
    {
        var info = Reconocedor();
        if (info is null)
        {
            return;
        }

        // La voz de Windows no tiene por qué ser rioplatense: lo que se está midiendo es que el audio
        // llegue entero del otro lado del caño, no cómo pronuncia.
        var pcm = Decir("Hola Viernes, anotá que falta carbón.");
        if (pcm is null || pcm.Length < BytesPerSecond)
        {
            return;
        }

        // El caño se llena de a poco y desde otro hilo, como lo llena el micrófono. Es lo que
        // importa: con el caño ya lleno, cada lectura sale entera de casualidad y no se prueba nada.
        // Alimentándolo de a bloques, el lector siempre le gana y ahí es donde SAPI pedía 3040 bytes,
        // recibía 960 y se daba por terminado.
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(2), BytesPerSecond);
        var bloque = BytesPerSecond / 33;
        var alimentar = Task.Run(async () =>
        {
            for (var offset = 0; offset < pcm.Length; offset += bloque)
            {
                pipe.Write(pcm, offset, Math.Min(bloque, pcm.Length - offset));
                await Task.Delay(3).ConfigureAwait(false);
            }

            // Medio segundo de silencio para que SAPI cierre la frase, y recién ahí el fin de audio.
            for (var i = 0; i < 16; i++)
            {
                pipe.Write(new byte[bloque], 0, bloque);
                await Task.Delay(3).ConfigureAwait(false);
            }

            pipe.Complete();
        });

        using var engine = Motor(info);
        engine.SetInputToAudioStream(pipe, Formato);
        // Recognize bloquea a propósito: es el hilo que hace de SAPI mientras el otro alimenta.
        var result = await Task.Run(() => engine.Recognize(TimeSpan.FromSeconds(30)));
        await alimentar;

        Assert.True(pipe.Position > 0, "SAPI no leyó un solo byte del caño.");
        Assert.NotNull(result);
        Assert.Contains(
            "viernes",
            result!.Text.ToLower(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreguntarLaPosicionNoEsMoverse()
    {
        // El primer pedido de SAPI, antes de leer nada. Lanzar acá deja al oído sordo en silencio.
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(1), BytesPerSecond);
        pipe.Write(new byte[8]);
        var leido = new byte[8];
        Assert.Equal(8, pipe.Read(leido, 0, leido.Length));

        Assert.Equal(8, pipe.Seek(0, SeekOrigin.Current));
        Assert.Equal(8, pipe.Seek(8, SeekOrigin.Begin));
        Assert.Equal(8, pipe.Position);
    }

    [Fact]
    public void MoverseDeVerdadSigueSiendoImposible()
    {
        // Mentir acá sería peor que fallar: el audio saldría corrido y sonaría a ruido.
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(1), BytesPerSecond);
        pipe.Write(new byte[8]);
        Assert.Equal(8, pipe.Read(new byte[8], 0, 8));

        Assert.Throws<NotSupportedException>(() => pipe.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => pipe.Seek(-4, SeekOrigin.Current));
        Assert.Throws<NotSupportedException>(() => pipe.Seek(0, SeekOrigin.End));
        Assert.Throws<NotSupportedException>(() => pipe.Position = 0);
    }
}
