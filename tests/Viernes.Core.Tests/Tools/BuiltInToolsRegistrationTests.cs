using Viernes.Core.Persistence;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Fija qué herramientas se declaran según el modo, que es lo que se paga en tokens cada turno.
/// </summary>
/// <remarks>
/// Con la búsqueda del proveedor encendida, <c>web_search</c> no busca nada —los resultados ya vienen
/// inyectados en el contexto del turno— y lo único que hacía era contestar que no hacía falta
/// llamarla. Igual su esquema viajaba en cada pedido, y encima invitaba al modelo a gastar una vuelta
/// entera para no obtener nada.
/// </remarks>
public sealed class BuiltInToolsRegistrationTests
{
    [Fact]
    public void ConBusquedaDelProveedor_WebSearchNiSeRegistra()
    {
        var tools = BuiltInTools.Create(new InMemoryUserDataStore(), providerWebSearch: true);

        Assert.DoesNotContain(tools, tool => tool.Definition.Name == WebSearchTool.ToolName);
    }

    [Fact]
    public void SinBusquedaDelProveedor_WebSearchSigueEstandoParaDecirQueEstaApagada()
    {
        var tools = BuiltInTools.Create(new InMemoryUserDataStore(), providerWebSearch: false);

        Assert.Contains(tools, tool => tool.Definition.Name == WebSearchTool.ToolName);
    }
}
