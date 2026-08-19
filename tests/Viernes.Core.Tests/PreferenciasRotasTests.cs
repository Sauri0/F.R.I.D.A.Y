using Viernes.Platform.Windows.Storage;
using Xunit;

namespace Viernes.Core.Tests;

/// <summary>
/// Que un campo ilegible cueste ese campo y no el archivo entero.
/// </summary>
/// <remarks>
/// Existe por la promesa de que actualizar no hace perder nada. Antes, cualquier propiedad con el
/// tipo cambiado —una versión futura que convierta <c>orbShape</c> de texto a objeto, alguien que
/// edite el archivo a mano y se coma una comilla— hacía fallar la lectura entera, y lo que se
/// devolvía eran preferencias <b>nuevas y vacías</b>: el asistente volvía a llamarse Viernes, el orbe
/// a ser una gota, la posición se olvidaba. Y el primer guardado escribía eso encima del archivo. En
/// silencio.
/// <para>
/// El escenario no es hipotético: es exactamente lo que pasa la primera vez que alguien actualice a
/// una versión que cambie el tipo de un campo, que es el caso que el instalador promete cubrir.
/// </para>
/// </remarks>
public sealed class PreferenciasRotasTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-prefs-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Completo = """
        {
          "schemaVersion": 1,
          "assistantName": "Ana Maria",
          "orbShape": "Nube",
          "microphoneMuted": true,
          "listenWhileHidden": false,
          "followActiveMonitor": true,
          "recognitionCulture": "es-AR",
          "preferredOpenRouterModel": "un/modelo",
          "widgetLeft": 123.5,
          "widgetTop": 456.5
        }
        """;

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
    public async Task UnArchivoSanoSeLeeEntero()
    {
        Escribir(Completo);
        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.True(leido.LoadedFromDisk);
        Assert.Null(leido.ErrorMessage);
        Assert.Equal("Ana Maria", leido.Settings.AssistantName);
        Assert.Equal("Nube", leido.Settings.OrbShape);
        Assert.True(leido.Settings.MicrophoneMuted);
        Assert.Equal(123.5, leido.Settings.WidgetLeft);
    }

    [Theory]
    [InlineData("\"orbShape\": \"Nube\"", "\"orbShape\": { \"tipo\": \"Nube\" }")]   // texto -> objeto
    [InlineData("\"microphoneMuted\": true", "\"microphoneMuted\": \"si\"")]        // bool -> texto
    [InlineData("\"widgetLeft\": 123.5", "\"widgetLeft\": \"izquierda\"")]          // numero -> texto
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": [1, 2]")]               // numero -> lista
    public async Task UnCampoRotoNoSeLlevaElNombreNiElResto(string original, string roto)
    {
        Escribir(Completo.Replace(original, roto, StringComparison.Ordinal));
        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        // Lo importante: el nombre del asistente sobrevive. Es lo que el usuario eligió y lo que
        // decide con qué palabras lo despierta.
        Assert.Equal("Ana Maria", leido.Settings.AssistantName);
        Assert.True(leido.LoadedFromDisk, "Se descartó el archivo entero por un solo campo.");

        // Y se dice qué se perdió, en vez de perderlo callado.
        Assert.False(string.IsNullOrWhiteSpace(leido.ErrorMessage));
    }

    [Fact]
    public async Task LosCamposBuenosQueQuedanSiguenValiendo()
    {
        Escribir(Completo.Replace("\"orbShape\": \"Nube\"", "\"orbShape\": { \"tipo\": \"Nube\" }", StringComparison.Ordinal));
        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.Equal("Ana Maria", leido.Settings.AssistantName);
        Assert.True(leido.Settings.MicrophoneMuted);
        Assert.True(leido.Settings.FollowActiveMonitor);
        Assert.Equal(123.5, leido.Settings.WidgetLeft);
        Assert.Equal(456.5, leido.Settings.WidgetTop);
        Assert.Equal("un/modelo", leido.Settings.PreferredOpenRouterModel);

        // El roto vuelve a su valor de fábrica, que es lo correcto: no se puede inventar.
        Assert.Equal("Gota", leido.Settings.OrbShape);
    }

    [Fact]
    public async Task UnArchivoQueNoSeParseaSeGuardaAlLadoEnVezDePerderse()
    {
        // Truncado a la mitad: no hay nada que rescatar campo por campo. Lo que sí hay que hacer es
        // no destruirlo, porque el primer guardado lo va a pisar.
        Escribir("{ \"assistantName\": \"Ana Maria\", \"orbShape\": ");
        var almacen = new LocalSettingsStore(_carpeta);
        var leido = await almacen.LoadAsync();

        Assert.False(leido.LoadedFromDisk);
        Assert.False(string.IsNullOrWhiteSpace(leido.ErrorMessage));

        var copias = Directory.GetFiles(_carpeta, "settings.json.roto-*");
        Assert.True(copias.Length == 1, $"Se esperaba una copia del archivo ilegible, hay {copias.Length}.");
        Assert.Contains("Ana Maria", await File.ReadAllTextAsync(copias[0]), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(copias[0]), leido.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnCampoQueLaVersionNoConoceNoMolesta()
    {
        // El otro lado del problema: una versión vieja leyendo un archivo nuevo. Eso ya andaba
        // —System.Text.Json ignora lo que no conoce— y esta prueba lo deja fijado, porque es la
        // mitad de «se puede ir y volver entre versiones».
        Escribir(Completo.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"algoQueNoExisteTodavia\": { \"a\": 1 },",
            StringComparison.Ordinal));

        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.True(leido.LoadedFromDisk);
        Assert.Null(leido.ErrorMessage);
        Assert.Equal("Ana Maria", leido.Settings.AssistantName);
        Assert.Equal("Nube", leido.Settings.OrbShape);
    }

    [Fact]
    public async Task SinArchivoNoSeInventaNingunaCopia()
    {
        Directory.CreateDirectory(_carpeta);
        var leido = await new LocalSettingsStore(_carpeta).LoadAsync();

        Assert.False(leido.LoadedFromDisk);
        Assert.Null(leido.ErrorMessage);
        Assert.Empty(Directory.GetFiles(_carpeta, "settings.json.roto-*"));
    }
}
