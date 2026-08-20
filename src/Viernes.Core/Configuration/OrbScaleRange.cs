namespace Viernes.Core.Configuration;

/// <summary>
/// Hasta dónde puede crecer y achicarse el orbe, en fracción de su tamaño de fábrica.
/// </summary>
/// <remarks>
/// Vive en Core y no en el shell porque hacen falta los dos extremos en dos lados que no se ven
/// entre sí: la geometría de la ventana, en Viernes.App, y la normalización del archivo de
/// preferencias, en Viernes.Platform.Windows. Con el rango escrito dos veces, el día que se mueva el
/// tope quedaría un archivo que acepta un valor que la ventana no sabe dibujar.
/// <para>
/// <b>El 200 % no es un número redondo elegido a ojo.</b> El alto útil de la ventana es el del
/// desplegable más alto —220 px— y el orbe de fábrica mide 108: mientras el orbe no pase de 220, el
/// alto de la ventana no cambia y ningún panel se mueve de donde está. Al 200 % el orbe mide 216, o
/// sea que entra por cuatro píxeles. Un tope más alto obligaría a que la ventana creciera también
/// para arriba y a recolocarla en los dos ejes; éste no.
/// </para>
/// <para>
/// El 50 % es el otro lado: 54 px es el tamaño donde el cuerpo todavía se lee como un cuerpo y la
/// superficie que recibe el clic sigue siendo agarrable con la mano.
/// </para>
/// </remarks>
public static class OrbScaleRange
{
    /// <summary>Lo más chico que se puede pedir: la mitad.</summary>
    public const double Minimum = 0.5;

    /// <summary>Lo más grande: el doble, que es lo que entra sin mover el alto de la ventana.</summary>
    public const double Maximum = 2.0;

    /// <summary>El tamaño de fábrica, el mismo que tuvo el orbe hasta que esto existió.</summary>
    public const double Default = 1.0;

    /// <summary>
    /// Deja el valor adentro del rango. Lo que no es un número vuelve al de fábrica.
    /// </summary>
    /// <remarks>
    /// <c>NaN</c> se trata aparte porque <see cref="Math.Clamp(double, double, double)"/> lo deja
    /// pasar tal cual, y un tamaño <c>NaN</c> no se ve como un orbe raro: se ve como una ventana que
    /// no se dibuja.
    /// </remarks>
    public static double Clamp(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, Minimum, Maximum) : Default;
}
