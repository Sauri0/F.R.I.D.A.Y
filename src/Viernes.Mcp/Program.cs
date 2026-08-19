using ModelContextProtocol.Server;
using Viernes.Core.Autonomy;
using Viernes.Core.Missions;
using Viernes.Core.Projects;
using Viernes.Core.Usage;
using Viernes.Memory.Persistence;

namespace Viernes.Mcp;

/// <summary>
/// El conector: un proceso que habla MCP por entrada y salida estándar.
/// </summary>
/// <remarks>
/// Es el único lugar donde se eligen las rutas de verdad. Cada pieza de Viernes ya sabe dónde vive
/// su archivo dentro de <c>%LOCALAPPDATA%\Viernes</c>, así que acá se las construye sin ruta: repetir
/// «misiones.json» de este lado sería la segunda copia de una constante, y en este proyecto eso ya
/// hizo que un banco de medición informara semanas enteras contra la copia vieja.
/// <para>
/// Nada, nunca, se escribe en la salida estándar salvo el protocolo: del otro lado hay un cliente
/// MCP leyendo JSON línea por línea, y un solo <c>Console.WriteLine</c> suelto le rompe la sesión.
/// Los avisos van a la salida de error.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();

        void Stop(object? sender, ConsoleCancelEventArgs args)
        {
            // Cancelar y no matar: así se cierra el transporte y el cliente ve un cierre limpio en
            // vez de una tubería rota.
            args.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += Stop;
        try
        {
            var options = ConnectorServer.CreateOptions(BuildConnector());

            await using var transport = new StdioServerTransport(options);
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"El conector de Viernes se cayó: {exception}")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            // Desuscribir siempre. Dejar un manejador vivo colgado de un evento estático apuntando a
            // algo ya liberado es de los errores que este proyecto ya pagó una vez.
            Console.CancelKeyPress -= Stop;
        }
    }

    /// <summary>Arma el conector contra los archivos reales del usuario.</summary>
    private static ViernesConnector BuildConnector()
    {
        var sessions = new ClaudeSessionWatcher();

        return new ViernesConnector(
            // Ojo con éste: MissionBook lee el archivo una sola vez y después devuelve su copia en
            // memoria, sin invalidar nunca. Acá no molesta —el conector es un proceso corto que
            // arranca, contesta y se muere—, pero sí molesta del otro lado: con Viernes abierto, el
            // orbe no ve lo que escribe el conector hasta reiniciar, y si el orbe guarda después,
            // pisa el archivo con su copia vieja. Está avisado en las instrucciones del servidor y
            // en docs/CONECTOR.md. Arreglarlo es tocar MissionBook, que no es de este frente.
            new MissionBook(),
            new JsonPersonalMemoryStore(),
            sessions,
            new ClaudeSessionWriter(sessions),
            // Los presupuestos van vacíos porque el conector no gasta: sólo lee cuánto se gastó, y
            // los límites únicamente intervienen al pedir permiso para una llamada al modelo, cosa
            // que acá no pasa nunca. La ruta va sin especificar para que el libro mayor use la misma
            // que la aplicación.
            new UsageLedger(new UsageBudgetConfiguration()),
            new ConnectorBoundary(new AutonomyPolicy()));
    }
}
