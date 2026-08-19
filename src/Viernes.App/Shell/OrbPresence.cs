using System.Windows;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// Esconderse y aparecer. Dos transiciones, no una con el signo cambiado.
/// </summary>
/// <remarks>
/// Irse es más lento y más blando que llegar: el orbe se encoge y se va hacia el borde más cercano,
/// que es de donde va a volver. Volver es un resorte más duro y con más rebote, porque aparecer tiene
/// que verse como una decisión y no como un <em>fade in</em>.
/// <para>
/// El eje de salida se elige al esconderse, midiendo cuál de los cuatro bordes está más cerca. Si el
/// orbe vive a la derecha de la pantalla y se va hacia la izquierda, se lee como que se fue a otro
/// lado en vez de guardarse.
/// </para>
/// </remarks>
internal sealed class OrbPresence
{
    private const double MaxFrame = 0.05;

    private double _target = 1;
    private double _velocity;

    /// <summary>Cuánto está presente, de 0 (guardado) a 1 (entero).</summary>
    public double Visibility { get; private set; } = 1;

    /// <summary>Hacia dónde se fue, en cada eje, entre -1 y 1.</summary>
    public Vector Exit { get; private set; } = new(0, 0.85);

    /// <summary>Si ya terminó de irse y no hay nada que dibujar.</summary>
    public bool IsGone => Visibility < 0.02;

    /// <summary>
    /// Si se está yendo o ya se fue. Lo contrario es «está entero o está volviendo».
    /// </summary>
    /// <remarks>
    /// No alcanza con <see cref="IsGone"/> para preguntar «¿hace falta esconderlo?»: durante el
    /// medio segundo de la retirada el orbe todavía se ve y <c>IsGone</c> dice que no, así que quien
    /// vigila la pantalla completa volvería a mandarlo a esconder una vez por segundo sobre algo que
    /// ya se está escondiendo.
    /// </remarks>
    public bool IsLeaving => _target == 0;

    /// <summary>Si está entero y quieto: no hay animación en curso.</summary>
    public bool IsSettled => Math.Abs(Visibility - _target) < 0.004 && Math.Abs(_velocity) < 0.03;

    /// <summary>Cuándo fue el último <see cref="Aparecer"/>. El orbe lo convierte en una onda.</summary>
    public long AppearedAtTicks { get; private set; }

    /// <summary>
    /// Se guarda hacia el borde más cercano.
    /// </summary>
    /// <param name="orbCenter">Centro del orbe, en coordenadas de la pantalla.</param>
    /// <param name="workArea">Área útil del monitor donde vive.</param>
    public void Esconder(Point orbCenter, Rect workArea)
    {
        if (_target == 0)
        {
            return;
        }

        var left = orbCenter.X - workArea.Left;
        var right = workArea.Right - orbCenter.X;
        var top = orbCenter.Y - workArea.Top;
        var bottom = workArea.Bottom - orbCenter.Y;
        var nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));

        var ex = nearest == left ? -1 : nearest == right ? 1 : 0;
        var ey = nearest == top ? -1 : nearest == bottom ? 1 : (nearest == left || nearest == right ? 0.25 : 0.85);

        Exit = new Vector(ex, ey);
        _target = 0;

        // Un empujón inicial: irse tiene que arrancar moviéndose, no acelerar desde cero.
        _velocity = 0.9;
    }

    /// <summary>Vuelve entero, desde donde se había ido.</summary>
    public void Aparecer()
    {
        if (_target == 1 && Visibility > 0.99)
        {
            return;
        }

        _target = 1;
        AppearedAtTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Avanza el resorte de presencia.</summary>
    public void Step(double dt)
    {
        if (Math.Abs(Visibility - _target) < 0.0001 && Math.Abs(_velocity) < 0.001)
        {
            return;
        }

        var step = Math.Min(MaxFrame, dt);
        var arriving = _target > 0.5;
        var stiffness = arriving ? 250 : 165;
        var damping = arriving ? 21 : 27;

        _velocity += ((_target - Visibility) * stiffness - _velocity * damping) * step;
        Visibility += _velocity * step;

        if (!arriving && Visibility < 0.004)
        {
            Visibility = 0;
            _velocity = 0;
        }

        if (arriving && Math.Abs(Visibility - 1) < 0.004 && Math.Abs(_velocity) < 0.03)
        {
            Visibility = 1;
            _velocity = 0;
        }
    }

    /// <summary>Escala del cuerpo: nunca llega a cero, se va encogiendo hasta un tercio.</summary>
    public double Scale => 0.30 + 0.70 * Math.Clamp(Visibility, 0, 1);

    /// <summary>Opacidad del cuerpo. La curva evita que se apague de golpe al final.</summary>
    public double Opacity => Math.Pow(Math.Clamp(Visibility, 0, 1), 0.55);

    /// <summary>Cuánto se corrió hacia el borde por el que se va.</summary>
    public Vector Offset => Exit * (30 * (1 - Math.Clamp(Visibility, 0, 1)));
}
