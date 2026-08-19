using Color = System.Windows.Media.Color;

namespace Viernes.App.Controls;

/// <summary>
/// Modo madrugada: de noche el orbe baja el volumen.
/// </summary>
/// <remarks>
/// Es el mismo criterio que ya usa la voz —<c>VoiceRegister</c> habla más bajo entre las once y las
/// seis— llevado a lo visual: a las tres de la mañana un orbe que gira y brilla igual que a las
/// diez de la mañana es una linterna en la cara. Baja la velocidad un 26 %, la opacidad un 32 % y
/// tiñe el color hacia el ámbar, que es lo que hace un ambiente iluminado con poca luz cálida.
/// <para>
/// La ventana horaria es exactamente la misma que la de la voz a propósito. Si alguna vez hay que
/// moverla, se mueve en los dos lados o Viernes queda hablando bajito con cara de mediodía.
/// </para>
/// </remarks>
internal static class OrbNight
{
    /// <summary>El tinte cálido al que se acerca el color de noche.</summary>
    private static readonly Color Warm = Color.FromRgb(255, 214, 170);

    /// <summary>Cuánto entra el modo madrugada a esta hora, de 0 a 1.</summary>
    /// <remarks>
    /// Los bordes van con una rampa de una hora en vez de un escalón: a las 22:59 y a las 23:01 el
    /// mundo no cambió, y un salto de opacidad justo cuando el usuario está mirando la pantalla se
    /// lee como un parpadeo defectuoso.
    /// </remarks>
    internal static double For(TimeOnly localTime)
    {
        var hours = localTime.Hour + (localTime.Minute / 60.0);

        // Cerrando el día: de 22 a 23 sube de 0 a 1.
        if (hours >= 22 && hours < 23)
        {
            return hours - 22;
        }

        // El corazón de la madrugada.
        if (hours >= 23 || hours < 5)
        {
            return 1;
        }

        // Amaneciendo: de 5 a 6 baja de 1 a 0.
        if (hours < 6)
        {
            return 6 - hours;
        }

        return 0;
    }

    /// <summary>Tiñe un color hacia el ámbar de la madrugada.</summary>
    internal static Color Tint(Color color, double night) =>
        night > 0.001 ? OrbPalette.LerpColor(color, Warm, 0.16 * night) : color;

    /// <summary>Cuánto se frena el movimiento de noche.</summary>
    internal static double Speed(double night) => 1 - (0.26 * Math.Clamp(night, 0, 1));

    /// <summary>Cuánto baja la opacidad de noche.</summary>
    internal static double Alpha(double night) => 1 - (0.32 * Math.Clamp(night, 0, 1));
}
