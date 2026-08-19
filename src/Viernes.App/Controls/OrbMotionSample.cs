namespace Viernes.App.Controls;

/// <summary>
/// Lo que el cuerpo necesita saber del movimiento de su propia ventana, en un cuadro.
/// </summary>
/// <remarks>
/// Es la costura que faltaba. La ventana tenía física completa —resorte, roce, rebote, imán— y el
/// cuerpo la ignoraba: <c>grep Velocity</c> dentro de <c>Controls/</c> devolvía sólo el resorte de
/// escala y el satélite del corte. Por eso el orbe no se estiraba hacia donde iba, no dejaba polvo
/// atrás y no se achataba al golpear: nadie le contaba que se estaba moviendo.
/// <para>
/// La velocidad llega ya suavizada por <see cref="Shell.OrbMotion"/> —0,72 de lo viejo más 0,28 de
/// lo nuevo—, así que acá no se vuelve a filtrar: filtrar dos veces sería llegar tarde dos veces.
/// </para>
/// <para>
/// El golpe viaja por <paramref name="HitToken"/> y no por una bandera: una bandera hay que apagarla
/// y el cuerpo dibuja en su propio reloj —la nube a 30 cuadros, la gota a los que haya—, así que un
/// golpe podía quedar encendido dos cuadros o perderse entero. Un número que sólo crece se compara
/// contra el último visto y no se pierde ni se repite.
/// </para>
/// </remarks>
/// <param name="VelocityX">Velocidad horizontal de la ventana, px/s, ya suavizada.</param>
/// <param name="VelocityY">Ídem vertical, positiva hacia abajo.</param>
/// <param name="IsDragging">Si el usuario lo tiene agarrado. El polvo sólo se desprende arrastrando.</param>
/// <param name="HitToken">Cambia una vez por golpe contra un borde. Cero es «todavía ninguno».</param>
/// <param name="HitNormalX">Normal del borde golpeado: +1 el izquierdo, −1 el derecho.</param>
/// <param name="HitNormalY">Normal del borde golpeado: +1 el de arriba, −1 el de abajo.</param>
/// <param name="HitStrength">Con cuánta fuerza llegó, de 0 a 1.</param>
internal readonly record struct OrbMotionSample(
    double VelocityX,
    double VelocityY,
    bool IsDragging,
    int HitToken,
    double HitNormalX,
    double HitNormalY,
    double HitStrength)
{
    /// <summary>
    /// A esta rapidez el efecto del movimiento está al máximo. Del fuente: <c>min(1, hypot(vx,vy)/1500)</c>.
    /// </summary>
    /// <remarks>
    /// Vive acá y en ningún otro lado. La misma constante escrita dos veces ya costó una vez —el
    /// umbral del micrófono— y el banco de medición terminó informando contra la copia vieja.
    /// </remarks>
    internal const double SpeedReference = 1500;

    /// <summary>Rapidez normalizada de 0 a 1. Es el único número del que salen los siete efectos.</summary>
    internal double Speed01 =>
        Math.Min(1, Math.Sqrt((VelocityX * VelocityX) + (VelocityY * VelocityY)) / SpeedReference);

    /// <summary>Hacia dónde va, en radianes. Es el eje sobre el que se estira el cuerpo.</summary>
    internal double Angle => Math.Atan2(VelocityY, VelocityX);
}

/// <summary>
/// Un cuerpo que se entera de que su ventana se mueve.
/// </summary>
/// <remarks>
/// Está aparte de <see cref="IOrbBody"/> a propósito. La píldora también implementa
/// <c>IOrbBody</c> —para que estado, ánimo, madrugada y escritorio se repartan una sola vez— pero es
/// texto, y un texto que se estira hacia donde va el orbe deja de leerse. Ponerle el método ahí la
/// obligaría a escribir un cuerpo vacío que nadie llama, que es justo el hallazgo que este repo se
/// cansó de leer en los informes.
/// </remarks>
internal interface IOrbMotionSink
{
    /// <summary>
    /// Le pasa al cuerpo el movimiento de este cuadro. Se llama siempre, aunque esté quieto.
    /// </summary>
    /// <remarks>
    /// Sólo anota: consumir esto acá haría el trabajo en el cuadro de la ventana y no en el del
    /// cuerpo, que son dos relojes distintos —la nube corre a 30—. El cuerpo lo lee cuando dibuja.
    /// </remarks>
    void ReportMotion(in OrbMotionSample motion);
}
