using System.Windows;
using Viernes.App.Services;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Shell;

/// <summary>
/// El escritorio visto por el orbe: una celda por monitor, y por dónde se pasa de una a otra.
/// </summary>
/// <remarks>
/// Existe por una queja concreta: «no debería rebotar en el borde de la pantalla que une a la otra
/// pantalla, debería pasar directo a la otra». Antes el orbe volaba contra
/// <see cref="ShellLayout.OrbBounds"/> del monitor donde estaba, y ese rectángulo tiene cuatro
/// paredes: la que da al escritorio y la que da a la pantalla de al lado son la misma cosa para la
/// física, y no lo son para el usuario.
/// <para>
/// <b>Un borde no está abierto o cerrado: está abierto o cerrado en el lugar donde el orbe lo está
/// cruzando.</b> Dos monitores de distinta altura comparten sólo un tramo del borde; arriba y abajo
/// de ese tramo hay pared. Por eso acá no se responde «¿este lado da a otra pantalla?» sino «¿si
/// sigo derecho desde esta altura, caigo en un lugar donde el orbe puede estar?». La respuesta usa
/// la celda del vecino —ya con su margen puesto—, así que cruzar nunca deja al orbe en un lugar
/// donde no podría haber estado si hubiera llegado caminando.
/// </para>
/// <para>
/// Y por eso mismo un hueco del escritorio virtual —dos monitores que no se tocan— queda cerrado sin
/// ningún caso especial: si los bordes no coinciden, la comparación de abajo no da, el lado sigue
/// siendo pared y el orbe rebota en vez de quedar flotando en la nada.
/// </para>
/// </remarks>
internal sealed class DesktopField
{
    /// <summary>Cuánto pueden diferir dos bordes y seguir contando como el mismo.</summary>
    /// <remarks>Es el mismo criterio y el mismo número que <c>MonitorMove.AreAdjacent</c>.</remarks>
    private const double Touching = 1;

    /// <summary>Un monitor: su clave, su área útil y dónde puede estar la esquina del orbe en él.</summary>
    internal readonly record struct Cell(string Key, Rect Work, Rect Orb);

    private readonly Cell[] _cells;

    private DesktopField(Cell[] cells) => _cells = cells;

    /// <summary>
    /// Mide el escritorio entero. <paramref name="scaleX"/> y <paramref name="scaleY"/> convierten
    /// los píxeles físicos que informa Windows al espacio donde viven <c>Left</c> y <c>Top</c>.
    /// </summary>
    /// <remarks>
    /// La escala se pide en vez de averiguarse acá a propósito: <c>Left</c> y <c>Top</c> los
    /// interpreta WPF con el DPI de <em>la ventana</em>, no con el del monitor que se está midiendo,
    /// así que la conversión correcta para posicionar una ventana es la de esa ventana. Quien llama
    /// es el único que la sabe. Con dos monitores de escalas distintas esto sigue sin ser exacto
    /// —está dicho en <c>MainWindow.ToLogical</c>—, pero al menos hay un solo lugar donde se decide.
    /// </remarks>
    public static DesktopField Measure(double scaleX, double scaleY)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var areas = new (string, Rect)[screens.Length];

        for (var i = 0; i < screens.Length; i++)
        {
            var physical = screens[i].WorkingArea;
            areas[i] = (MonitorSlots.KeyFor(screens[i]), new Rect(
                physical.Left / scaleX,
                physical.Top / scaleY,
                physical.Width / scaleX,
                physical.Height / scaleY));
        }

