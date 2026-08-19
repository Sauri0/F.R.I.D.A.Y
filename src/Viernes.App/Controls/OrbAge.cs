using System.Globalization;

namespace Viernes.App.Controls;

/// <summary>
/// Esperándote envejece: a los tres días la pregunta sin contestar se ve distinta que recién hecha.
/// </summary>
/// <remarks>
/// Es el único estado del repertorio que cambia con el tiempo sin que pase nada. Y tiene que
/// cambiar: un orbe que espera hace diez minutos y otro que espera hace tres días diciendo
/// exactamente lo mismo le está mintiendo al usuario sobre cuánto hace que dejó algo colgado.
/// <para>
/// La antigüedad <b>viene de afuera</b>. El libro de misiones sabe cuándo se hizo la pregunta; el
/// orbe recibe un <see cref="TimeSpan"/> ya calculado y no sale a buscar ninguna fecha. Si alguna
/// vez el orbe empieza a leer relojes ajenos, la próxima cosa que va a leer es una base de datos.
/// </para>
/// </remarks>
internal static class OrbAge
{
    /// <summary>Cuánto brillo pierde con la antigüedad puesta.</summary>
    internal const double BrightnessDrop = 0.30;

    /// <summary>Cuánto se alarga la respiración: el período queda ×2,15 a los tres días.</summary>
    internal const double BreathStretch = 1.15;

    /// <summary>Cuántos píxeles se hunde, sobre las 70 unidades del lienzo.</summary>
    internal const double Sink = 3.2;

    /// <summary>
    /// Cuánto entró la antigüedad, de 0 a 1.
    /// </summary>
    /// <remarks>
    /// La rampa pasa por los dos puntos que el boceto fija con su perilla de tres posiciones: recién
    /// hecha vale 0, a las tres horas vale 0,5 y a los tres días vale 1. Entre esos puntos se
    /// interpola derecho, que es la lectura más conservadora que se puede hacer de una perilla que
    /// sólo tiene tres muescas. Lo que <em>no</em> se puede hacer es lineal de punta a punta: eso
    /// dejaría las primeras horas —que son la mayoría de las esperas reales— sin ningún cambio.
    /// </remarks>
    internal static double Strength(TimeSpan age)
    {
        var hours = age.TotalHours;
        if (hours <= 0)
        {
            return 0;
        }

        if (hours < 3)
        {
            return hours / 3 * 0.5;
        }

        return Math.Min(1, 0.5 + ((hours - 3) / (72 - 3) * 0.5));
    }

    /// <summary>Cuánto se descuelga cada grano de polvo hacia la banda de abajo.</summary>
    /// <param name="strength">La antigüedad ya resuelta a 0..1.</param>
    /// <param name="delay">El escalonado del grano, de 0 a 1. Los de atrás sedimentan más.</param>
    /// <remarks>
    /// Que el corrimiento dependa del escalonado es lo que hace que se lea como sedimento y no como
    /// que el orbe entero se cayó: unos granos bajan 2,2 px y otros 9, así que la nube se deshilacha
    /// por abajo en una banda en vez de mudarse enterita.
    /// </remarks>
    internal static double DustBand(double strength, double delay) => strength * (2.2 + (6.8 * delay));

    /// <summary>Lo que dice la píldora al costado de «te espero».</summary>
    /// <remarks>
    /// Las tres formas —«hace un rato», «hace N horas», «hace N días»— son las del boceto. Los
    /// cortes entre una y otra no están en ningún lado del fuente, que usa una perilla de tres
    /// muescas: son la hora y el día, que es como se cuenta el tiempo cuando se habla.
    /// </remarks>
    internal static string Caption(TimeSpan age)
    {
        var hours = age.TotalHours;
        if (hours < 1)
        {
            return "hace un rato";
        }

        if (hours < 24)
        {
            var whole = (int)hours;
            return whole == 1 ? "hace una hora" : $"hace {whole.ToString(CultureInfo.InvariantCulture)} horas";
        }

        var days = (int)age.TotalDays;
        return days == 1 ? "hace un día" : $"hace {days.ToString(CultureInfo.InvariantCulture)} días";
    }
}
