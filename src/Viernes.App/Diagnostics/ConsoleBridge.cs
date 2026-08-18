using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Engancha la consola desde la que se lanzó Viernes cuando se la invoca en modo diagnóstico.
/// </summary>
/// <remarks>
/// El proyecto es <c>WinExe</c>: Windows no le da consola a un proceso de interfaz gráfica, así que
/// todos los <c>Console.WriteLine</c> de <c>--check-mic</c>, <c>--check-voice</c>,
/// <c>--check-listen</c>, <c>--check-whisper</c> y <c>--render-orb</c> escribían en un descriptor
/// nulo. Desde afuera el comando devolvía el prompt sin decir absolutamente nada, y el informe
/// quedaba en un archivo de <c>%TEMP%</c> que nadie mencionaba. <c>AttachConsole(-1)</c> toma la
/// consola del proceso padre, que es la ventana donde se escribió el comando.
/// <para>
/// Corre como inicializador de módulo, y no desde <c>OnStartup</c>, por una razón concreta: .NET
/// construye <c>Console.Out</c> la primera vez que alguien lo toca y se queda para siempre con el
/// descriptor que había en ese momento. Enganchar la consola después de la primera escritura no
/// sirve de nada. Un inicializador de módulo corre antes que <c>Main</c>, así que no hay forma de
/// llegar tarde.
/// </para>
/// <para>
/// Si no hay consola padre —lanzado desde el explorador o desde un acceso directo— la llamada falla
/// y no se hace nada más: abrir una consola nueva sólo mostraría el informe hasta que el proceso
/// termine, que es medio segundo después.
/// </para>
/// </remarks>
internal static class ConsoleBridge
{
    /// <summary>Identificador convencional del proceso padre para <c>AttachConsole</c>.</summary>
    private const int ParentProcess = -1;

    private const int StandardOutputHandle = -11;

    private static readonly string[] DiagnosticArguments =
    [
        "--check-voice",
        "--check-listen",
        "--check-whisper",
        "--check-mic",
        "--render-orb"
    ];

    [ModuleInitializer]
    internal static void AttachToLaunchingConsole()
    {
        // Sólo en los modos que escriben por consola. Enganchar siempre metería la salida de un
        // arranque normal en la terminal de quien lo lanzó, que no la pidió.
        if (!Environment.GetCommandLineArgs().Any(IsDiagnosticArgument))
        {
            return;
        }

        try
        {
            // Si ya hay dónde escribir, no se toca nada. Cuando alguien redirige la salida
            // —`Viernes.exe --check-mic > informe.txt`— el proceso arranca con un descriptor válido y
            // los Console.WriteLine ya llegan a destino; engancharse a la consola del padre encima de
            // eso reemplazaría los descriptores estándar y rompería la redirección, que es el único
            // camino que hoy funciona.
            if (HasUsableStandardOutput())
            {
                return;
            }

            AttachConsole(ParentProcess);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sin kernel32 no hay nada que hacer, y no poder imprimir un diagnóstico no puede ser
            // el motivo por el que la aplicación no arranca.
        }
    }

    /// <summary>
    /// True cuando el proceso ya nació con una salida estándar utilizable.
    /// </summary>
    /// <remarks>
    /// Un proceso de subsistema GUI que se lanza desde una terminal sin redirección arranca con el
    /// descriptor en cero: ése es exactamente el caso que hay que reparar, y el único.
    /// </remarks>
    private static bool HasUsableStandardOutput()
    {
        var handle = GetStdHandle(StandardOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    private static bool IsDiagnosticArgument(string argument) =>
        DiagnosticArguments.Contains(argument, StringComparer.OrdinalIgnoreCase);

    // DllImport y no LibraryImport, igual que en el resto del repo.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
}
