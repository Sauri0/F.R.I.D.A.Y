using System.Text.Json;
using Viernes.Core.Configuration;
using Xunit;

namespace Viernes.Core.Tests;

/// <summary>
/// Que guardar una clave no se lleve puesto el resto del archivo, y que borrar sea borrar.
/// </summary>
/// <remarks>
/// El archivo de claves no tiene sólo claves: trae además notas para el usuario —las propiedades que
/// empiezan con guión bajo— que explican qué pegar y dónde. El caché de <see cref="LocalCredentials"/>
/// las descarta a propósito, así que escribir el archivo desde el caché las borraría, y quien abriera
/// <c>claves.json</c> después se encontraría un archivo mudo.
/// <para>
/// Estas pruebas corren contra una carpeta propia y no contra la del usuario: <c>LocalCredentials</c>
/// resuelve su ruta desde <c>%LOCALAPPDATA%</c> una sola vez, así que se la cambia por el entorno
/// antes de que la clase se toque por primera vez. Si otra prueba ya la usó, esta clase se saltea en
/// vez de escribir en el archivo real de quien está compilando.
/// </para>
/// </remarks>
public sealed class ClavesTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-claves-" + Guid.NewGuid().ToString("N")[..8]);

    public ClavesTests()
    {
        Directory.CreateDirectory(_carpeta);
        LocalCredentials.DirectoryOverride = _carpeta;
        LocalCredentials.Reload();

        // Que la redirección haya funcionado se comprueba acá y no se supone. La primera versión de
        // esta clase redirigía con la variable LOCALAPPDATA, que GetFolderPath no mira: las ocho
        // pruebas se salteaban solas y pasaban en verde. Se descubrió rompiendo una aserción a
        // propósito y viendo que seguía verde.
        Assert.StartsWith(_carpeta, LocalCredentials.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        LocalCredentials.DirectoryOverride = null;
        LocalCredentials.Reload();
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

    private void Escribir(string contenido)
    {
        File.WriteAllText(LocalCredentials.FilePath, contenido);
        LocalCredentials.Reload();
    }

    private static string LeerCrudo() => File.ReadAllText(LocalCredentials.FilePath);

    [Fact]
    public void GuardarUnaClaveNoBorraLaOtraNiLasNotas()
    {
        Escribir("""
            {
              "_ayuda": "Pegá tu clave de Google acá abajo.",
              "_donde": "aistudio.google.com/apikey",
              "GOOGLE_API_KEY": "la-vieja"
            }
            """);

        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", "la-nueva"));

        using var documento = JsonDocument.Parse(LeerCrudo());
        var raiz = documento.RootElement;

        Assert.Equal("la-nueva", raiz.GetProperty("GOOGLE_API_KEY").GetString());

        // Las notas sobreviven. Son la única explicación que el archivo trae de sí mismo.
        Assert.Equal("Pegá tu clave de Google acá abajo.", raiz.GetProperty("_ayuda").GetString());
        Assert.Equal("aistudio.google.com/apikey", raiz.GetProperty("_donde").GetString());
    }

    [Fact]
    public void GuardarUnaNoPisaLaDeAlLado()
    {
        Escribir("""{ "GOOGLE_API_KEY": "google", "OTRA_COSA": "no la toques" }""");
        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", "google-2"));

        using var documento = JsonDocument.Parse(LeerCrudo());
        Assert.Equal("google-2", documento.RootElement.GetProperty("GOOGLE_API_KEY").GetString());
        Assert.Equal("no la toques", documento.RootElement.GetProperty("OTRA_COSA").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GuardarVacioBorraLaClaveEnVezDeGuardarLaNada(string? vacio)
    {
        Escribir("""{ "_ayuda": "una nota", "GOOGLE_API_KEY": "la-vieja" }""");
        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", vacio));

        using var documento = JsonDocument.Parse(LeerCrudo());

        // Que la propiedad no exista, y no que exista valiendo "". «No tengo clave» y «tengo una
        // clave que es la nada» son estados distintos y el segundo no sirve para nada.
        Assert.False(documento.RootElement.TryGetProperty("GOOGLE_API_KEY", out _));
        Assert.Equal("una nota", documento.RootElement.GetProperty("_ayuda").GetString());

        // Con el entorno vacío, sacarla del archivo alcanza para que no esté.
        //
        // El aislamiento es la mitad del hallazgo: Get cae al entorno como respaldo, y esta máquina
        // tiene GOOGLE_API_KEY puesta ahí. Sin aislarlo, esta prueba medía la máquina de quien
        // compila en vez de medir el código. Lo que pasa cuando el entorno SÍ la tiene está abajo.
        SinLaDelEntorno(() => Assert.False(LocalCredentials.Has("GOOGLE_API_KEY")));
    }

    /// <summary>
    /// Sacarla del archivo no alcanza si el entorno tiene una: el respaldo la resucita.
    /// </summary>
    /// <remarks>
    /// Esto es lo que hacía que el botón «Borrar la de Google» no borrara nada en una máquina con la
    /// clave también en el entorno —la de quien usa esto, sin ir más lejos—. El arreglo vive en la
    /// capa de arriba, que además la saca del entorno; se fija acá para que se entienda por qué hace
    /// falta que la saque de dos lados.
    /// </remarks>
    [Fact]
    public void SacarlaDelArchivoNoAlcanzaSiElEntornoTieneOtra()
    {
        Escribir(UnaClaveDeArchivo);

        var anterior = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "la-del-entorno");
            LocalCredentials.Reload();

            Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", null));

            // Sigue estando, y ahora viene del entorno.
            Assert.True(LocalCredentials.Has("GOOGLE_API_KEY"));
            Assert.Equal("la-del-entorno", LocalCredentials.Get("GOOGLE_API_KEY"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", anterior);
            LocalCredentials.Reload();
        }
    }

    private const string UnaClaveDeArchivo = "{ \"GOOGLE_API_KEY\": \"la-del-archivo\" }";

    /// <summary>Corre algo con la credencial fuera del entorno de este proceso, y la repone.</summary>
    private static void SinLaDelEntorno(Action comprobar)
    {
        var anterior = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", null);
            LocalCredentials.Reload();
            comprobar();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", anterior);
            LocalCredentials.Reload();
        }
    }

    [Fact]
    public void SinArchivoPreviosSeCreaUnoConLaClaveSola()
    {
        if (File.Exists(LocalCredentials.FilePath))
        {
            File.Delete(LocalCredentials.FilePath);
        }

        LocalCredentials.Reload();
        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", "recien-puesta"));
        Assert.True(LocalCredentials.Has("GOOGLE_API_KEY"));
    }

    [Fact]
    public void GuardarDejaElArchivoLegibleYNoATrozos()
    {
        Escribir("""{ "GOOGLE_API_KEY": "la-vieja" }""");
        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", "la-nueva"));

        // No quedan restos del archivo temporal con el que se escribe.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(LocalCredentials.FilePath)!, "*.nuevo"));

        // Y se relee solo: quien acaba de guardar no tiene que acordarse de recargar.
        Assert.Equal("la-nueva", LocalCredentials.Get("GOOGLE_API_KEY"));
    }

    [Fact]
    public void UnArchivoRotoNoImpideGuardar()
    {
        // Lo que había no se puede interpretar, así que no se puede conservar. Lo que no puede pasar
        // es que guardar falle y el usuario se quede sin poder poner la clave por la interfaz.
        Escribir("{ esto no es json");
        Assert.Null(LocalCredentials.Set("GOOGLE_API_KEY", "la-nueva"));
        Assert.Equal("la-nueva", LocalCredentials.Get("GOOGLE_API_KEY"));
    }
}
