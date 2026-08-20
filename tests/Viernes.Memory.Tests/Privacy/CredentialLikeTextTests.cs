using Viernes.Memory.Privacy;
using Xunit;

namespace Viernes.Memory.Tests.Privacy;

/// <summary>
/// El único reconocedor de credenciales del proyecto.
/// </summary>
/// <remarks>
/// <b>Había dos y eso ya costó.</b> El rechazo de memorias tenía el suyo y el tapado de charlas
/// tenía otro, casi iguales; al ensanchar uno el otro se quedó con la red vieja, y lo que decían
/// distinto era qué llega al disco del usuario. Estas pruebas están sobre el que quedó, y las dos
/// operaciones —rechazar y tapar— se prueban juntas justamente para que no se separen de nuevo.
/// </remarks>
public sealed class CredentialLikeTextTests
{
    [Theory]
    [InlineData("la clave es MiSecretoQueNadieSabe")]
    [InlineData("clave: MiSecretoQueNadieSabe")]
    [InlineData("el pin es 481516")]
    [InlineData("secret = MiSecretoQueNadieSabe")]
    [InlineData("Pwd=MiSecretoQueNadieSabe;Database=viernes")]
    [InlineData("sk-or-v1-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("sk_live_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("https://api.ejemplo.com/v1?token=abc123secreto&page=2")]
    public void LoQuePareceUnaCredencial_SeReconoceYSeTapa(string texto)
    {
        Assert.True(CredentialLikeText.Looks(texto));
        Assert.Contains(CredentialLikeText.Placeholder, CredentialLikeText.Redact(texto), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reconocer y tapar no pueden separarse.
    /// </summary>
    /// <remarks>
    /// Es la prueba que hace falta para que el arreglo dure. Si algún día alguien vuelve a escribir
    /// una expresión aparte para una de las dos operaciones, esto se cae — que es exactamente lo que
    /// no pasó la primera vez, porque no había nada mirándolo.
    /// </remarks>
    [Theory]
    [InlineData("la clave es MiSecretoQueNadieSabe")]
    [InlineData("abrime el navegador y buscá el clima")]
    [InlineData("recordame comprar pan")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    public void ReconocerYTapar_SiempreDicenLoMismo(string texto)
    {
        var reconoce = CredentialLikeText.Looks(texto);
        var tapa = CredentialLikeText.Redact(texto) != texto;

        Assert.Equal(reconoce, tapa);
    }

    [Theory]
    [InlineData("abrime el navegador y buscá cuánto sale el pasaje a Bariloche")]
    [InlineData("recordame llamar al médico mañana a las tres")]
    [InlineData("anotá que el martes tengo turno")]
    public void UnaFraseNormal_NoSeReconoceNiSeTapa(string texto)
    {
        Assert.False(CredentialLikeText.Looks(texto));
        Assert.Equal(texto, CredentialLikeText.Redact(texto));
    }

    [Fact]
    public void LoVacio_NoTira()
    {
        Assert.False(CredentialLikeText.Looks(null));
        Assert.False(CredentialLikeText.Looks("   "));
        Assert.Equal(string.Empty, CredentialLikeText.Redact(null));
    }
}
