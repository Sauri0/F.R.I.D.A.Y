using System.Windows;
using Viernes.Core.Configuration;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// Las medidas de la ventana y dónde cae cada cosa adentro.
/// </summary>
/// <remarks>
/// La decisión que ordena todo: <b>la ventana no cambia de tamaño mientras se usa</b> —el desplegable
/// más ancho, el más alto, y aire para las sombras— y es transparente. Lo que se anima es el vidrio
/// de adentro. Cambiar <c>Window.Width</c> y <c>Window.Height</c> por cuadro obliga a Windows a
/// recrear la superficie de la ventana en cada paso, y como el orbe está anclado a una esquina hay
/// que corregir <c>Left</c> y <c>Top</c> a mano: eso es lo que se veía como un salto cada vez que el
/// panel se abría.
/// <para>
/// Lo que sí la cambia es el tamaño elegido para el orbe —ver <see cref="Scale"/>—, y eso no
/// contradice lo anterior: pasa una vez cuando el usuario mueve la barra, no sesenta veces por
/// segundo mientras un panel se abre.
/// </para>
/// <para>
/// El precio es una ventana grande y transparente por encima del escritorio. No molesta porque una
/// ventana <em>layered</em> deja pasar el clic por los píxeles con alfa cero: donde no hay vidrio ni
/// orbe, el clic va a parar a lo que haya abajo.
/// </para>
/// </remarks>
internal static class ShellLayout
{
    /// <summary>El orbe de fábrica mide 108 × 108. Es el 100 % de la barra de tamaño.</summary>
    public const double DefaultOrbSize = 108;

    /// <summary>Cuánto se mete el vidrio por debajo del orbe. Es el solape de siempre.</summary>
    /// <remarks>
    /// Estaba escrito como la diferencia entre dos constantes —<c>PanelReach</c> 100 contra un orbe
    /// de 108— y por eso no seguía al orbe cuando éste cambiaba de tamaño: al 50 % quedaba un hueco
    /// de 46 px entre el cuerpo y su desplegable, y al 200 % el cuerpo le tapaba 116 px al vidrio.
    /// Acá el 8 es el dato y el alcance sale de él, que es la relación que la ventana ya tenía.
    /// </remarks>
    public const double PanelOverlap = 8;

    /// <summary>Aire alrededor del contenido para que la sombra larga tenga dónde caer.</summary>
    public const double ShadowPad = 26;

    private static double _scale = OrbScaleRange.Default;

    /// <summary>
    /// Qué tan grande es el orbe respecto del de fábrica. Sale de las preferencias del usuario.
    /// </summary>
    /// <remarks>
    /// Es estático y mutable porque el tamaño del orbe es una sola cosa para todo el proceso: hay
    /// una ventana, un orbe y una preferencia. Todo lo que depende de él —el alto útil, dónde cae el
    /// orbe adentro de la ventana, hasta dónde puede llegar contra los bordes— se calcula al usarse
    /// y no una vez al arrancar; antes eran <c>static readonly</c> con inicializador, que es lo
    /// mismo que congelar el valor del arranque.
    /// </remarks>
    public static double Scale
    {
        get => _scale;
        set => _scale = OrbScaleRange.Clamp(value);
    }

    /// <summary>Cuánto mide el orbe ahora mismo, ya con el tamaño elegido puesto.</summary>
    public static double OrbSize => DefaultOrbSize * _scale;

    /// <summary>Dónde arranca el vidrio, medido desde el borde del orbe. Se solapan 8 px.</summary>
    public static double PanelReach => OrbSize - PanelOverlap;

    /// <summary>Ancho útil: el alcance del panel más el desplegable más ancho de los trece.</summary>
    public static double ContentWidth => PanelReach + PanelCatalog.MaxWidth;

    /// <summary>
    /// Alto útil: el desplegable más alto, que le gana al orbe en todo el rango de tamaños.
    /// </summary>
    /// <remarks>
    /// El <see cref="Math.Max(double, double)"/> no es defensa por las dudas: es la razón por la que
    /// el tope de la barra es 200 %. El panel más alto mide 220 y el orbe al doble mide 216, así que
    /// en todo el rango legal esta cuenta devuelve 220 y el alto de la ventana no se mueve nunca.
    /// </remarks>
    public static double ContentHeight => Math.Max(OrbSize, PanelCatalog.MaxHeight);

    /// <summary>Ancho de la ventana. Sigue al tamaño del orbe, porque el alcance del panel lo sigue.</summary>
    public static double WindowWidth => ContentWidth + 2 * ShadowPad;

    /// <summary>Alto de la ventana. En todo el rango de tamaños da lo mismo: 272.</summary>
    public static double WindowHeight => ContentHeight + 2 * ShadowPad;

    /// <summary>Dónde cae el orbe dentro de la ventana, verticalmente centrado.</summary>
    public static double OrbTop => ShadowPad + (ContentHeight - OrbSize) / 2;

    /// <summary>Dónde cae el orbe cuando el panel se abre hacia la derecha.</summary>
    public static double OrbLeftWhenOpeningRight => ShadowPad;

    /// <summary>Dónde cae el orbe cuando el panel se abre hacia la izquierda.</summary>
    public static double OrbLeftWhenOpeningLeft => ShadowPad + ContentWidth - OrbSize;

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
