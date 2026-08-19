using Viernes.Core.Voice;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// Las tres calidades de la transcripción en vivo, que el boceto define por palabra.
/// </summary>
public sealed class DictationLineTests
{
    [Fact]
    public void SoloLaUltimaEsProvisoria()
    {
        var words = DictationLine.Build(null, ["creame", "una", "carpeta"], live: true);

        // El boceto es literal: prov = D.live && i === W.length - 1. Una sola palabra, la última.
        // Con una cola provisoria entera, media frase tiembla todo el tiempo.
        Assert.Equal(
            [DictationQuality.Confirmado, DictationQuality.Confirmado, DictationQuality.Provisorio],
            words.Select(word => word.Quality));
    }

    [Fact]
    public void CerradaLaFrase_NoQuedaNingunaProvisoria()
    {
        var words = DictationLine.Build(null, ["creame", "una", "carpeta"], live: false);

        Assert.All(words, word => Assert.Equal(DictationQuality.Confirmado, word.Quality));
    }

    [Fact]
    public void LoRecuperadoVaAdelanteYConSuPropiaCalidad()
    {
        var words = DictationLine.Build(["estaba", "pensando", "que"], ["Viernes", "anotá"], live: true);

        Assert.Equal(
            ["estaba", "pensando", "que", "Viernes", "anotá"],
            words.Select(word => word.Text));
        Assert.Equal(
            [
                DictationQuality.Recuperado,
                DictationQuality.Recuperado,
                DictationQuality.Recuperado,
                DictationQuality.Confirmado,
                DictationQuality.Provisorio
            ],
            words.Select(word => word.Quality));
    }

    [Fact]
    public void SinNadaRecuperado_ArrancaEnLoQueSeDijo()
    {
        // Es el caso normal: la persona la nombra primero y no hay búfer que recuperar.
        var words = DictationLine.Build([], ["Viernes"], live: true);

        Assert.Single(words);
        Assert.Equal(DictationQuality.Provisorio, words[0].Quality);
    }

    [Fact]
    public void UnaPalabraVaciaAlFinal_NoDejaLaAnteriorTemblandoParaSiempre()
    {
        // El reconocedor manda vacíos más seguido de lo que parece. Si el índice de la última se
        // calculara después de filtrarlos, «carpeta» quedaría provisoria y nunca llegaría una
        // palabra que la reemplace.
        var words = DictationLine.Build(null, ["creame", "una", "carpeta", "   "], live: true);

        Assert.Equal(["creame", "una", "carpeta"], words.Select(word => word.Text));
        Assert.All(words, word => Assert.Equal(DictationQuality.Confirmado, word.Quality));
    }

    [Fact]
    public void SinNada_DevuelveVacio()
    {
        Assert.Empty(DictationLine.Build(null, null, live: true));
    }

    [Fact]
    public void AplanarDaLaFraseEntera()
    {
        var words = DictationLine.Build(["che"], ["Viernes", "anotá", "esto"], live: true);

        Assert.Equal("che Viernes anotá esto", DictationLine.Flatten(words));
    }
}