        return Of(areas);
    }

    /// <summary>
    /// Arma el escritorio con áreas útiles dadas, ya en unidades de WPF.
    /// </summary>
    /// <remarks>
    /// Es por donde entra <see cref="Measure"/> y también las pruebas. Existe porque la geometría de
    /// esto —dos pantallas de distinta altura que comparten sólo un tramo, un hueco en el escritorio
    /// virtual, un vecino arriba en vez de al costado— no se puede comprobar con los monitores que
    /// tenga enchufada la máquina donde se compila: hay que poder inventarlos.
    /// </remarks>
    internal static DesktopField Of(params (string Key, Rect Work)[] areas)
    {
        var cells = new Cell[areas.Length];
        for (var i = 0; i < areas.Length; i++)
        {
            cells[i] = new Cell(areas[i].Key, areas[i].Work, ShellLayout.OrbBounds(areas[i].Work));
        }

        return new DesktopField(cells);
    }

    /// <summary>
    /// Hasta dónde puede llegar el orbe desde donde está: su celda, abierta por los lados que dan a
    /// otra pantalla <em>a esta altura</em>.
    /// </summary>
    /// <param name="work">Área útil del monitor donde está el orbe ahora.</param>
    /// <param name="orb">Esquina superior izquierda del orbe.</param>
    /// <remarks>
    /// Devuelve un rectángulo y no una lista de paredes porque eso es lo que la física sabe consumir
    /// —<c>OrbMotion.Step</c> recibe un <c>Rect</c> y rebota contra sus cuatro lados— y porque
    /// recalcularlo por cuadro alcanza: a 180 Hz el orbe se corre unos pocos píxeles entre cuadro y
    /// cuadro, muchísimo menos que cualquier tramo de borde compartido.
    /// <para>
    /// Cuando el orbe ya está metido en la costura —pasó el borde de su propia celda y todavía no
    /// llegó a la del vecino— el alto se recorta a lo que las dos celdas tienen en común. Sin eso,
    /// un orbe cruzando en diagonal entre dos pantallas de distinta altura podría salirse del tramo
    /// compartido mientras cruza y aparecer del otro lado pegando un salto vertical al recortarse.
    /// </para>
    /// </remarks>
    public Rect Reach(Rect work, Point orb)
    {
        var home = ShellLayout.OrbBounds(work);
        var left = home.Left;
        var top = home.Top;
        var right = home.Right;
        var bottom = home.Bottom;

        // Se le pregunta al vecino con la coordenada RECORTADA, no con la cruda.
        //
        // El orbe puede estar fuera de su celda al preguntar: durante el arrastre nadie lo recorta,
        // y el resorte se pasa de los bordes a propósito. Con la coordenada cruda, un orbe apoyado
        // contra el borde de abajo —Y por debajo de 952 en una pantalla de 1080— no pasaba la prueba
        // de altura del vecino de al lado, así que la costura se cerraba y volvía a ser una pared
        // justo en el cuadro en que lo tirabas hacia la otra pantalla. Medido con las dos pantallas
        // del usuario: soltado en (-100;1000) a 1200 px/s hacia la derecha, los límites daban
        // [-1900..-128] y el orbe terminaba 223 px a la IZQUIERDA. Con Y = 952 daban [-1900..1792] y
        // cruzaba bien. O sea que el orbe salía disparado al revés de como lo tiraste.
        //
        // Recortar acá es legítimo porque ese recorte va a pasar igual una línea después, en
        // ClampInto: se le pregunta al vecino por el punto donde el orbe REALMENTE va a estar.
        var y = Math.Clamp(orb.Y, home.Top, home.Bottom);
        var x = Math.Clamp(orb.X, home.Left, home.Right);

        foreach (var cell in _cells)
        {
            if (IsSame(cell.Work, work))
            {
                continue;
            }

            // Vecino a la izquierda: su borde derecho es el izquierdo de éste, y a la altura del
            // orbe ese monitor todavía existe.
            if (Math.Abs(cell.Work.Right - work.Left) <= Touching &&
                y >= cell.Orb.Top && y <= cell.Orb.Bottom)
            {
                left = Math.Min(left, cell.Orb.Left);
                if (orb.X < home.Left)
                {
                    top = Math.Max(top, cell.Orb.Top);
                    bottom = Math.Min(bottom, cell.Orb.Bottom);
                }
            }

            if (Math.Abs(cell.Work.Left - work.Right) <= Touching &&
                y >= cell.Orb.Top && y <= cell.Orb.Bottom)
            {
                right = Math.Max(right, cell.Orb.Right);
                if (orb.X > home.Right)
                {
                    top = Math.Max(top, cell.Orb.Top);
                    bottom = Math.Min(bottom, cell.Orb.Bottom);
                }
            }

            if (Math.Abs(cell.Work.Bottom - work.Top) <= Touching &&
                x >= cell.Orb.Left && x <= cell.Orb.Right)
            {
                top = Math.Min(top, cell.Orb.Top);
                if (orb.Y < home.Top)
                {
                    left = Math.Max(left, cell.Orb.Left);
                    right = Math.Min(right, cell.Orb.Right);
                }
            }

            if (Math.Abs(cell.Work.Top - work.Bottom) <= Touching &&
                x >= cell.Orb.Left && x <= cell.Orb.Right)
            {
                bottom = Math.Max(bottom, cell.Orb.Bottom);
                if (orb.Y > home.Bottom)
                {
                    left = Math.Max(left, cell.Orb.Left);
                    right = Math.Min(right, cell.Orb.Right);
                }
            }
        }

        // El recorte de la costura puede cruzar los bordes si el tramo común es más chico que el
        // orbe. Un Rect con ancho negativo es una excepción, así que se prefiere la celda de casa:
        // volver a rebotar contra la pared conocida es peor que cruzar, y es mucho mejor que caerse.
        if (right < left || bottom < top)
        {
            return home;
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// En qué monitor está el orbe, medido con el <b>centro</b> y no con la esquina.
    /// </summary>
    /// <remarks>
    /// Con la esquina, un orbe de 108 px apoyado contra la costura contesta el monitor de al lado
    /// mientras se lo ve entero de este lado. El centro es el único punto que dice lo mismo que ve
    /// el usuario. Si el centro no cae en ninguno —un hueco del escritorio virtual—, gana el más
    /// cercano: contestar «ninguno» obligaría a todos los que llaman a inventar un caso especial.
    /// </remarks>
    public string KeyAt(Point orb)
    {
        var centre = new Point(orb.X + (ShellLayout.OrbSize / 2), orb.Y + (ShellLayout.OrbSize / 2));
        var best = string.Empty;
        var bestDistance = double.MaxValue;

        foreach (var cell in _cells)
        {
            if (cell.Work.Contains(centre))
            {
                return cell.Key;
            }

            var dx = Math.Max(0, Math.Max(cell.Work.Left - centre.X, centre.X - cell.Work.Right));
            var dy = Math.Max(0, Math.Max(cell.Work.Top - centre.Y, centre.Y - cell.Work.Bottom));
            var distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cell.Key;
            }
        }

        return best;
    }

    private static bool IsSame(Rect one, Rect other) =>
        Math.Abs(one.Left - other.Left) <= Touching &&
        Math.Abs(one.Top - other.Top) <= Touching &&
        Math.Abs(one.Right - other.Right) <= Touching &&
        Math.Abs(one.Bottom - other.Bottom) <= Touching;
}
