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
    [InlineData("clave: MiSecretoQueNadieSabe")]
    [InlineData("el pin es 481516")]
    [InlineData("secret = MiSecretoQueNadieSabe")]
    [InlineData("Pwd=MiSecretoQueNadieSabe;Database=viernes")]
    [InlineData("https://api.ejemplo.com/v1?token=abc123secreto&page=2")]
    public void LoQuePareceUnaCredencial_SeReconoceYSeTapa(string texto)
    {
        Assert.True(CredentialLikeText.Looks(texto));
        Assert.Contains(CredentialLikeText.Placeholder, CredentialLikeText.Redact(texto), StringComparison.Ordinal);
    }

    /// <summary>
    /// Una clave de puras letras dictada en prosa se tapa en la charla, pero no impide guardar.
    /// </summary>
    /// <remarks>
    /// Es exactamente el borde entre los dos umbrales, y por eso está escrito como prueba y no como
    /// comentario: en la charla no cuesta nada taparla, y negarse a guardar un recordatorio porque
    /// dice «la clave es» seguido de una palabra larga sí cuesta.
    /// </remarks>
    [Theory]
    [InlineData("la clave es MiSecretoQueNadieSabe")]
    [InlineData("la contraseña es difícilísimadeadivinar")]
    public void DictadaSinNingunDigito_SeTapaPeroNoImpideGuardar(string texto)
    {
        Assert.Contains(CredentialLikeText.Placeholder, CredentialLikeText.Redact(texto), StringComparison.Ordinal);
        Assert.False(CredentialLikeText.Looks(texto));
    }

    /// <summary>
    /// Lo que se rechaza siempre se tapa. Nunca al revés.
    /// </summary>
    /// <remarks>
    /// <b>Es la prueba que hace que los dos umbrales no se separen.</b> Son la misma idea con dos
    /// exigencias distintas —rechazar es caro, tapar es barato— y lo único que no puede pasar es que
    /// algo se niegue a guardarse y después no se tape en la charla. Si alguien afloja el ancho o
    /// endurece el estricto sin mirar el otro, esto se cae.
    /// </remarks>
    [Theory]
    [InlineData("la clave es Casa12345")]
    [InlineData("el pin es 481516")]
    [InlineData("la clave del wifi es Casa12345")]
    [InlineData("recordame comprar pan")]
    [InlineData("la clave es la constancia")]
    public void LoQueSeRechaza_SiempreSeTapa(string texto)
    {
        if (CredentialLikeText.Looks(texto))
        {
            Assert.NotEqual(texto, CredentialLikeText.Redact(texto));
        }
    }

    /// <summary>
    /// Negarse a guardar algo es caro, así que el umbral para eso es más alto.
    /// </summary>
    /// <remarks>
    /// <b>Salió de un defecto de verdad.</b> Al unificar los dos reconocedores quedó uno solo con el
    /// umbral ancho, y el guardia —el que impide guardar una memoria con una clave adentro— empezó a
    /// rechazar recordatorios perfectamente legítimos. Una asistente que se niega a anotar «la clave
    /// del examen es la constancia» no está cuidando nada: está rota.
    /// </remarks>
    [Theory]
    [InlineData("recordame que la clave del examen es la constancia")]
    [InlineData("anotá que el pin del turno es en la puerta de atrás")]
    [InlineData("la contraseña es difícil de recordar")]
    public void UnaFraseDeTodosLosDias_SeGuardaAunqueSeTape(string texto)
    {
        Assert.False(CredentialLikeText.Looks(texto));
    }

    /// <summary>Y una clave dictada de verdad sí se rechaza, con complemento y todo.</summary>
    /// <remarks>
    /// «La clave DEL WIFI es …» es como se dice de verdad, y antes no coincidía: la expresión exigía
    /// que la palabra estuviera pegada al verbo.
    /// </remarks>
    [Theory]
    [InlineData("la clave del wifi es Casa12345")]
    [InlineData("la contraseña de la compu es Bruno2019")]
    [InlineData("el pin es 481516")]
    public void UnaClaveDictadaDeVerdad_SeRechaza(string texto)
    {
        Assert.True(CredentialLikeText.Looks(texto));
    }

    /// <summary>Las formas propias de un servicio se reconocen y se tapan siempre.</summary>
    /// <remarks>
    /// Van por el armador y no como literales: escritas enteras, GitHub bloquea el envío del
    /// repositorio porque parecen claves de verdad — y hace bien, no tiene cómo saber que no lo son.
    /// </remarks>
    [Theory]
    [MemberData(nameof(CredencialesDeMentira.Conocidas), MemberType = typeof(CredencialesDeMentira))]
    public void ConSuFormaDeSiempre_SeReconoceYSeTapa(string credencial)
    {
        Assert.True(CredentialLikeText.Looks(credencial));
        Assert.Contains(CredentialLikeText.Placeholder, CredentialLikeText.Redact(credencial), StringComparison.Ordinal);
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
