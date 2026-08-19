using System.Runtime.InteropServices;
using System.Text.Json;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Que las credenciales del asistente no crucen hacia un proceso que él mismo lanza.
/// </summary>
/// <remarks>
/// Esta puerta no tenía prueba, y se notó: la línea que limpiaba el entorno borraba
/// <c>OPENROUTER_API_KEY</c> y se quedó ahí. Cuando apareció la sesión hablada y con ella
/// <c>GOOGLE_API_KEY</c>, nadie volvió a esa línea, así que a quien tuviera esa clave en el entorno
/// —el respaldo, para quien prefiere no tener claves en archivos— le viajaba a cada comando.
/// <para>
/// Es una prueba de integración de verdad: lanza PowerShell y le pregunta al proceso hijo qué ve.
/// Comprobarlo de otra manera sería comprobar que la lista tiene dos elementos, que es justo lo que
/// ya era cierto cuando el defecto existía.
/// </para>
/// </remarks>
public sealed class ShellToolCredentialsTests
{
    private static bool EsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Theory]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("GOOGLE_API_KEY")]
    public async Task LaCredencialNoLlegaAlProcesoHijo(string variable)
    {
        if (!EsWindows)
        {
            return;
        }

        var anterior = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "valor-de-prueba-que-no-tiene-que-salir");
        try
        {
            var salida = await CorrerAsync($"Write-Output \"[$env:{variable}]\"");

            // El proceso hijo tiene que ver la variable VACÍA, no ausente-por-casualidad: los
            // corchetes hacen que un valor filtrado se vea, en vez de perderse en una línea en blanco.
            Assert.Contains("[]", salida, StringComparison.Ordinal);
            Assert.DoesNotContain("valor-de-prueba", salida, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, anterior);
        }
    }

    [Fact]
    public async Task LoQueNoEsUnaCredencial_SiCruza()
    {
        if (!EsWindows)
        {
            return;
        }

        // El otro lado, y hace falta: si la limpieza se pasara de rosca y vaciara el entorno entero,
        // la prueba de arriba pasaría igual y los comandos del usuario dejarían de encontrar sus
        // propias herramientas.
        var anterior = Environment.GetEnvironmentVariable("VIERNES_PRUEBA_ENTORNO");
        Environment.SetEnvironmentVariable("VIERNES_PRUEBA_ENTORNO", "esto-si-tiene-que-pasar");
        try
        {
            var salida = await CorrerAsync("Write-Output \"[$env:VIERNES_PRUEBA_ENTORNO]\"");

            Assert.Contains("esto-si-tiene-que-pasar", salida, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIERNES_PRUEBA_ENTORNO", anterior);
        }
    }

    private static async Task<string> CorrerAsync(string comando)
    {
        var argumentos = JsonSerializer.SerializeToElement(new { comando });
        var resultado = await new ShellTool().ExecuteAsync(
            argumentos, new ToolExecutionContext("t1"), CancellationToken.None);

        return resultado.Message;
    }
}
