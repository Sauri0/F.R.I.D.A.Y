using Viernes.Core.Live;
using Xunit;

namespace Viernes.Core.Tests.Live;

public sealed class LiveEndpointTests
{
    [Fact]
    public void LaDireccionSinClave_EsLaVerificadaContraElSdkOficial()
    {
        Assert.Equal(
            "wss://generativelanguage.googleapis.com/ws/" +
            "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
            LiveEndpoint.Redacted);
    }

    [Fact]
    public void LaClaveViajaEnLaCadenaDeConsultaYEscapada()
    {
        var uri = LiveEndpoint.Build("cla ve+rara");

        // Se mira Query y no ToString a propósito: ToString devuelve la forma legible y desescapa lo
        // que se escapó. O sea que loguear la Uri no sólo filtra la clave, la filtra en claro. Es la
        // razón de que exista LiveEndpoint.Redacted.
        Assert.Equal("?key=cla%20ve%2Brara", uri.Query);
        Assert.Equal("cla ve+rara", Uri.UnescapeDataString(uri.Query["?key=".Length..]));
    }

    [Fact]
    public void LaVersionParaMostrar_NoTieneLaClave()
    {
        // Es lo único que puede terminar escrito en un mensaje de error o en un archivo de registro.
        Assert.DoesNotContain("key", LiveEndpoint.Redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", LiveEndpoint.Redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SinClave_SeQuejaEnVezDeArmarUnaDireccionInvalida()
    {
        Assert.Throws<ArgumentException>(() => LiveEndpoint.Build(""));
        Assert.Throws<ArgumentException>(() => LiveEndpoint.Build("   "));
    }
}
