using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Viernes.Core.Awareness;

namespace Viernes.Platform.Windows.Awareness;

/// <summary>
/// Observa el escritorio: qué ventana está adelante, hace cuánto que no tocás nada, y por dónde
/// anduviste.
/// </summary>
/// <remarks>
/// Mira por eventos baratos y no por capturas de pantalla. Preguntarle a Windows qué ventana está al
/// frente cuesta microsegundos; sacar una foto y hacérsela interpretar a un modelo cuesta cientos de
/// milisegundos y mil tokens. La visión sirve para entender <em>contenido</em>; para saber
/// <em>dónde estás</em> el sistema ya tiene la respuesta.
/// <para>
/// El historial se arma con lo que se observó al pasar: no hay muestreo en segundo plano, se anota
/// cada vez que alguien pregunta. Alcanza para «seguí con lo de antes» sin dejar un hilo despierto
/// gastando batería para mirar una ventana que casi nunca cambia.
/// </para>
/// </remarks>
public sealed class WindowsEnvironmentObserver : IEnvironmentObserver, IDisposable
{
    private const int MaximumHistory = 12;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private readonly Lock _gate = new();
    private readonly List<(string Title, string Process, DateTimeOffset Seen)> _history = [];
    private readonly Timer _sampler;

    /// <summary>
    /// Muestrea la ventana de adelante cada veinte segundos.
    /// </summary>
    /// <remarks>
    /// Sin esto el historial sólo se llenaba cuando alguien preguntaba, y entonces «¿dónde estaba
    /// trabajando?» respondía con una sola línea de la última vez que se hizo esa misma pregunta.
    /// Un modelo de la situación que se arma recién cuando se lo interroga no es un modelo de la
    /// situación: es una foto del momento de preguntar.
    /// <para>
    /// El costo es una llamada al sistema cada veinte segundos —microsegundos— y no despierta
    /// procesos ni toca disco. Veinte es el punto donde no se pierde en qué anduviste y tampoco se
    /// registra cada vez que pasás por el escritorio para llegar a otra ventana.
    /// </para>
    /// </remarks>
    public WindowsEnvironmentObserver()
    {
        _sampler = new Timer(
            _ =>
            {
                try
                {
                    var (title, process) = ReadForeground();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        Remember(title, process);
                    }
                }
                catch (Exception)
                {
                    // Observar nunca puede tumbar el asistente.
                }
            },
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(20));
    }

    public string? DescribeNow()
    {
        var (title, process) = ReadForeground();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        Remember(title, process);

        var idle = ReadIdleTime();
        var idleText = idle.TotalSeconds switch
        {
            < 5 => "estás usándola ahora",
            < 60 => $"hace {(int)idle.TotalSeconds} segundos que no tocás nada",
            < 3600 => $"hace {(int)idle.TotalMinutes} minutos que no tocás nada",
            _ => "hace más de una hora que no tocás nada"
        };

        return $"Contexto del equipo: tenés adelante «{title}» ({process}); {idleText}. " +
            "Usalo para resolver a qué se refiere el usuario cuando dice «esto», «eso» o «acá», " +
            "sin preguntarle lo que ya se puede deducir.";
    }

    public string DescribeRecentActivity()
    {
        // Se refresca antes de contar: si no, lo último que figura es de la vez anterior que alguien
        // preguntó, y podría ser de hace media hora.
        var (title, process) = ReadForeground();
        if (!string.IsNullOrWhiteSpace(title))
        {
            Remember(title, process);
        }

        lock (_gate)
        {
            if (_history.Count == 0)
            {
                return "Todavía no vi en qué venías trabajando.";
            }

            var builder = new StringBuilder("Por donde anduviste, de lo más reciente a lo más viejo:");
            foreach (var entry in _history.AsEnumerable().Reverse())
            {
                var ago = DateTimeOffset.Now - entry.Seen;
                var when = ago.TotalMinutes < 1
                    ? "recién"
                    : ago.TotalMinutes < 60
                        ? $"hace {(int)ago.TotalMinutes} min"
                        : $"hace {(int)ago.TotalHours} h";
                builder.AppendLine().Append("- ").Append(entry.Title).Append(" (").Append(when).Append(')');
            }

            return builder.ToString();
        }
    }

    public string DescribeSystemLoad()
    {
        try
        {
            var processes = Process.GetProcesses();
            var heaviest = processes
                .Where(process => process.WorkingSet64 > 0)
                .OrderByDescending(process => process.WorkingSet64)
                .Take(5)
                .Select(process =>
                    $"{process.ProcessName} ({Math.Round(process.WorkingSet64 / 1024d / 1024d)} MB)")
                .ToArray();

            var total = processes.Sum(process => process.WorkingSet64) / 1024d / 1024d / 1024d;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return $"Procesos: {processes.Length}. Memoria en uso por procesos: " +
                $"{Math.Round(total, 1)} GB. Los que más ocupan: {string.Join(", ", heaviest)}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "No pude leer el estado del sistema.";
        }
    }

    private void Remember(string title, string process)
    {
        lock (_gate)
        {
            // Volver a la misma ventana no crea una entrada nueva: actualiza cuándo se la vio. Sin
            // esto, alternar entre dos programas llena el historial con la misma línea repetida.
            var existing = _history.FindIndex(entry =>
                string.Equals(entry.Title, title, StringComparison.Ordinal));
            if (existing >= 0)
            {
                _history.RemoveAt(existing);
            }

            _history.Add((title, process, DateTimeOffset.Now));
            if (_history.Count > MaximumHistory)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private static (string Title, string Process) ReadForeground()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero)
        {
            return (string.Empty, string.Empty);
        }

        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return (string.Empty, string.Empty);
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);

        var processName = string.Empty;
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // El proceso puede haber muerto entre leer el identificador y abrirlo.
        }

        return (buffer.ToString(), processName);
    }

    /// <summary>Título de la ventana al frente, crudo. Lo consume el vigilante de señales.</summary>
    public string ForegroundTitle() => ReadForeground().Title;

    /// <summary>Hace cuánto que no hay teclado ni mouse. Es el costo de interrumpir, medido.</summary>
    public TimeSpan IdleTime() => ReadIdleTime();

    public void Dispose() => _sampler.Dispose();

    private static TimeSpan ReadIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var elapsed = (uint)Environment.TickCount - info.Time;
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
