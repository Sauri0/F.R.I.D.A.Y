using Viernes.Memory.Chats;
using Xunit;

namespace Viernes.Memory.Tests.Chats;

/// <summary>
/// Lo que parece una credencial no llega al disco.
/// </summary>
/// <remarks>
/// Sobre esto se apoyan dos cosas que el usuario ve: <c>MemoryPrivacy.StoresCredentials = false</c>,
/// y la frase «lo que parece una credencial se tapa antes de escribirse» que se le muestra a él y a
/// otros agentes por el conector.
/// <para>
/// Estos casos salieron de una auditoría adversarial. Ninguno es rebuscado: son las formas en que
/// una credencial aparece de verdad en una charla — dictada en voz alta, pegada en una dirección,
/// adentro de una cadena de conexión.
/// </para>
/// <para>
/// <b>Se prueban contra el archivo escrito y no contra el reconocedor</b>, porque lo que importa no
/// es que la expresión regular sea linda sino que la credencial no esté en el disco.
/// </para>
/// </remarks>
public sealed class TapadoDeCredencialesTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-tapado-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            // Limpiar de más no importa.
        }
    }

    private string Escribir(string dicho)
    {
        using (var charla = ChatArchive.Open(_carpeta, "escribiendo"))
        {
            charla.Note(ChatVoice.Persona, dicho);
            charla.Close();
        }

        var archivo = Directory.GetFiles(_carpeta, "*.md").Single();
        var texto = File.ReadAllText(archivo);
        File.Delete(archivo);
        return texto;
    }

    /// <summary>La forma en que alguien la diría hablando.</summary>
    /// <remarks>
    /// El comentario del tapado prometía cubrir «las frases del tipo “la clave es …”» y el
    /// reconocedor sólo tenía «clave secreta». Nadie dicta «mi clave secreta es»: dice «la clave es».
    /// </remarks>
    [Theory]
    [InlineData("la clave es MiSecretoQueNadieSabe", "MiSecretoQueNadieSabe")]
    [InlineData("clave: MiSecretoQueNadieSabe", "MiSecretoQueNadieSabe")]
    [InlineData("el pin es 481516", "481516")]
    [InlineData("secret = MiSecretoQueNadieSabe", "MiSecretoQueNadieSabe")]
    [InlineData("Pwd=MiSecretoQueNadieSabe;Database=viernes", "MiSecretoQueNadieSabe")]
    public void DichaEnCastellano_NoLlegaAlDisco(string dicho, string secreto)
    {
        var texto = Escribir(dicho);

        Assert.DoesNotContain(secreto, texto, StringComparison.Ordinal);
        Assert.Contains("credencial", texto, StringComparison.Ordinal);
    }

    /// <summary>Las formas de los servicios que el usuario usa o podría usar.</summary>
    [Theory]
    [MemberData(nameof(Viernes.Memory.Tests.Privacy.CredencialesDeMentira.Conocidas), MemberType = typeof(Viernes.Memory.Tests.Privacy.CredencialesDeMentira))]
    public void ConSuFormaDeSiempre_NoLlegaAlDisco(string credencial)
    {
        var texto = Escribir($"pegá esto donde va: {credencial}");

        Assert.DoesNotContain(credencial, texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// Un token pegado adentro de una dirección.
    /// </summary>
    /// <remarks>
    /// Es la forma más fácil de filtrar una credencial sin darse cuenta: se pega el enlace entero.
    /// No podía coincidir nunca porque toda la alternancia estaba anclada a un borde de palabra, y
    /// una dirección empieza el token con <c>?</c> o <c>&amp;</c>.
    /// </remarks>
    [Fact]
    public void UnTokenEnUnaDireccion_NoLlegaAlDisco()
    {
        var texto = Escribir("mirá esto: https://api.ejemplo.com/v1/cosas?token=abc123secreto&page=2");

        Assert.DoesNotContain("abc123secreto", texto, StringComparison.Ordinal);

        // Y lo que no es el token sigue estando: tapar no es tirar la charla.
        Assert.Contains("api.ejemplo.com", texto, StringComparison.Ordinal);
    }

    /// <summary>Una charla normal no se tapa entera.</summary>
    /// <remarks>
    /// El tapado prefiere tapar de más, y eso tiene un costo: hay que verificar que ese costo no sea
    /// una charla ilegible. Una frase sin nada parecido a una credencial tiene que quedar intacta.
    /// </remarks>
    [Fact]
    public void UnaCharlaNormal_QuedaIntacta()
    {
        const string normal = "abrime el navegador y buscá cuánto sale el pasaje a Bariloche en marzo";

        Assert.Contains(normal, Escribir(normal), StringComparison.Ordinal);
    }
}
