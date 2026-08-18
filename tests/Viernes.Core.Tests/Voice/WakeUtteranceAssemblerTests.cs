using Viernes.Platform.Windows.Speech.Recognition;
using Viernes.Platform.Windows.Speech.WakeWord;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>Detector guionado: se le dice bloque por bloque si hay voz o no.</summary>
internal sealed class GuionVoiceActivityDetector : IVoiceActivityDetector
{
    private bool _isVoice = true;

    public VoiceActivityDetectorInfo Info { get; } = new("Guionado", false, "Dice lo que se le indique.");

    /// <summary>Lo que va a contestar de acá en adelante.</summary>
    public void Say(bool isVoice) => _isVoice = isVoice;

    public VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance) =>
        new(_isVoice, _isVoice ? 1 : 0, 0.5);

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Lo que hace posible nombrarla en el medio de la frase y que igual entienda todo.
/// </summary>
/// <remarks>
/// La comprobación central usa un truco: el audio que se le da es una rampa de bytes que crece de a
/// uno sin parar. Si el resultado sigue siendo una rampa continua, entonces en la juntura entre lo
/// que estaba guardado y lo que se siguió grabando <em>no se perdió ni se repitió un solo byte</em>.
/// Con audio de verdad esto no se puede ver: Whisper transcribe lo que le den y una sílaba repetida
/// en el medio pasa desapercibida hasta que alguien nota que la asistente entiende cualquier cosa.
/// </remarks>
public sealed class WakeUtteranceAssemblerTests
{
    private const int SampleRate = 16_000;

    /// <summary>960 bytes: 480 muestras de 16 bits, los 30 ms que entrega la captura real.</summary>
    private const int BytesPerFrame = 960;

    private static ContinuousWakeListenerOptions Options() => new()
    {
        PreRoll = TimeSpan.FromSeconds(10),
        Endpointer = new UtteranceEndpointerOptions
        {
            InitialSilenceTimeout = TimeSpan.FromSeconds(2),
            EndSilenceTimeout = TimeSpan.FromMilliseconds(850),
            MaximumDuration = TimeSpan.FromSeconds(20),
            RequiredVoiceEnergy = TimeSpan.FromMilliseconds(150)
        }
    };

    /// <summary>Bloques de una rampa continua, para poder ver la juntura.</summary>
    private sealed class Rampa
    {
        private byte _next;

        public byte[] Frame()
        {
            var frame = new byte[BytesPerFrame];
            for (var index = 0; index < frame.Length; index++)
            {
                frame[index] = _next++;
            }

            return frame;
        }
    }

    private static AssembledUtterance? Feed(WakeUtteranceAssembler assembler, Rampa rampa, int frames)
    {
        AssembledUtterance? result = null;
        for (var index = 0; index < frames; index++)
        {
            result ??= assembler.Write(rampa.Frame());
        }

        return result;
    }

    private static byte[] DataOf(Stream wave)
    {
        var memory = new MemoryStream();
        wave.CopyTo(memory);
        return memory.ToArray()[WaveAudio.HeaderBytes..];
    }

    [Fact]
    public void ElNombreEnElMedio_LaFraseLlegaEnteraYSinCosturas()
    {
        // «che, necesito que Viernes me abra Spotify»: tres segundos hablando, el nombre, y sigue.
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 100);
        Assert.True(assembler.NameHeard("Viernes", 0.69f));
        Feed(assembler, rampa, frames: 34);

        detector.Say(false);
        var utterance = Feed(assembler, rampa, frames: 40);

        Assert.NotNull(utterance);
        var data = DataOf(utterance.Wave);

        // Ni un byte perdido ni repetido en la juntura entre la ventana rodante y la cola.
        for (var index = 1; index < data.Length; index++)
        {
            Assert.Equal((byte)(data[index - 1] + 1), data[index]);
        }

