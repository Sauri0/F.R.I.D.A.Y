using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Viernes.App.Services;

/// <summary>
/// Escribe archivos chicos de estado sin hacer esperar a quien los pide.
/// </summary>
/// <remarks>
/// Existe por una medición. La posición del orbe y la memoria por monitor se guardaban con
/// <c>File.WriteAllText</c> sincrónico desde <c>MainWindow.UpdateResting</c>, que corre <b>dentro
/// del bucle de cuadro</b>. Y no una vez por soltada: el resorte de reposo es subamortiguado, así
/// que «está quieto» se enciende, se apaga mientras el imán lo acomoda y se vuelve a encender —dos
/// flancos, dos archivos cada uno: <b>cuatro archivos abiertos, escritos y cerrados por cada vez que
/// soltás el orbe</b>—.
/// <para>
/// Medido en esta máquina contra <c>%LOCALAPPDATA%\Viernes</c>, 300 repeticiones del par: mediana
/// 0,98 ms, p90 2,17 ms, <b>p99 39,9 ms y peor caso 50 ms</b>. El perfil es intermitente —el
/// antivirus mira cada escritura—: casi siempre gratis, y una de cada veinte se come entre cinco y
/// nueve cuadros seguidos. Mientras dura, <c>CompositionTarget.Rendering</c> no corre y la ventana
/// no se mueve un píxel. Eso es, palabra por palabra, «se queda pegado un mínimo instante» y
/// «a veces».
/// </para>
/// <para>
/// Acá el que pide una escritura deja el contenido y sigue. Un hilo de fondo las baja al disco.
/// <b>Se guarda por ruta y gana el último</b>: si llegan tres estados del mismo archivo antes de que
/// el hilo despierte, se escribe uno solo, el más nuevo, que es el único que alguien va a leer.
/// </para>
/// </remarks>
internal static class DeferredFile
{
    private static readonly ConcurrentDictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AutoResetEvent Ring = new(false);
    private static readonly ConcurrentDictionary<string, bool> DirectoriesMade = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<Thread> Writer = new(
        () =>
        {
            var thread = new Thread(Drain)
            {
                IsBackground = true,
                Name = "Viernes.DeferredFile",
                Priority = ThreadPriority.BelowNormal,
            };

            thread.Start();
            return thread;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Deja pedido el contenido de un archivo y vuelve enseguida.</summary>
    public static void Write(string path, string contents)
    {
        try
        {
            Pending[path] = contents;
            _ = Writer.Value;
            Ring.Set();
        }
        catch (Exception)
        {
            // Guardar la posición es comodidad: que falle no puede tumbar al asistente.
        }
    }

    /// <summary>
    /// Espera a que no quede nada pendiente, con un tope. Para el cierre ordenado.
    /// </summary>
    /// <remarks>
    /// Con tope porque nadie puede quedarse esperando al disco para cerrar la aplicación. Si no
    /// llega, se pierde la última posición del orbe: es exactamente el precio que corresponde pagar.
    /// </remarks>
    public static void Flush(TimeSpan timeout)
    {
        try
        {
            if (!Writer.IsValueCreated)
            {
                return;
            }

            var limit = DateTime.UtcNow + timeout;
            while (!Pending.IsEmpty && DateTime.UtcNow < limit)
            {
                Ring.Set();
                Thread.Sleep(5);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void Drain()
    {
        while (true)
        {
            Ring.WaitOne();

            foreach (var path in Pending.Keys)
            {
                if (!Pending.TryRemove(path, out var contents))
                {
                    continue;
                }

                try
                {
                    // Una vez por ruta y no por escritura: crear un directorio que ya existe sigue
                    // siendo una llamada al sistema de archivos, y acá se hacía cuatro veces por
                    // soltada para enterarse siempre de lo mismo.
                    if (DirectoriesMade.TryAdd(path, true))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    }

                    File.WriteAllText(path, contents);
                }
                catch (Exception)
                {
                    // Si el disco no quiere, el asistente sigue andando sin memoria de posición.
                }
            }
        }
    }
}
