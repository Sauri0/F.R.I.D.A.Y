using System.Windows;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// La física del orbe: arrastre con inercia, rebote contra los bordes e imán en las esquinas.
/// </summary>
/// <remarks>
/// Todo se integra con pasos fijos de 1/120 s y no con el <c>dt</c> del cuadro. Es la única forma de
/// que un resorte se sienta igual a 30 fps que a 144: con paso variable, un cuadro largo mete un
/// salto grande, el resorte se pasa y el rebote cambia de altura según lo ocupada que esté la
/// máquina. Un objeto que rebota distinto cada vez deja de leerse como objeto.
/// <para>
/// Las constantes salen medidas de la referencia ejecutable y no son intercambiables entre sí: el
/// resorte del arrastre es más duro que el de reposo porque tiene que seguir al dedo, y el
/// rozamiento del vuelo es exponencial para que la desaceleración se vea, no se calcule.
/// </para>
/// </remarks>
internal sealed class OrbMotion
{
    /// <summary>Cuánto se separa del borde de la pantalla cuando se apoya.</summary>
    public const double Margin = 20;

    /// <summary>A menos de esta distancia de un borde, el orbe termina pegado a él.</summary>
    public const double MagnetRange = 58;

    private const double SubStep = 1.0 / 120;
    private const double MaxFrame = 0.05;

    private const double DragStiffness = 146;
    private const double DragDamping = 15.5;
    private const double RestStiffness = 104;
    private const double RestDamping = 14.5;
    private const double FlyFriction = 0.075;
    private const double SettleSpeed = 44;
    private const double MinBounceSpeed = 18;

    /// <summary>Dónde está el orbe ahora, esquina superior izquierda de sus 108 px.</summary>
    public Point Position { get; private set; }

    /// <summary>A dónde tiende cuando no está volando: el imán, o el dedo.</summary>
    public Point Target { get; private set; }

    /// <summary>Velocidad en píxeles por segundo.</summary>
    public Vector Velocity { get; private set; }

    /// <summary>Si el usuario lo tiene agarrado.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Si viene de ser soltado y todavía tiene inercia.</summary>
    public bool IsFlying { get; private set; }

    /// <summary>Rapidez instantánea. La usa el panel para esconderse mientras el orbe vuela.</summary>
    public double Speed => Velocity.Length;

    /// <summary>Deja el orbe donde se lo pone, sin inercia ni destino pendiente.</summary>
    public void Teleport(Point position)
    {
        Position = position;
        Target = position;
        Velocity = default;
        IsFlying = false;
    }

    /// <summary>
    /// Le da un destino sin moverlo: el resorte de reposo lo lleva hasta ahí.
    /// </summary>
    /// <remarks>
    /// Es lo que usa la llegada desde la bandeja. Animar <c>Left</c> y <c>Top</c> con un
    /// <c>Storyboard</c> pelearía con el bucle de física, que escribe la posición cuadro a cuadro;
    /// darle un destino al mismo resorte que ya existe hace que la llegada se mueva igual que
    /// cualquier otro desplazamiento del orbe, que es justamente lo que se quiere.
    /// </remarks>
    public void Nudge(Point target)
    {
        IsFlying = false;
        Target = target;
    }

    /// <summary>Empieza el arrastre. Desde acá el destino lo manda el puntero.</summary>
    public void BeginDrag()
    {
        IsDragging = true;
        IsFlying = false;
        Target = Position;
    }

    /// <summary>
    /// Durante el arrastre la posición la impone la ventana —la mueve Windows—, así que se anota y
    /// se estima la velocidad para poder soltarla después.
    /// </summary>
    public void ReportDragged(Point position, double dt)
    {
        if (dt > 0.0005)
        {
            var instant = new Vector((position.X - Position.X) / dt, (position.Y - Position.Y) / dt);

            // Promedio corrido: un solo cuadro es ruido, y soltar con ruido dispara el orbe.
            Velocity = Velocity * 0.72 + instant * 0.28;
        }

        Position = position;
        Target = position;
    }

    /// <summary>Suelta el orbe con la velocidad que traía.</summary>
    public void Drop()
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        IsFlying = true;

