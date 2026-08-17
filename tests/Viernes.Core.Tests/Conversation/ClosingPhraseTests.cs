using Viernes.Core.Conversation;
using Xunit;

namespace Viernes.Core.Tests.Conversation;

/// <summary>
/// Reglas de la frase de cierre. Cortar de más es peor que cortar de menos —te deja hablando solo—,
/// así que lo ambiguo sólo cierra cuando la frase entera es corta.
/// </summary>
/// <remarks>
/// Antes este archivo <em>copiaba</em> la regla adentro, porque el runtime vivía en el proyecto de
/// la aplicación y desde acá no se puede referenciar. La copia pasaba en verde mientras el código
/// real hacía otra cosa: afirmaba que «basta de recordatorios…» no cerraba, y en el equipo cerraba.
/// Ahora la regla vive en el núcleo y estas pruebas ejercitan la de verdad.
/// </remarks>
public sealed class ClosingPhraseTests
{
    [Theory]
    [InlineData("listo")]
    [InlineData("Listo.")]
    [InlineData("gracias Viernes")]
    [InlineData("Viernes, listo")]
    [InlineData("chau")]
    [InlineData("ya está")]
    [InlineData("nada más")]
    [InlineData("terminamos")]
    [InlineData("basta")]
    [InlineData("callate")]
    [InlineData("descansá")]
    public void Cierra_ConUnaDespedidaCorta(string text) =>
        Assert.True(ClosingPhrase.IsClosing(text));

    /// <remarks>
    /// Una orden explícita no se puede confundir con otra cosa, así que vale a cualquier largo:
    /// «no, no, no, dejá de oír» son seis palabras y es exactamente cómo suena alguien pidiendo que
    /// pare.
    /// </remarks>
    [Theory]
    [InlineData("no, no, no, dejá de oír")]
    [InlineData("bueno Viernes, andá a dormir que ya terminamos por hoy")]
    [InlineData("che, dejá de escucharme un rato por favor")]
    public void Cierra_ConUnaOrdenExplicitaAunqueSeaLarga(string text) =>
        Assert.True(ClosingPhrase.IsClosing(text));

    /// <remarks>
    /// El caso que fallaba en el equipo del usuario. «basta», «cortá» y «terminá» son palabras
    /// corrientes: aparecen adentro de pedidos que piden justamente seguir trabajando.
    /// </remarks>
    [Theory]
    [InlineData("basta de recordatorios a la mañana, moveme todo a la tarde")]
    [InlineData("cortá el video por la mitad y guardalo en el escritorio")]
    [InlineData("terminá de escribir eso que dejaste a medias")]
    [InlineData("gracias por armarme la agenda de mañana")]
    [InlineData("listo el recordatorio de las nueve o todavía no")]
    [InlineData("dejá anotado que tengo que llamar a Ana")]
    public void NoCierra_CuandoLaPalabraEsParteDeUnaInstruccion(string text) =>
        Assert.False(ClosingPhrase.IsClosing(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Viernes")]
    [InlineData(null)]
    public void NoCierra_ConNadaUtil(string? text) =>
        Assert.False(ClosingPhrase.IsClosing(text));

    [Fact]
    public void NoCierra_ConUnaConsultaComun() =>
        Assert.False(ClosingPhrase.IsClosing("qué tengo en la agenda hoy"));

    /// <remarks>
    /// La transcripción acentúa según le parece. Si la comparación dependiera del acento, la mitad
    /// de las despedidas fallarían al azar.
    /// </remarks>
    [Theory]
    [InlineData("Dejá de oír", "deja de oir")]
    [InlineData("¡Listo!", "listo")]
    [InlineData("no,  no,  no", "no no no")]
    public void Normalize_PliegaAcentosYPuntuacion(string raw, string expected) =>
        Assert.Equal(expected, ClosingPhrase.Normalize(raw));
}
