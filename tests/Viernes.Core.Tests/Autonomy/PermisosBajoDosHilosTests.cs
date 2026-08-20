using Viernes.Core.Autonomy;
using Xunit;

namespace Viernes.Core.Tests.Autonomy;

/// <summary>
/// Leer los permisos mientras se aprende uno no puede tirar.
/// </summary>
/// <remarks>
/// <b>Este defecto lo descartó un escéptico porque «no reproducía», y una carrera que no reproduce
/// sigue siendo una carrera.</b> La carga devolvía la lista cacheada —no una copia— y quien la
/// recibía la recorría ya afuera del candado, sobre la misma colección que aprender modifica en el
/// lugar. Recorrer una colección de .NET mientras otro hilo le agrega o le saca elementos termina en
/// excepción.
/// <para>
/// Dejó de ser teórico cuando el mismo libro pasó a compartirse entre tres llamadores en hilos
/// distintos: la herramienta que enseña un permiso, el turno escrito que los lee en cada pedido, y
/// el armado de la instrucción de la sesión hablada.
/// </para>
/// <para>
/// La prueba martilla a propósito, y está medida en las dos direcciones: revirtiendo el arreglo
/// falla 2 de cada 3 corridas; con el arreglo puesto, 5 de 5 en verde. Esa intermitencia ES el
/// defecto — es exactamente por lo que un escéptico que lo intentó una vez concluyó que no existía.
/// </para>
/// </remarks>
public sealed class PermisosBajoDosHilosTests : IDisposable
{
    private readonly string _archivo = Path.Combine(
        Path.GetTempPath(),
        "viernes-permisos-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try
        {
            File.Delete(_archivo);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            // Limpiar de más no importa.
        }
    }

    [Fact]
    public async Task LeerYAprenderALaVez_NoTira()
    {
        var permisos = new AutonomyPolicy(_archivo);
        using var plazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await permisos.LearnAsync("mandar", "correo", AutonomyLevel.Automatico).ConfigureAwait(false);

        var aprendiendo = Task.Run(
            async () =>
            {
                for (var i = 0; i < 400 && !plazo.IsCancellationRequested; i++)
                {
                    await permisos.LearnAsync($"accion{i % 20}", $"cosa{i}", AutonomyLevel.Automatico).ConfigureAwait(false);
                }
            },
            plazo.Token);

        var leyendo = Task.Run(
            async () =>
            {
                for (var i = 0; i < 400 && !plazo.IsCancellationRequested; i++)
                {
                    await permisos.ListAsync().ConfigureAwait(false);
                    await permisos.DecideAsync("mandar", "correo").ConfigureAwait(false);
                    await permisos.DescribeAsync().ConfigureAwait(false);
                }
            },
            plazo.Token);

        // Si alguna de las dos tira, esto la propaga y la prueba cae con el motivo real.
        await Task.WhenAll(aprendiendo, leyendo).ConfigureAwait(false);

        Assert.Equal(AutonomyLevel.Automatico, await permisos.DecideAsync("mandar", "correo").ConfigureAwait(false));
    }
}
