using Viernes.Platform.Windows.Speech.Recognition;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// La decisión sobre una secuencia de bloques: cuándo empezó a hablar y cuándo terminó.
/// </summary>
/// <remarks>
/// Esta lógica vivía adentro de la sesión de captura y por eso no tenía pruebas: para ejercitarla
/// hacía falta un micrófono. Ahí adentro se cometieron —y se corrigieron— dos errores que esta clase
/// de pruebas habría encontrado en segundos: exigir energía <em>seguida</em> (un «dale» no llegaba
/// nunca) y reiniciar la cuenta en cada bache. Las dos están cubiertas acá abajo.
/// </remarks>
public sealed class UtteranceEndpointerTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(30);

    private static UtteranceEndpointer Endpointer() => new(new UtteranceEndpointerOptions
    {
        InitialSilenceTimeout = TimeSpan.FromSeconds(2),
        EndSilenceTimeout = TimeSpan.FromMilliseconds(850),
        MaximumDuration = TimeSpan.FromSeconds(6),
        RequiredVoiceEnergy = TimeSpan.FromMilliseconds(150)
    });

    private static UtteranceStopReason Feed(UtteranceEndpointer endpointer, bool isVoice, int frames)
    {
        var reason = UtteranceStopReason.None;
        for (var index = 0; index < frames; index++)
        {
            reason = endpointer.Observe(isVoice, Frame);
        }

        return reason;
    }

    [Fact]
    public void Observe_UnPortazo_NoAlcanzaParaDarLaVozPorEmpezada()
    {
        // Dos bloques y se apaga: eso es un golpe, no una palabra.
        var endpointer = Endpointer();

        Feed(endpointer, isVoice: true, frames: 2);
        Feed(endpointer, isVoice: false, frames: 5);

        Assert.False(endpointer.VoiceStarted);
    }

    [Fact]
    public void Observe_UnaPalabraCorta_SiEmpieza()
    {
        // «sí», «dale», «listo»: unos 200 ms de núcleo vocálico. Con la regla vieja —240 ms
        // seguidos— no llegaban nunca y la captura devolvía vacío sin transcribir.
        var endpointer = Endpointer();

        Feed(endpointer, isVoice: true, frames: 5);

        Assert.True(endpointer.VoiceStarted);
    }

    [Fact]
    public void Observe_AlEmpezar_MarcaElComienzoDondeArrancoLaEnergiaYNoDondeSeConfirmo()
    {
        // Si marcara donde se confirma, la frase que se manda a transcribir empieza 150 ms tarde y
        // se come la primera consonante.
        var endpointer = Endpointer();

        Feed(endpointer, isVoice: true, frames: 5);

        Assert.Equal(TimeSpan.Zero, endpointer.VoiceStartedAt);
    }

    [Fact]
    public void Observe_ConUnBacheCorto_LaEnergiaDecaePeroNoSeReinicia()
    {
        // Adentro de una palabra hay micro-silencios. Reiniciar en cada uno era el error viejo:
        // 120 ms de voz, un bloque de bache y 60 ms más suman 150 y arrancan; con reinicio, esos
        // últimos 60 ms empezaban de cero y la palabra corta no llegaba nunca.
        var endpointer = Endpointer();

        Feed(endpointer, isVoice: true, frames: 4);
        Feed(endpointer, isVoice: false, frames: 1);
        Feed(endpointer, isVoice: true, frames: 2);

        Assert.True(endpointer.VoiceStarted);
    }

    [Fact]
    public void Observe_SinVozDentroDelPlazo_CierraPorSilencioInicial()
    {
        var endpointer = Endpointer();

        var reason = Feed(endpointer, isVoice: false, frames: 100);

        Assert.Equal(UtteranceStopReason.InitialSilence, reason);
    }

    [Fact]
    public void Observe_CuandoSeCalla_CierraPorFinDeFrase()
    {
        var endpointer = Endpointer();
        Feed(endpointer, isVoice: true, frames: 10);

        var reason = Feed(endpointer, isVoice: false, frames: 30);

        Assert.Equal(UtteranceStopReason.EndSilence, reason);
        Assert.True(endpointer.TrailingSilence >= TimeSpan.FromMilliseconds(850));
    }

    [Fact]
    public void Observe_UnSilencioCortoNoCierraLaFrase()
    {
        var endpointer = Endpointer();
        Feed(endpointer, isVoice: true, frames: 10);

        var reason = Feed(endpointer, isVoice: false, frames: 10);

        Assert.Equal(UtteranceStopReason.None, reason);
    }

    [Fact]
    public void Observe_SiNuncaSeCalla_CierraPorElTope()
    {
        var endpointer = Endpointer();

        var reason = Feed(endpointer, isVoice: true, frames: 300);

        Assert.Equal(UtteranceStopReason.MaximumDuration, reason);
    }

    [Fact]
    public void Observe_UnaVezCerrada_NoCambiaDeOpinion()
    {
        var endpointer = Endpointer();
        Feed(endpointer, isVoice: true, frames: 10);
        Feed(endpointer, isVoice: false, frames: 30);

        var reason = Feed(endpointer, isVoice: true, frames: 50);

        Assert.Equal(UtteranceStopReason.EndSilence, reason);
    }

    [Fact]
    public void Reset_DejaTodoComoAlPrincipio()
    {
        var endpointer = Endpointer();
        Feed(endpointer, isVoice: true, frames: 10);
        Feed(endpointer, isVoice: false, frames: 30);

        endpointer.Reset();

        Assert.False(endpointer.VoiceStarted);
        Assert.Equal(UtteranceStopReason.None, endpointer.StopReason);
        Assert.Equal(TimeSpan.Zero, endpointer.Elapsed);
    }
}