        // Y lo anterior al nombre está adentro: los tres segundos que ya venía diciendo.
        Assert.True(utterance.PreRollDuration >= TimeSpan.FromMilliseconds(2900));
        Assert.True(utterance.Duration > TimeSpan.FromSeconds(4));
        Assert.Equal(UtteranceStopReason.EndSilence, utterance.StopReason);
    }

    [Fact]
    public void SiSoloDiceElNombre_IgualMandaAlgoDeContexto()
    {
        // Medio segundo con «Viernes» y nada más no le da al modelo con qué decidir.
        var detector = new GuionVoiceActivityDetector();
        detector.Say(false);
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 100);
        detector.Say(true);
        Feed(assembler, rampa, frames: 5);
        Assert.True(assembler.NameHeard("Viernes", 0.7f));

        detector.Say(false);
        var utterance = Feed(assembler, rampa, frames: 100);

        Assert.NotNull(utterance);
        Assert.True(utterance.PreRollDuration >= TimeSpan.FromMilliseconds(1500));

        // Y si después del nombre no dice nada, cierra sola por silencio inicial en vez de quedarse
        // grabando el cuarto.
        Assert.Equal(UtteranceStopReason.InitialSilence, utterance.StopReason);
    }

    [Fact]
    public void ConUnSilencioLargoAntes_NoArrastraLaConversacionDeRecien()
    {
        // Con la tele puesta o después de una charla ajena, mandar los diez segundos enteros es
        // mandarle al modelo diez segundos de otra cosa adelante del pedido.
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 200);
        detector.Say(false);
        Feed(assembler, rampa, frames: 100);
        detector.Say(true);
        Feed(assembler, rampa, frames: 20);

        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        Feed(assembler, rampa, frames: 34);
        detector.Say(false);
        var utterance = Feed(assembler, rampa, frames: 40);

        Assert.NotNull(utterance);

        // Lo poco que venía diciendo, no los seis segundos de antes del silencio.
        Assert.True(utterance.PreRollDuration < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void RepetirElNombreEnMedioDeLaFrase_NoReiniciaLaCaptura()
    {
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 30);
        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        Feed(assembler, rampa, frames: 10);

        Assert.False(assembler.NameHeard("Viernes", 0.7f));
    }

    [Fact]
    public void SinNombre_NoJuntaNadaAunqueSeHableTodoElDia()
    {
        // El micrófono está abierto todo el tiempo; lo que no puede pasar es que grabe todo el
        // tiempo. Sin nombre no hay captura y la ventana rodante se pisa sola.
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        var utterance = Feed(assembler, rampa, frames: 2000);

        Assert.Null(utterance);
        Assert.False(assembler.IsCapturing);
    }

    [Fact]
    public void SiNuncaSeCalla_CierraPorElTopeYEntregaIgual()
    {
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        var utterance = Feed(assembler, rampa, frames: 1000);

        Assert.NotNull(utterance);
        Assert.Equal(UtteranceStopReason.MaximumDuration, utterance.StopReason);
    }

    [Fact]
    public void DespuesDeEntregarUnaFrase_QuedaListoParaLaSiguiente()
    {
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 30);
        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        Feed(assembler, rampa, frames: 34);
        detector.Say(false);
        Assert.NotNull(Feed(assembler, rampa, frames: 40));
        Assert.False(assembler.IsCapturing);

        detector.Say(true);
        Feed(assembler, rampa, frames: 30);
        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        Feed(assembler, rampa, frames: 34);
        detector.Say(false);

        Assert.NotNull(Feed(assembler, rampa, frames: 40));
    }

    [Fact]
    public void ElWavEntregadoTieneElLargoQueDiceTener()
    {
        var detector = new GuionVoiceActivityDetector();
        var assembler = new WakeUtteranceAssembler(Options(), detector);
        var rampa = new Rampa();

        Feed(assembler, rampa, frames: 40);
        Assert.True(assembler.NameHeard("Viernes", 0.7f));
        Feed(assembler, rampa, frames: 34);
        detector.Say(false);
        var utterance = Feed(assembler, rampa, frames: 40);

        Assert.NotNull(utterance);
        var data = DataOf(utterance.Wave);
        var declarada = WaveAudio.Duration(data.Length, SampleRate);
        Assert.True((declarada - utterance.Duration).Duration() < TimeSpan.FromMilliseconds(1));
    }
}