        // Soltarlo casi quieto no debería mandarlo a ningún lado: el resto lo hace el imán.
        if (Speed < 60)
        {
            Velocity *= 0.5;
        }
    }

    /// <summary>
    /// Avanza la simulación. <paramref name="bounds"/> es dónde puede estar la esquina del orbe.
    /// </summary>
    public void Step(double dt, Rect bounds)
    {
        if (IsDragging)
        {
            return;
        }

        var remaining = Math.Min(MaxFrame, dt);
        while (remaining > 0.0001)
        {
            var h = remaining > SubStep ? SubStep : remaining;
            remaining -= h;

            if (IsFlying)
            {
                StepFlight(h, bounds);
            }
            else
            {
                StepSettle(h, bounds);
            }
        }
    }

    private void StepFlight(double h, Rect bounds)
    {
        var friction = Math.Pow(FlyFriction, h);
        Position = new Point(Position.X + Velocity.X * h, Position.Y + Velocity.Y * h);
        Velocity = new Vector(Velocity.X * friction, Velocity.Y * friction);

        if (Position.X < bounds.Left)
        {
            Position = new Point(bounds.Left, Position.Y);
            Bounce(1, 0);
        }
        else if (Position.X > bounds.Right)
        {
            Position = new Point(bounds.Right, Position.Y);
            Bounce(-1, 0);
        }

        if (Position.Y < bounds.Top)
        {
            Position = new Point(Position.X, bounds.Top);
            Bounce(0, 1);
        }
        else if (Position.Y > bounds.Bottom)
        {
            Position = new Point(Position.X, bounds.Bottom);
            Bounce(0, -1);
        }

        if (Speed >= SettleSpeed)
        {
            return;
        }

        IsFlying = false;
        Velocity *= 0.5;
        Target = Magnetize(Position, bounds);
    }

    private void StepSettle(double h, Rect bounds)
    {
        Target = new Point(
            Math.Clamp(Target.X, bounds.Left, bounds.Right),
            Math.Clamp(Target.Y, bounds.Top, bounds.Bottom));

        Velocity = new Vector(
            Velocity.X + (RestStiffness * (Target.X - Position.X) - RestDamping * Velocity.X) * h,
            Velocity.Y + (RestStiffness * (Target.Y - Position.Y) - RestDamping * Velocity.Y) * h);

        Position = new Point(Position.X + Velocity.X * h, Position.Y + Velocity.Y * h);
    }

    /// <summary>
    /// El rebote devuelve entre el 14 % y el 46 % de la velocidad según con cuánta fuerza llegó: una
    /// pelota lanzada rebota, una apoyada no.
    /// </summary>
    private void Bounce(double nx, double ny)
    {
        var speed = Speed;
        var restitution = Math.Clamp(0.14 + speed / 3600, 0.14, 0.46);

        Velocity = nx != 0
            ? new Vector(Math.Abs(Velocity.X) * restitution * nx, Velocity.Y * 0.86)
            : new Vector(Velocity.X * 0.86, Math.Abs(Velocity.Y) * restitution * ny);

        Velocity = new Vector(
            Math.Abs(Velocity.X) < MinBounceSpeed ? 0 : Velocity.X,
            Math.Abs(Velocity.Y) < MinBounceSpeed ? 0 : Velocity.Y);
    }

    /// <summary>
    /// El imán: a menos de 58 px de un borde, el orbe termina pegado. Es lo que hace que quede
    /// prolijo sin pedirle puntería a nadie.
    /// </summary>
    private static Point Magnetize(Point position, Rect bounds)
    {
        var x = Math.Clamp(position.X, bounds.Left, bounds.Right);
        var y = Math.Clamp(position.Y, bounds.Top, bounds.Bottom);

        if (x < bounds.Left + MagnetRange)
        {
            x = bounds.Left;
        }
        else if (x > bounds.Right - MagnetRange)
        {
            x = bounds.Right;
        }

        if (y < bounds.Top + MagnetRange)
        {
            y = bounds.Top;
        }
        else if (y > bounds.Bottom - MagnetRange)
        {
            y = bounds.Bottom;
        }

        return new Point(x, y);
    }

    /// <summary>Ajusta la posición cuando cambia el área útil, sin pegar un salto.</summary>
    public void ClampInto(Rect bounds)
    {
        Position = new Point(
            Math.Clamp(Position.X, bounds.Left, bounds.Right),
            Math.Clamp(Position.Y, bounds.Top, bounds.Bottom));
        Target = new Point(
            Math.Clamp(Target.X, bounds.Left, bounds.Right),
            Math.Clamp(Target.Y, bounds.Top, bounds.Bottom));
    }
}
