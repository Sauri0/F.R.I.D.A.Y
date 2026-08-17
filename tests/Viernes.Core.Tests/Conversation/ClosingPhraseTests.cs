using Xunit;

namespace Viernes.Core.Tests.Conversation;

/// <summary>
/// Reglas de la frase de cierre. Es el inverso del wake word: la palabra que lo despide. Cortar de
/// más es peor que cortar de menos —te deja hablando solo—, así que la regla es deliberadamente
/// estrecha: frases cortas y sólo cuando son esencialmente la despedida.
/// </summary>
public sealed class ClosingPhraseTests
{
    private static readonly string[] Closing =
    [
        "listo", "gracias", "chau", "nada más", "nada mas", "dejá", "deja", "ya está", "ya esta",
        "terminamos", "cortá", "corta", "basta", "salí", "sali", "adiós", "adios"
    ];

    [Theory]
    [InlineData("listo")]
    [InlineData("Listo.")]
    [InlineData("gracias Viernes")]
    [InlineData("Viernes, listo")]
    [InlineData("chau")]
    [InlineData("ya está")]
    [InlineData("nada más")]
    [InlineData("terminamos")]
    public void Cierra_ConUnaDespedidaCorta(string text) => Assert.True(IsClosingPhrase(text));

    [Theory]
    [InlineData("gracias por armarme la agenda de mañana")]
    [InlineData("listo el recordatorio de las nueve o todavía no")]
    [InlineData("dejá anotado que tengo que llamar a Ana")]
    [InlineData("basta de recordatorios a la mañana, moveme todo a la tarde")]
    public void NoCierra_CuandoLaDespedidaEsParteDeUnaInstruccion(string text) =>
        Assert.False(IsClosingPhrase(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Viernes")]
    public void NoCierra_ConNadaUtil(string text) => Assert.False(IsClosingPhrase(text));

    [Fact]
    public void NoCierra_ConUnaConsultaComun() =>
        Assert.False(IsClosingPhrase("qué tengo en la agenda hoy"));

    /// <summary>Copia de la regla del runtime; el shell no es referenciable desde estas pruebas.</summary>
    private static bool IsClosingPhrase(string text)
    {
        var normalized = new string(text
            .ToLowerInvariant()
            .Where(character => !char.IsPunctuation(character))
            .ToArray())
            .Replace("viernes", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.Length == 0 || normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
        {
            return false;
        }

        return Closing.Any(phrase =>
            normalized.Equals(phrase, StringComparison.Ordinal) ||
            normalized.StartsWith(phrase + " ", StringComparison.Ordinal) ||
            normalized.EndsWith(" " + phrase, StringComparison.Ordinal));
    }
}
