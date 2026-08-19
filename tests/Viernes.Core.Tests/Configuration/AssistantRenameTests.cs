using Viernes.Core.Configuration;
using Viernes.Platform.Windows.Storage;
using Xunit;

namespace Viernes.Core.Tests.Configuration;

/// <summary>
/// Renombrar al asistente tiene que cambiar con qué se lo despierta, y no tiene que poder romperlo.
/// </summary>
/// <remarks>
/// El nombre se puede cambiar desde las opciones, así que ya no llega una sola vez desde el
/// instalador: llega cada vez que alguien quiera, con la aplicación andando. Lo que se prueba acá es
/// la parte que no necesita interfaz —qué queda guardado y qué frases salen de eso—; que el oído se
/// rearme con las frases nuevas necesita micrófono y no se prueba desde acá.
/// </remarks>
public sealed class AssistantRenameTests
{
    [Fact]
    public void CambiarElNombreCambiaLasFrasesDeActivacion()
    {
        var antes = new ViernesLocalSettings { AssistantName = "Viernes" };
        var despues = antes with { AssistantName = "Ana" };

        Assert.Contains("Hola Viernes", antes.EffectiveWakePhrases);
        Assert.Contains("Hola Ana", despues.EffectiveWakePhrases);
        Assert.DoesNotContain("Hola Viernes", despues.EffectiveWakePhrases);
    }

    /// <remarks>
    /// Las frases escritas a mano ganan sobre el nombre a propósito. La consecuencia es que
    /// renombrar no cambia con qué se lo llama, y por eso la ventana del nombre lo avisa en vez de
    /// dejar al usuario probando un nombre que no despierta nada.
    /// </remarks>
    [Fact]
    public void LasFrasesEscritasAManoNoSiguenAlNombre()
    {
        var settings = new ViernesLocalSettings
        {
            AssistantName = "Ana",
            WakeWordPhrases = ["Hola Viernes"]
        };

        Assert.Equal(["Hola Viernes"], settings.EffectiveWakePhrases);
    }

    [Theory]
    [InlineData("R2D2")]
    [InlineData("A")]
    [InlineData("Ana<script>")]
    [InlineData("")]
    public async Task UnNombreQueNoSirveNoQuedaGuardado(string propuesto)
    {
        using var carpeta = new CarpetaTemporal();
        var store = new LocalSettingsStore(carpeta.Path);

        await store.SaveAsync(new ViernesLocalSettings { AssistantName = propuesto });
        var leido = await store.LoadAsync();

        Assert.Equal(AssistantIdentity.DefaultName, leido.Settings.AssistantName);
        Assert.Contains("Hola Viernes", leido.Settings.EffectiveWakePhrases);
    }

    /// <remarks>
    /// El instalador escribe el mismo archivo con su propia copia de la normalización —está en
    /// PowerShell y no puede llamar a este código—, así que las dos tienen que coincidir. Si esta
    /// prueba cambia, hay que cambiar <c>Normalizar-Nombre</c> en <c>instalador/instalar.ps1</c>.
    /// </remarks>
    [Theory]
    [InlineData("ana maria", "Ana Maria")]
    [InlineData("  ana  ", "Ana")]
    [InlineData("Ana   María", "Ana María")]
    [InlineData("jean-luc", "Jean-Luc")]
    [InlineData("JARVIS", "JARVIS")]
    public async Task ElNombreSeGuardaNormalizadoIgualQueEnElInstalador(string crudo, string esperado)
    {
        using var carpeta = new CarpetaTemporal();
        var store = new LocalSettingsStore(carpeta.Path);

        await store.SaveAsync(new ViernesLocalSettings { AssistantName = crudo });
        var leido = await store.LoadAsync();

        Assert.Equal(esperado, leido.Settings.AssistantName);
        Assert.Equal(AssistantIdentity.Normalize(crudo), leido.Settings.AssistantName);
    }

    /// <remarks>
    /// La carpeta de datos identifica al producto, no al asistente: si siguiera al nombre, cambiarlo
    /// abandonaría el historial y los 465 MB del modelo de voz ya bajado.
    /// </remarks>
    [Fact]
    public async Task RenombrarNoMueveLaCarpetaDeDatos()
    {
        using var carpeta = new CarpetaTemporal();
        var store = new LocalSettingsStore(carpeta.Path);
        var antes = store.SettingsFilePath;

        await store.SaveAsync(new ViernesLocalSettings { AssistantName = "Ana" });

        Assert.Equal(antes, store.SettingsFilePath);
        Assert.True(File.Exists(antes));
    }

    private sealed class CarpetaTemporal : IDisposable
    {
        public CarpetaTemporal()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"viernes-nombre-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
