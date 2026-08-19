using System.Windows;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point existe dos veces.
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// Cómo se muda el orbe de un monitor a otro.
/// </summary>
/// <remarks>
/// Dos viajes distintos, y cuál se usa lo decide la geometría: si los dos monitores se tocan, el
/// orbe cruza volando y deja estela; si no se tocan, se va por un borde y vuelve por el otro. La
/// regla dura es <b>nunca cruza contenido</b>: entre pantallas que no son vecinas hay escritorio,
/// ventanas y a veces nada —un hueco en el escritorio virtual—, y atravesarlo en línea recta se lee
/// como un objeto pasando por encima de todo lo que el usuario está mirando.
/// <para>
/// Los tres números salen del fuente de la referencia, de <c>mudar(lejos)</c>:
/// <c>M.sx = (tx - M.x) * 3.4</c>, <c>M.sy = -55</c> y el <c>setTimeout</c> de 520 ms entre esconderse
/// y aparecer del otro lado. El margen contra el borde —20— ya vive en <see cref="OrbMotion.Margin"/>
/// y sale del mismo lugar.
/// </para>
/// </remarks>
internal static class MonitorMove
{
    /// <summary>
    /// Cuánto empuje horizontal por píxel de distancia.
    /// </summary>
    /// <remarks>
    /// No es una velocidad fija: es proporcional a lo que hay que recorrer, así que cruzar una
    /// pantalla chica y una grande tardan parecido. Con el rozamiento exponencial del vuelo, 3,4 deja
    /// el orbe llegando al otro lado con energía para el imán del borde.
    /// </remarks>
    public const double Kick = 3.4;

    /// <summary>Empujón vertical del despegue. Negativo: el viaje arquea hacia arriba.</summary>
    public const double Lift = -55;

    /// <summary>Cuánto tarda en salir por un borde antes de aparecer por el otro.</summary>
    public static readonly TimeSpan EdgeGap = TimeSpan.FromMilliseconds(520);

    /// <summary>
    /// Techo del viaje adyacente. Si a esto no llegó, llegó igual.
    /// </summary>
    /// <remarks>
    /// El vuelo termina solo cuando la velocidad baja del umbral de asentamiento, pero eso depende de
    /// contra qué rebotó en el camino. Sin un techo, un vuelo que quedara oscilando dejaría al orbe
    /// con los límites de dos monitores puestos para siempre, o sea libre de quedarse en el medio.
    /// </remarks>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(3);

    /// <summary>Cuánto pueden diferir dos bordes y seguir contando como el mismo.</summary>
    private const double Touching = 1;

    /// <summary>
    /// Si los dos monitores comparten un borde.
    /// </summary>
    /// <remarks>
    /// Compartir un borde es tocarse en un lado <em>y</em> solaparse en el otro eje: dos pantallas
    /// pegadas en diagonal —esquina con esquina— cumplen lo primero y no lo segundo, y entre ellas no
    /// hay por dónde cruzar sin pasar por el medio de la pantalla.
    /// </remarks>
    public static bool AreAdjacent(Rect one, Rect other)
    {
        var sideBySide =
            (Math.Abs(one.Right - other.Left) <= Touching || Math.Abs(other.Right - one.Left) <= Touching) &&
            Overlap(one.Top, one.Bottom, other.Top, other.Bottom) > 0;

        var stacked =
            (Math.Abs(one.Bottom - other.Top) <= Touching || Math.Abs(other.Bottom - one.Top) <= Touching) &&
            Overlap(one.Left, one.Right, other.Left, other.Right) > 0;

        return sideBySide || stacked;
    }

    private static double Overlap(double oneFrom, double oneTo, double otherFrom, double otherTo) =>
        Math.Min(oneTo, otherTo) - Math.Max(oneFrom, otherFrom);
}

/// <summary>
/// Una mudanza en vuelo.
/// </summary>
/// <param name="Bounds">
/// Dónde puede estar el orbe mientras dura: los límites de los dos monitores juntos. Sin esto, el
/// recorte por cuadro lo devuelve al monitor de origen y el viaje no arranca nunca.
/// </param>
/// <param name="Destination">Dónde tiene que terminar.</param>
/// <param name="DeadlineUtc">Cuándo se da por terminada aunque no haya llegado.</param>
internal sealed record MonitorTravel(Rect Bounds, Point Destination, DateTime DeadlineUtc);
