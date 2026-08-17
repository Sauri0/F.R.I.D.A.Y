using System.Diagnostics;
using Viernes.Core.Awareness;

namespace Viernes.Platform.Windows.Awareness;

/// <summary>
/// Mira el escritorio cada tanto y produce candidatos a aviso. Decidir si se dicen es de otro.
/// </summary>
/// <remarks>
/// La lista es corta a propósito. Prefiero tres señales que Windows realmente entrega y que se
/// pueden verificar, antes que diez que suenan bien en un diagrama y en la práctica producen falsos
/// positivos: un asistente que avisa cosas que no son termina enseñándote a ignorarlo.
/// </remarks>
public sealed class DesktopSignals : IDisposable
{
    /// <summary>Umbral de memoria comprometida. Por debajo de esto Windows todavía respira.</summary>
    private const double HighMemoryFraction = 0.88;

    private readonly Timer _timer;
    private readonly SalienceGate _gate;
    private readonly Action<Observation> _announce;
    private readonly Func<TimeSpan> _idleTime;
    private readonly Func<string> _foregroundTitle;
    private string? _watchedWindow;
    private DateTimeOffset _watchedSince;

    public DesktopSignals(
        SalienceGate gate,
        Func<TimeSpan> idleTime,
        Func<string> foregroundTitle,
        Action<Observation> announce)
    {
        _gate = gate;
        _idleTime = idleTime;
        _foregroundTitle = foregroundTitle;
        _announce = announce;

        // Cada treinta segundos: lo bastante seguido para enterarse de algo que importa, lo bastante
        // espaciado para no ser un proceso que se hace notar en el administrador de tareas.
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30));
    }

    private void Sample()
    {
        try
        {
            var idle = _idleTime();
            var candidates = new List<Observation>();

            AddMemoryPressure(candidates);
            AddLostWindow(candidates);

            var chosen = _gate.Choose(candidates, userIsBusy: idle < TimeSpan.FromSeconds(8));
            if (chosen is not null)
            {
                _announce(chosen);
            }
        }
        catch (Exception)
        {
            // Observar de fondo nunca puede tumbar el asistente ni hacerse notar por fallar.
        }
    }

    /// <summary>
    /// Memoria al límite. Es accionable —se puede cerrar algo— y verificable, que es lo que la hace
    /// digna de interrumpir; «la CPU está al 80 %» casi nunca lo es, porque puede ser exactamente lo
    /// que pediste que pase.
    /// </summary>
    private static void AddMemoryPressure(List<Observation> candidates)
    {
        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0)
        {
            return;
        }

        var used = 0L;
        Process? heaviest = null;
        foreach (var process in Process.GetProcesses())
        {
            used += process.WorkingSet64;
            if (heaviest is null || process.WorkingSet64 > heaviest.WorkingSet64)
            {
                heaviest?.Dispose();
                heaviest = process;
                continue;
            }

            process.Dispose();
        }

        var fraction = (double)used / info.TotalAvailableMemoryBytes;
        if (fraction >= HighMemoryFraction && heaviest is not null)
        {
            candidates.Add(new Observation(
                "memoria-alta",
                $"La memoria está casi llena. El que más ocupa es {heaviest.ProcessName}.",
                Importance: 0.7,
                Urgency: 0.6,
                Confidence: 0.9,
                Actionability: 0.9));
        }

        heaviest?.Dispose();
    }

    /// <summary>
    /// La ventana en la que venías trabajando desapareció mientras no la mirabas.
    /// </summary>
    /// <remarks>
    /// Exige que hubieras estado ahí un rato antes de contarla como pérdida: si cerraste algo vos,
    /// no es noticia, y si estuviste treinta segundos en una ventana probablemente la abriste sin
    /// querer. Cinco minutos es el punto donde «se cerró» empieza a significar algo.
    /// </remarks>
    private void AddLostWindow(List<Observation> candidates)
    {
        var current = _foregroundTitle();
        if (!string.IsNullOrWhiteSpace(current))
        {
            if (!string.Equals(current, _watchedWindow, StringComparison.Ordinal))
            {
                _watchedWindow = current;
                _watchedSince = DateTimeOffset.UtcNow;
                _gate.Forget("ventana-perdida");
            }

            return;
        }

        if (_watchedWindow is null || DateTimeOffset.UtcNow - _watchedSince < TimeSpan.FromMinutes(5))
        {
            return;
        }

        candidates.Add(new Observation(
            "ventana-perdida",
            $"Se cerró «{_watchedWindow}», donde venías trabajando.",
            Importance: 0.8,
            Urgency: 0.7,
            Confidence: 0.6,
            Actionability: 0.7));
    }

    public void Dispose() => _timer.Dispose();
}
