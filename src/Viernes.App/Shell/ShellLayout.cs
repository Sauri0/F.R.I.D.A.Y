using System.Windows;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// Las medidas de la ventana y dónde cae cada cosa adentro.
/// </summary>
/// <remarks>
/// La decisión que ordena todo: <b>la ventana mide siempre lo mismo</b> —el desplegable más ancho,
/// el más alto, y aire para las sombras— y es transparente. Lo que se anima es el vidrio de adentro.
/// Cambiar <c>Window.Width</c> y <c>Window.Height</c> por cuadro obliga a Windows a recrear la
/// superficie de la ventana en cada paso, y como el orbe está anclado a una esquina hay que corregir
/// <c>Left</c> y <c>Top</c> a mano: eso es lo que se veía como un salto cada vez que el panel se abría.
/// <para>
/// El precio es una ventana grande y transparente por encima del escritorio. No molesta porque una
/// ventana <em>layered</em> deja pasar el clic por los píxeles con alfa cero: donde no hay vidrio ni
/// orbe, el clic va a parar a lo que haya abajo.
/// </para>
/// </remarks>
internal static class ShellLayout
{
    /// <summary>El orbe mide 108 × 108. No cambia nunca.</summary>
    public const double OrbSize = 108;

    /// <summary>Dónde arranca el vidrio, medido desde el borde del orbe. Se solapan 8 px.</summary>
    public const double PanelReach = 100;

    /// <summary>Aire alrededor del contenido para que la sombra larga tenga dónde caer.</summary>
    public const double ShadowPad = 26;

    /// <summary>Ancho útil: el alcance del panel más el desplegable más ancho de los trece.</summary>
    public static double ContentWidth { get; } = PanelReach + PanelCatalog.MaxWidth;

    /// <summary>Alto útil: el desplegable más alto de los trece, que siempre supera al orbe.</summary>
    public static double ContentHeight { get; } = Math.Max(OrbSize, PanelCatalog.MaxHeight);

    /// <summary>Ancho de la ventana. Constante durante toda la sesión.</summary>
    public static double WindowWidth { get; } = ContentWidth + 2 * ShadowPad;

    /// <summary>Alto de la ventana. Constante durante toda la sesión.</summary>
    public static double WindowHeight { get; } = ContentHeight + 2 * ShadowPad;

    /// <summary>Dónde cae el orbe dentro de la ventana, verticalmente centrado.</summary>
    public static double OrbTop { get; } = ShadowPad + (ContentHeight - OrbSize) / 2;

    /// <summary>Dónde cae el orbe cuando el panel se abre hacia la derecha.</summary>
    public static double OrbLeftWhenOpeningRight => ShadowPad;

    /// <summary>Dónde cae el orbe cuando el panel se abre hacia la izquierda.</summary>
    public static double OrbLeftWhenOpeningLeft { get; } = ShadowPad + ContentWidth - OrbSize;

    /// <summary>Dónde arranca el marco del panel según el lado.</summary>
    public static double PanelHostLeft(bool opensRight) =>
        opensRight ? ShadowPad + PanelReach : ShadowPad;

    /// <summary>Esquina de la ventana para que el orbe caiga en <paramref name="orb"/>.</summary>
    public static Point WindowOriginFor(Point orb, bool opensRight) => new(
        orb.X - (opensRight ? OrbLeftWhenOpeningRight : OrbLeftWhenOpeningLeft),
        orb.Y - OrbTop);

    /// <summary>Dónde está el orbe si la ventana tiene esa esquina.</summary>
    public static Point OrbOriginFor(Point window, bool opensRight) => new(
        window.X + (opensRight ? OrbLeftWhenOpeningRight : OrbLeftWhenOpeningLeft),
        window.Y + OrbTop);

    /// <summary>
    /// Hasta dónde puede llegar la esquina del orbe dentro de un área útil, ya con el margen puesto.
    /// </summary>
    public static Rect OrbBounds(Rect workArea)
    {
        var left = workArea.Left + OrbMotion.Margin;
        var top = workArea.Top + OrbMotion.Margin;
        var right = Math.Max(left, workArea.Right - OrbSize - OrbMotion.Margin);
        var bottom = Math.Max(top, workArea.Bottom - OrbSize - OrbMotion.Margin);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Si hay lugar para abrir hacia la derecha. Si no lo hay de ningún lado, gana la derecha:
    /// el panel se recorta antes de que el orbe se mueva de donde el usuario lo dejó.
    /// </summary>
    public static bool ShouldOpenRight(Point orb, Rect workArea) =>
        orb.X + OrbSize + PanelCatalog.MaxWidth <= workArea.Right ||
        orb.X - PanelCatalog.MaxWidth < workArea.Left;
}
