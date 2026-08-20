using System.Text.Json;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Leer una página: lo que devuelve, y sobre todo lo que se niega a abrir.
/// </summary>
/// <remarks>
/// Es la herramienta con más superficie peligrosa de todas: trae texto que escribió un desconocido y
/// puede alcanzar cualquier dirección. Las dos cosas que hay que sostener son que <b>lo que trae es
/// dato y nunca una orden</b>, y que <b>no sirve para mirar adentro de la red del usuario</b>.
/// <para>
/// Lo que sale a internet de verdad no se prueba acá: eso pide red y un servidor que no se cae.
/// Lo que sí se prueba es todo lo que decide <em>antes</em> de salir, que es donde están los
/// defectos que importan.
/// </para>
/// </remarks>
public sealed class WebReadToolTests
{
    private static async Task<ToolExecutionResult> LeerAsync(string url)
    {
        var tool = new WebReadTool();
        var argumentos = JsonDocument.Parse(JsonSerializer.Serialize(new { url })).RootElement;

        return await tool.ExecuteAsync(
            argumentos,
            new ToolExecutionContext("c1"),
            CancellationToken.None);
    }

    /// <summary>
    /// No abre nada que apunte a la máquina del usuario ni a su red.
    /// </summary>
    /// <remarks>
    /// Una dirección puede apuntar a un router, a una impresora, a la consola de un servicio que
    /// confía en quien esté del lado de adentro, o al metadato de una nube en 169.254.169.254 — que
    /// es de donde se sacan credenciales de máquina. Nada de eso se lee.
    /// </remarks>
    [Theory]
    [InlineData("http://localhost:8080/admin")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://0.0.0.0/")]
    public async Task LoQueApuntaALaRedInterna_NoSeAbre(string url)
    {
        var resultado = await LeerAsync(url);

        Assert.Equal(ToolExecutionStatus.Failed, resultado.Status);
        Assert.Contains("red interna", resultado.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sólo direcciones de internet, y no cualquier cosa con dos puntos.</summary>
    /// <remarks>
    /// <c>file://</c> leería un archivo del disco sin pasar por la herramienta de archivos ni por su
    /// política, y las otras son formas conocidas de hacerle pedir algo raro a una biblioteca.
    /// </remarks>
    [Theory]
    [InlineData("file:///C:/Users/brank/claves.json")]
    [InlineData("ftp://ejemplo.com/x")]
    [InlineData("data:text/html,<b>hola</b>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("no soy una direccion")]
    public async Task LoQueNoEsUnaDireccionDeInternet_SeRechaza(string url)
    {
        var resultado = await LeerAsync(url);

        Assert.Equal(ToolExecutionStatus.Failed, resultado.Status);
        Assert.Contains("http", resultado.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SinDireccion_LoDiceYNoTira()
    {
        var tool = new WebReadTool();
        var resultado = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement,
            new ToolExecutionContext("c1"),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, resultado.Status);
    }

    /// <summary>
    /// La descripción le dice al modelo que lo que trae no son órdenes.
    /// </summary>
    /// <remarks>
    /// Es la mitad barata de la defensa contra una página escrita para darle órdenes. La otra mitad
    /// —el marco que envuelve cada respuesta— sólo se ve con una página de verdad del otro lado.
    /// </remarks>
    [Fact]
    public void LaDescripcion_AvisaQueEsContenidoAjeno()
    {
        var definicion = new WebReadTool().Definition;

        Assert.Equal("leer_web", definicion.Name);
        Assert.Contains("NUNCA", definicion.Description, StringComparison.Ordinal);
        Assert.Contains("instrucción", definicion.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Está registrada entre las de siempre, o no existe para nadie.</summary>
    [Fact]
    public void QuedaRegistradaEntreLasHerramientas()
    {
        var herramientas = BuiltInTools.Create(new Viernes.Core.Persistence.InMemoryUserDataStore());

        Assert.Contains(herramientas, tool => tool.Definition.Name == WebReadTool.ToolName);
    }
}
