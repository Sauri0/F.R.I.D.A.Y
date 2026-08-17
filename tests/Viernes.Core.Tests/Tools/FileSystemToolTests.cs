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
        string action, string path, string? content = null)
    {
        var arguments = content is null
            ? JsonSerializer.SerializeToElement(new { accion = action, ruta = path })
            : JsonSerializer.SerializeToElement(new { accion = action, ruta = path, contenido = content });

        return await new FileSystemTool().ExecuteAsync(
            arguments, new ToolExecutionContext("t1"), CancellationToken.None);
    }
}
