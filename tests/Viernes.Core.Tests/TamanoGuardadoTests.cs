using Viernes.Core.Configuration;
using Viernes.Platform.Windows.Storage;
using Xunit;

namespace Viernes.Core.Tests;

/// <summary>
/// El tamaño del orbe se guarda como fracción, y lo que llega del archivo se recorta.
/// </summary>
/// <remarks>
/// El archivo de preferencias es texto y se puede editar a mano: alguien que escriba <c>50</c>
/// queriendo decir «50 %» pediría un orbe de 5400 px y una ventana de 5800, o sea nada visible en
/// ninguna pantalla y ninguna forma de volver atrás sin borrar el archivo. El recorte va en la
/// normalización, que es por donde pasan tanto lo que se lee como lo que se escribe.
/// </remarks>
public sealed class TamanoGuardadoTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-tamano-" + Guid.NewGuid().ToString("N")[..8]);

    private string Escribir(string contenido)
    {
        Directory.CreateDirectory(_carpeta);
        var ruta = Path.Combine(_carpeta, "settings.json");
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_carpeta))
            {
                Directory.Delete(_carpeta, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task UnArchivoSinTamañoDejaAlOrbeComoSalióDeFábrica()
    {
        Escribir("""{ "schemaVersion": 1, "assistantName": "Ana" }""");

        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.True(leido.LoadedFromDisk);
        Assert.Equal(OrbScaleRange.Default, leido.Settings.OrbScale, 3);
    }

    [Theory]
    [InlineData("1.25", 1.25)]
    [InlineData("0.5", 0.5)]
    [InlineData("2", 2.0)]
    [InlineData("50", 2.0)]      // «50» por «50 %»: 5400 px de orbe.
    [InlineData("-3", 0.5)]
    [InlineData("0", 0.5)]
    public async Task ElTamañoLeídoSeRecortaAlRangoLegal(string escrito, double esperado)
    {
        Escribir($$"""{ "schemaVersion": 1, "orbScale": {{escrito}} }""");

        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.Equal(esperado, leido.Settings.OrbScale, 3);
    }

    [Fact]
    public async Task ElTamañoTambiénSeRecortaAlGuardarlo()
    {
        var almacen = new LocalSettingsStore(_carpeta);

        var guardado = await almacen.SaveAsync(new ViernesLocalSettings { OrbScale = 9 });
        Assert.True(guardado.Succeeded);

        var leido = await almacen.LoadAsync();
        Assert.Equal(OrbScaleRange.Maximum, leido.Settings.OrbScale, 3);
    }

    [Fact]
    public async Task UnTamañoRotoNoSeLlevaPuestoElRestoDelArchivo()
    {
        Escribir("""
            {
              "schemaVersion": 1,
              "assistantName": "Ana Maria",
              "orbShape": "Nube",
              "orbScale": "grande"
            }
            """);

        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.True(leido.LoadedFromDisk);
        Assert.Equal("Ana Maria", leido.Settings.AssistantName);
        Assert.Equal("Nube", leido.Settings.OrbShape);
        Assert.Equal(OrbScaleRange.Default, leido.Settings.OrbScale, 3);
    }
}
