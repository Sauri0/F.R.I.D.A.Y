using System.Diagnostics;
using System.Text;
using Viernes.Core.Configuration;
using Xunit;

namespace Viernes.Core.Tests;

/// <summary>
/// Que el instalador y la aplicación juzguen los nombres igual.
/// </summary>
/// <remarks>
/// Las reglas del nombre están escritas dos veces —en <see cref="AssistantIdentity"/> y en
/// <c>instalador/instalar.ps1</c>— y no se pueden compartir: el instalador corre <b>antes</b> de que
/// la aplicación exista en el disco. Lo único que puede mantenerlas juntas es esto.
/// <para>
/// Si se separan, el síntoma no es un error: es que el instalador acepta un nombre y lo guarda con
/// una forma, y la aplicación lo normaliza a otra. El usuario escribe «JARVIS», el instalador dice
/// «listo, JARVIS», y el asistente se presenta como «Jarvis». Eso pasó de verdad: el instalador usaba
/// <c>ToTitleCase</c>, que baja todo a minúscula antes de subir la primera letra de cada palabra.
/// </para>
/// <para>
/// La prueba corre el archivo real con <c>-ProbarNombre</c>, que existe sólo para esto y no instala
/// nada. Si no hay PowerShell en la máquina, se saltea en vez de fallar: no todo el que compila esto
/// tiene por qué tener Windows.
/// </para>
/// </remarks>
public sealed class ReglasDelNombreTests
{
    /// <summary>Los casos que separaron a las dos implementaciones alguna vez, y los bordes.</summary>
    public static TheoryData<string> Nombres =>
    [
        "ana",
        "Ana",
        "Ana Maria",
        "ana   maria",       // los espacios internos se colapsan
        "JARVIS",            // las mayúsculas elegidas se respetan
        "McCoy",             // ídem, en el medio de la palabra
        "O'Brien",           // apóstrofo recto
        "O’Brien",           // apóstrofo tipográfico, que es el que pone Word
        "Jean-Luc",          // guión
        "Renée",             // acento
        "a",                 // corto de más
        "Ana2",              // número
        "R2D2",
        "123",               // sin letras
        "Ana!",              // símbolo
        "Un nombre demasiado largo para esto",
    ];

    [Theory]
    [MemberData(nameof(Nombres))]
    public void ElInstaladorYLaAplicacionJuzganIgual(string nombre)
    {
        var contestado = PreguntarleAlInstalador(nombre);
        if (contestado is null)
        {
            return;
        }

        var aceptaLaAplicacion = AssistantIdentity.TryValidate(nombre, out var problema);
        var (aceptaElInstalador, resto) = contestado.Value;

        Assert.True(
            aceptaLaAplicacion == aceptaElInstalador,
            $"«{nombre}»: la aplicación dice {aceptaLaAplicacion} y el instalador {aceptaElInstalador}.");

        if (!aceptaLaAplicacion)
        {
            // El motivo también, porque es lo que el usuario lee. Dos mensajes distintos para el
            // mismo rechazo son dos explicaciones distintas de la misma regla.
            Assert.True(
                string.Equals(problema, resto, StringComparison.Ordinal),
                $"«{nombre}»: la aplicación dice «{problema}» y el instalador «{resto}».");
            return;
        }

        Assert.True(
            string.Equals(AssistantIdentity.Normalize(nombre), resto, StringComparison.Ordinal),
            $"«{nombre}»: la aplicación lo guarda como «{AssistantIdentity.Normalize(nombre)}» " +
            $"y el instalador como «{resto}».");
    }

    /// <summary>
    /// Corre el instalador real y devuelve su veredicto, o <c>null</c> si acá no se puede correr.
    /// </summary>
    private static (bool Acepta, string Resto)? PreguntarleAlInstalador(string nombre)
    {
        var guion = UbicarInstalador();
        if (guion is null)
        {
            return null;
        }

        foreach (var interprete in new[] { "pwsh", "powershell" })
        {
            var salida = Correr(interprete, guion, nombre);
            if (salida is null)
            {
                continue;
            }

            // El formato es «ok<tab>Forma final» o «no<tab>El motivo.».
            var partes = salida.Split('\t', 2);
            if (partes.Length != 2)
            {
                continue;
            }

            return (partes[0] == "ok", partes[1].Trim());
        }

        return null;
    }

