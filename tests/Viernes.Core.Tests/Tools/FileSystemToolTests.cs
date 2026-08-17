using System.Text.Json;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Cubre la diferencia entre archivo y carpeta, que es donde la herramienta mentía.
/// </summary>
/// <remarks>
/// Sin acción para carpetas, «creame una carpeta» terminaba en <c>escribir</c> y creaba un archivo
/// sin extensión con ese nombre; el mensaje de éxito no distinguía uno de otro, así que el engaño
/// recién aparecía al intentar abrirla. Estas pruebas fijan las dos mitades: que crear una carpeta
/// cree una carpeta de verdad, y que escribir se niegue cuando lo que le piden es claramente una.
/// </remarks>
public sealed class FileSystemToolTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "viernes-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task CrearCarpeta_DejaUnaCarpetaYNoUnArchivo()
    {
        var target = Path.Combine(this.root, "Proyecto");

        var result = await RunAsync("carpeta", target);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(Directory.Exists(target));
        Assert.False(File.Exists(target));
        Assert.Contains("carpeta", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrearCarpeta_SobreUnaQueYaEstaNoFalla()
    {
        var target = Path.Combine(this.root, "Proyecto");
        await RunAsync("carpeta", target);

        var result = await RunAsync("carpeta", target);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Escribir_SobreUnaCarpetaSeNiega()
    {
        var target = Path.Combine(this.root, "Proyecto");
        Directory.CreateDirectory(target);

        var result = await RunAsync("escribir", target, "algo");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Escribir_SinExtensionNiContenidoNoInventaUnArchivo()
    {
        var target = Path.Combine(this.root, "Parece Una Carpeta");

        var result = await RunAsync("escribir", target);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
        Assert.Contains("accion=carpeta", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// El peor bug que tuvo esta herramienta. «Mové el informe a Trabajo», con Trabajo inexistente,
    /// renombraba <c>informe.docx</c> a un archivo llamado «Trabajo» sin extensión y contestaba que
    /// lo había movido: el archivo seguía existiendo, sin nombre reconocible ni programa que lo
    /// abriera, que es la forma más difícil de notar que se perdió algo.
    /// </remarks>
    [Fact]
    public async Task Mover_ACarpetaInexistente_LaCreaYNoRenombraElArchivo()
    {
        var origen = Path.Combine(this.root, "informe.docx");
        Directory.CreateDirectory(this.root);
        await File.WriteAllTextAsync(origen, "importante");
        var carpeta = Path.Combine(this.root, "Trabajo");

        var result = await RunAsync("mover", origen, destination: carpeta);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(Directory.Exists(carpeta));
        Assert.False(File.Exists(carpeta));
        Assert.Equal("importante", await File.ReadAllTextAsync(
            Path.Combine(carpeta, "informe.docx")));
    }

    [Fact]
    public async Task Mover_ADestinoConExtension_SigueSiendoUnRenombre()
    {
        var origen = Path.Combine(this.root, "nota.txt");
        Directory.CreateDirectory(this.root);
        await File.WriteAllTextAsync(origen, "x");
        var destino = Path.Combine(this.root, "copia.txt");

        await RunAsync("mover", origen, destination: destino);

        Assert.True(File.Exists(destino));
        Assert.False(Directory.Exists(destino));
    }

    [Fact]
    public async Task Copiar_SobreAlgoExistente_GuardaLoPisado()
    {
        Directory.CreateDirectory(this.root);
        var origen = Path.Combine(this.root, "nuevo.txt");
        var destino = Path.Combine(this.root, "viejo.txt");
        await File.WriteAllTextAsync(origen, "nuevo");
        await File.WriteAllTextAsync(destino, "el que se pisa");

        var result = await RunAsync("copiar", origen, destination: destino);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Contains("papelera", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// La herramienta prometía «se puede recuperar» en cada borrado y la acción no existía: la única
    /// forma era abrir la papelera a mano y adivinar cuál de las carpetas con fecha era.
    /// </remarks>
    [Fact]
    public async Task Recuperar_SinRutaListaLoQueHay()
    {
        var result = await RunAsync("recuperar", path: null);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Recuperar_LoQueNoEstaFallaEnVezDeInventar()
    {
        var result = await RunAsync("recuperar", "no-existe-ningun-archivo-asi.txt");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Abrir_LoQueNoExisteFallaEnVezDeAbrirOtraCosa()
    {
        var result = await RunAsync("abrir", Path.Combine(this.root, "Fantasma"));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("escritorio\\Sub")]
    [InlineData("escritorio/Sub")]
    public async Task RutaHablada_TambienFuncionaComoPrefijo(string spoken)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Sub");

        try
        {
            var result = await RunAsync("carpeta", spoken);

            Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            Directory.Delete(expected, recursive: true);
        }
    }

    [Fact]
    public async Task RutaRelativa_CaeEnElEscritorioYNoJuntoAlEjecutable()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SueltaViernes");

        try
        {
            await RunAsync("carpeta", "SueltaViernes");

            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            Directory.Delete(expected, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private static async Task<ToolExecutionResult> RunAsync(
        string action, string? path = null, string? content = null, string? destination = null)
    {
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>(
            new[]
            {
                new KeyValuePair<string, string>("accion", action),
                new KeyValuePair<string, string>("ruta", path!),
                new KeyValuePair<string, string>("contenido", content!),
                new KeyValuePair<string, string>("destino", destination!)
            }.Where(pair => pair.Value is not null)));

        return await new FileSystemTool().ExecuteAsync(
            arguments, new ToolExecutionContext("t1"), CancellationToken.None);
    }
}
