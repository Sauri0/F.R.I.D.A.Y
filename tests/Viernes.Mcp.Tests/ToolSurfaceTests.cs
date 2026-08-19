using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// Que lo que se escribió llegue de verdad a la lista que publica el proceso.
/// </summary>
/// <remarks>
/// El hallazgo más repetido de los revisores de este repositorio es código que nadie llama. Un
/// método del conector que no quedó enganchado a ninguna herramienta compila, tiene sus pruebas en
/// verde y no existe para Claude. Estas pruebas miran exactamente la configuración que arma el
/// arranque —<see cref="ConnectorServer.CreateOptions"/>—, no una lista escrita al lado.
/// </remarks>
public sealed class ToolSurfaceTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public void ElServidorPublicaLasDiezHerramientas()
    {
        var options = ConnectorServer.CreateOptions(this.harness.Connector);

        Assert.NotNull(options.ToolCollection);
        var names = options.ToolCollection.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(10, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.StartsWith("viernes_", name, StringComparison.Ordinal));

        Assert.Contains("viernes_estado", names);
        Assert.Contains("viernes_misiones_listar", names);
        Assert.Contains("viernes_mision_crear", names);
        Assert.Contains("viernes_mision_avanzar", names);
        Assert.Contains("viernes_mision_cerrar", names);
        Assert.Contains("viernes_mision_preguntar", names);
        Assert.Contains("viernes_memoria_buscar", names);
        Assert.Contains("viernes_memoria_proponer", names);
        Assert.Contains("viernes_proyectos_listar", names);
        Assert.Contains("viernes_proyecto_escribir", names);
    }

    [Fact]
    public void CadaHerramientaSePresentaConSuExplicacion()
    {
        var options = ConnectorServer.CreateOptions(this.harness.Connector);

        Assert.NotNull(options.ToolCollection);
        Assert.All(
            options.ToolCollection,
            tool => Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description)));

        // Las instrucciones del servidor son donde Claude se entera de la frontera al conectarse.
        Assert.Contains("no aprueba memoria", options.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("no pasa por encima de los permisos", options.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void LaHerramientaDeEscribirAvisaEnSuPropiaDescripcionQueNoFunciona()
    {
        var options = ConnectorServer.CreateOptions(this.harness.Connector);

        Assert.NotNull(options.ToolCollection);
        var escribir = Assert.Single(
            options.ToolCollection,
            tool => tool.ProtocolTool.Name == "viernes_proyecto_escribir");

        // Que lo diga la descripción y no sólo la respuesta ahorra una llamada inútil y, sobre todo,
        // evita que Claude le prometa al usuario algo que no va a pasar.
        Assert.Contains("HOY NO FUNCIONA", escribir.ProtocolTool.Description, StringComparison.Ordinal);
    }
}
