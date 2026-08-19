using Viernes.App.Shell;
using Xunit;

namespace Viernes.App.Tests;

/// <summary>
/// Lo que la ventana del nombre muestra mientras se escribe.
/// </summary>
/// <remarks>
/// El usuario no está eligiendo lo que escribe: está eligiendo el nombre normalizado y la frase con
/// la que va a tener que llamarlo. Que «ana maria» quede «Ana Maria» y que la frase sea «Hola Ana
/// Maria» son las dos sorpresas que esta vista previa existe para evitar, así que se prueban.
/// </remarks>
public class AssistantNamePreviewTests
{
    [Fact]
    public void MuestraElNombreYaNormalizado()
    {
        var (valid, message) = AssistantNamePreview.Describe("ana maria");

        Assert.True(valid);
        Assert.Contains("Ana Maria", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MuestraLaFraseConLaQueLoVaADespertar()
    {
        var (valid, message) = AssistantNamePreview.Describe("ana");

        Assert.True(valid);
        Assert.Contains("Hola Ana", message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// El primer elemento del par es el que habilita Aceptar. Un nombre que no sirve tiene que
    /// devolver <c>false</c> <em>y</em> el motivo: sin motivo, el botón apagado no explica nada.
    /// </remarks>
    [Theory]
    [InlineData("R2D2", "números")]
    [InlineData("A", "dos letras")]
    [InlineData("", "Escribí")]
    [InlineData("   ", "Escribí")]
    public void UnNombreQueNoSirveExplicaPorQue(string raw, string fragment)
    {
        var (valid, message) = AssistantNamePreview.Describe(raw);

        Assert.False(valid);
        Assert.Contains(fragment, message, StringComparison.OrdinalIgnoreCase);
    }
}
