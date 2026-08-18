using Viernes.Platform.Windows.Speech.WakeWord;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// Qué cuenta como que la nombraron.
/// </summary>
/// <remarks>
/// El cambio de criterio que cubren estas pruebas: el nombre solo, en cualquier posición, alcanza.
/// Antes se exigían dos palabras porque «el viernes tengo turno» disparaba con confianza 0,69 —más
/// alta que casi todas las detecciones reales, medidas entre 0,61 y 0,72— y ningún umbral las
/// separa. Ahora el falso positivo no cuesta nada, porque al dispararse se manda la frase entera al
/// modelo en vez de saludar. La exigencia de dos palabras queda para quien la prefiera.
/// </remarks>
public sealed class WakePhrasePolicyTests
{
    [Fact]
    public void Accepts_ElNombreSolo_AlcanzaDeDefecto()
    {
        Assert.True(WakePhrasePolicy.Accepts("Viernes", requireCompoundPhrase: false));
    }

    [Fact]
    public void Accepts_ConDosPalabrasExigidas_ElNombreSoloNoAlcanza()
    {
        Assert.False(WakePhrasePolicy.Accepts("Viernes", requireCompoundPhrase: true));
        Assert.True(WakePhrasePolicy.Accepts("Hola Viernes", requireCompoundPhrase: true));
    }

    [Fact]
    public void Accepts_UnaFraseVacia_NuncaDespierta()
    {
        Assert.False(WakePhrasePolicy.Accepts("   ", requireCompoundPhrase: false));
        Assert.False(WakePhrasePolicy.Accepts(null, requireCompoundPhrase: false));
    }

    [Fact]
    public void Normalize_SacaEspaciosDeMasYCaracteresDeControl()
    {
        Assert.Equal("Hola Viernes", WakePhrasePolicy.Normalize("  Hola\t\tViernes\n "));
    }

    [Fact]
    public void MentionsName_AlPrincipioDeLaFrase()
    {
        // El caso que motivó todo: decirlo y seguir hablando sin esperar.
        Assert.True(WakePhrasePolicy.MentionsName(
            "Viernes creame una carpeta en el escritorio",
            ["Viernes"]));
    }

    [Fact]
    public void MentionsName_EnElMedioDeLaFrase()
    {
        Assert.True(WakePhrasePolicy.MentionsName(
            "che, necesito que Viernes me abra Spotify",
            ["Viernes"]));
    }

    [Fact]
    public void MentionsName_TambienEnUnFalsoPositivo()
    {
        // Y está bien que lo encuentre: el nombre está ahí. Lo que hace que no moleste no es
        // detectarlo mejor sino mandarle la frase entera al modelo, que ve que nadie le pidió nada.
        Assert.True(WakePhrasePolicy.MentionsName("el viernes tengo turno", ["Viernes"]));
    }

    [Fact]
    public void MentionsName_NoSeConfundeConPalabrasQueLoContienen()
    {
        Assert.False(WakePhrasePolicy.MentionsName("adviernes", ["Viernes"]));
        Assert.False(WakePhrasePolicy.MentionsName("viernesito", ["Viernes"]));
    }

    [Fact]
    public void MentionsName_IgnoraMayusculasYAcentos()
    {
        Assert.True(WakePhrasePolicy.MentionsName("VIERNES, dale", ["Viernes"]));
        Assert.True(WakePhrasePolicy.MentionsName("che vïernes", ["Viernes"]));
    }

    [Fact]
    public void MentionsName_ConFraseCompuesta_AlcanzaConQueAparezcaElNombre()
    {
        // El reconocedor devuelve «Hola Viernes» pero la transcripción puede traer sólo «viernes»
        // si el «hola» quedó bajo. Pedir la frase entera acá dejaría afuera casos reales.
        Assert.True(WakePhrasePolicy.MentionsName("bueno viernes, abrí Spotify", ["Hola Viernes"]));
    }

    [Fact]
    public void MentionsName_SinTexto_EsFalso()
    {
        Assert.False(WakePhrasePolicy.MentionsName("", ["Viernes"]));
        Assert.False(WakePhrasePolicy.MentionsName(null, ["Viernes"]));
    }
}
