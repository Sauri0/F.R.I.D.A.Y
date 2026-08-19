namespace Viernes.App.Controls;

/// <summary>
/// El techo del lienzo. Los dos cuerpos dibujan en un espacio de 70 unidades y no pueden salirse.
/// </summary>
/// <remarks>
/// El boceto dibuja el orbe de 108 px sobre un lienzo de 372: tiene 132 px de guarda para que el
/// polvo del error salga volando y la gota se descuelgue sin que nadie los corte. Acá el control
/// mide lo que mide el orbe y esa guarda no existe, así que hay que ponerle un techo.
/// <para>
/// El techo es blando y no un recorte: hasta las tres cuartas partes del radio no toca nada —o sea,
/// ningún estado normal lo nota— y de ahí en más comprime con una exponencial que se acerca al
/// límite sin llegar nunca. Un recorte duro dejaría a la gota con un borde recto y al polvo del
/// error apilado contra una pared; así, en cambio, el error se lee como que empuja contra algo.
/// </para>
/// </remarks>
internal static class OrbBounds
{
    /// <summary>Lo más lejos del centro que puede llegar cualquier cosa, en unidades del lienzo.</summary>
    /// <remarks>
    /// El centro vertical está en 36,5 de 70, así que abajo quedan 33,5 y ese es el lado corto.
    /// Con 32,5 sobra medio punto para el grano, que se dibuja centrado en su posición.
    /// </remarks>
    internal const double MaxReach = 32.5;

    /// <summary>Dónde empieza a comprimir. Debajo de esto no toca nada.</summary>
    private const double Knee = MaxReach * 0.75;

    /// <summary>Comprime una distancia para que nunca pase de <see cref="MaxReach"/>.</summary>
    internal static double SoftLimit(double distance)
    {
        if (distance <= Knee)
        {
            return distance;
        }

        var span = MaxReach - Knee;
        return Knee + (span * (1 - Math.Exp(-(distance - Knee) / span)));
    }

    /// <summary>Lo mismo, para un desplazamiento en dos ejes: comprime el módulo y conserva la dirección.</summary>
    internal static (double X, double Y) SoftLimit(double dx, double dy)
    {
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance <= Knee || distance <= 0)
        {
            return (dx, dy);
        }

        var factor = SoftLimit(distance) / distance;
        return (dx * factor, dy * factor);
    }
}
