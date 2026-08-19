using System.Globalization;

namespace Viernes.App.Controls;

/// <summary>
/// Dónde está parada la escalera de reintentos mientras el orbe está sordo.
/// </summary>
/// <param name="Attempt">Cuántos reintentos van. Cero es el primer intento, el de entrada.</param>
/// <param name="SinceAttempt">Segundos desde que salió a buscar por última vez.</param>
/// <param name="ToNext">Segundos hasta el próximo intento. Cero cuando ya no quedan.</param>
internal readonly record struct OrbRetry(int Attempt, double SinceAttempt, double ToNext);

/// <summary>
/// La parte visual de estar sorda reintentando: la escalera de intensidad y la cuenta regresiva.
/// </summary>
/// <remarks>
/// <b>El reintento en sí no es de acá.</b> Volver a abrir el micrófono lo hace el runtime; esto sólo
/// dibuja el intento y cuenta el próximo. Si el runtime avisa en qué intento va
/// —<see cref="OrbStateChannel.ReportRetry"/>— la escalera lo sigue; si no avisa nada, se cuenta
/// desde que entró en sorda, que es lo que hace el boceto.
/// <para>
/// Cada intento sale más débil que el anterior, por 0,78. Es la diferencia entre un asistente que
/// insiste y uno que se va apagando: los cuatro estirones son el mismo gesto, cada vez con menos
/// convicción, y al cuarto ya casi no se ve. Eso es honesto —después de 76 segundos sin señal, no
/// va a entrar— y es mucho mejor que un ícono de error que no dice cuánto falta.
/// </para>
/// </remarks>
internal static class OrbDeafRetry
{
    /// <summary>Los cuatro intervalos entre intentos, en segundos.</summary>
    internal static readonly double[] Ladder = [3, 8, 20, 45];

    /// <summary>Cuánto dura el estirón de cada intento.</summary>
    private const double ReachSeconds = 0.9;

    /// <summary>Cuánto se debilita cada intento respecto del anterior.</summary>
    private const double Fade = 0.78;

    /// <summary>Amplitud del estirón en la nube.</summary>
    private const double CloudReach = 0.20;

    /// <summary>Y en la gota, que lo aplica sobre el radio del cuerpo entero.</summary>
    private const double DropReach = 0.16;

    /// <summary>Resuelve la escalera a partir de cuánto lleva sorda.</summary>
    internal static OrbRetry Resolve(double elapsed)
    {
        var accumulated = 0.0;
        var attempt = 0;
        foreach (var gap in Ladder)
        {
            if (elapsed > accumulated + gap)
            {
                accumulated += gap;
                attempt++;
            }
            else
            {
                break;
            }
        }

        var toNext = attempt < Ladder.Length
            ? Math.Max(0, accumulated + Ladder[attempt] - elapsed)
            : 0;

        return new OrbRetry(attempt, Math.Max(0, elapsed - accumulated), toNext);
    }

    /// <summary>La escalera cuando el runtime dice en qué intento va y cuándo lo hizo.</summary>
    internal static OrbRetry Resolve(int attempt, double sinceAttempt)
    {
        var clamped = Math.Clamp(attempt, 0, Ladder.Length);
        var toNext = clamped < Ladder.Length
            ? Math.Max(0, Ladder[clamped] - sinceAttempt)
            : 0;

        return new OrbRetry(clamped, Math.Max(0, sinceAttempt), toNext);
    }

    /// <summary>Cuánto se estira la nube ahora mismo.</summary>
    internal static double CloudStretch(OrbRetry retry) => Stretch(retry, CloudReach);

    /// <summary>Cuánto se estira la gota ahora mismo.</summary>
    internal static double DropStretch(OrbRetry retry) => Stretch(retry, DropReach);

    /// <summary>Lo que dice la píldora al costado del nombre del estado.</summary>
    /// <remarks>
    /// Contar el próximo es lo que convierte «no te oigo» en una promesa en vez de una queja. Cuando
    /// ya no quedan intentos deja de contar y dice «reintentando» a secas: mentir con un cero que no
    /// va a llegar a nada sería peor que no decir nada.
    /// </remarks>
    internal static string Caption(OrbRetry retry) => retry.ToNext > 0
        ? "reintento en 0:" + Math.Ceiling(retry.ToNext).ToString("00", CultureInfo.InvariantCulture)
        : "reintentando";

    private static double Stretch(OrbRetry retry, double amplitude) =>
        retry.SinceAttempt < ReachSeconds
            ? amplitude * Math.Pow(Fade, retry.Attempt) * Math.Sin(Math.PI * retry.SinceAttempt / ReachSeconds)
            : 0;
}