    private static string? Correr(string interprete, string guion, string nombre)
    {
        try
        {
            var arranque = new ProcessStartInfo(interprete)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            arranque.ArgumentList.Add("-NoProfile");
            arranque.ArgumentList.Add("-ExecutionPolicy");
            arranque.ArgumentList.Add("Bypass");

            // Con -Command y no con -File, y la codificación de salida forzada a UTF-8.
            //
            // Windows PowerShell 5.1 escribe a la salida estándar con la página de códigos OEM de la
            // consola: «números» volvía como «n£meros» y la comparación fallaba con las dos partes
            // diciendo lo mismo. Esto no es un problema del instalador —en pantalla se ve bien— sino
            // de leerlo desde otro proceso, así que se arregla acá y no allá.
            arranque.ArgumentList.Add("-Command");
            arranque.ArgumentList.Add(
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '" +
                guion.Replace("'", "''") + "' -ProbarNombre '" + nombre.Replace("'", "''") + "'");

            using var proceso = Process.Start(arranque);
            if (proceso is null)
            {
                return null;
            }

            var salida = proceso.StandardOutput.ReadToEnd();
            proceso.WaitForExit(30_000);
            return string.IsNullOrWhiteSpace(salida) ? null : salida.Trim();
        }
        catch (Exception)
        {
            // No hay ese intérprete en esta máquina: se prueba con el otro.
            return null;
        }
    }

    /// <summary>Sube desde donde corren las pruebas hasta encontrar el instalador.</summary>
    private static string? UbicarInstalador()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null)
        {
            var candidato = Path.Combine(directorio.FullName, "instalador", "instalar.ps1");
            if (File.Exists(candidato))
            {
                return candidato;
            }

            directorio = directorio.Parent;
        }

        return null;
    }

    /// <summary>
    /// El archivo del instalador tiene que empezar con BOM UTF-8, y no es un detalle de formato.
    /// </summary>
    /// <remarks>
    /// <c>INSTALAR.cmd</c> prefiere <c>pwsh</c> pero cae a <c>powershell</c> —Windows PowerShell
    /// 5.1— cuando no está, que es el caso de cualquier Windows recién bajado de fábrica. Y 5.1 lee
    /// un <c>.ps1</c> sin BOM como ANSI: con 165 líneas acentuadas, <c>$tamaño</c> se leía
    /// <c>$tama??o</c> y el archivo <b>ni siquiera parseaba</b>. Lo que veía quien lo bajaba de
    /// GitHub no era un error de instalación: eran veinte errores de sintaxis antes de que empezara
    /// nada.
    /// </remarks>
    [Fact]
    public void ElInstaladorEmpiezaConBom()
    {
        var guion = UbicarInstalador();
        if (guion is null)
        {
            return;
        }

        var primeros = new byte[3];
        using (var archivo = File.OpenRead(guion))
        {
            Assert.Equal(3, archivo.Read(primeros, 0, 3));
        }

        Assert.True(
            primeros is [0xEF, 0xBB, 0xBF],
            $"instalar.ps1 empieza con {primeros[0]:X2} {primeros[1]:X2} {primeros[2]:X2} " +
            "y no con EF BB BF: sin BOM no parsea bajo Windows PowerShell 5.1.");
    }

    /// <summary>
    /// Los nombres que no se pueden pasar por línea de comandos, contra la aplicación sola.
    /// </summary>
    /// <remarks>
    /// El vacío y los espacios se pierden al pasar por el proceso —PowerShell descarta un argumento
    /// vacío antes de que el guion lo vea—, así que la comparación cruzada no los alcanza. Que la
    /// aplicación los rechace igual se comprueba acá.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LoQueNoEsUnNombreNoSeGuarda(string? nombre)
    {
        Assert.False(AssistantIdentity.TryValidate(nombre, out var problema));
        Assert.False(string.IsNullOrWhiteSpace(problema));
        Assert.Equal(AssistantIdentity.DefaultName, AssistantIdentity.Normalize(nombre));
    }
}
