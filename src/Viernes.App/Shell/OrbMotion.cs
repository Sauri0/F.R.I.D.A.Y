using System.Windows;
using Viernes.App.Controls;
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

    /// <summary>Por debajo de esta rapidez el rebote no le llega al cuerpo.</summary>
    /// <remarks>
    /// Del fuente: <c>if (sp > 220) { M.hit = … }</c>. Apoyarse contra un borde no es un choque, y
    /// un achatamiento cada vez que el imán termina de acomodar el orbe se leería como un tic.
    /// </remarks>
    private const double HitSpeed = 220;

    /// <summary>Cuánto pesa la lectura vieja en el promedio corrido de la velocidad.</summary>
    /// <remarks>
    /// 0,72 lo viejo y 0,28 lo nuevo, del fuente. Sin suavizar, un solo cuadro largo manda la estela
    /// a cualquier parte y el cuerpo pega un tirón que no corresponde a ningún movimiento real.
    /// </remarks>
    private const double VelocityMemory = 0.72;

    /// <summary>Piso del intervalo al derivar la velocidad. Un dt chico inventa velocidades.</summary>
    private const double MinVelocitySpan = 0.008;

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

    /// <summary>Rapidez instantánea del integrador. Es la que decide si sigue volando y cuánto rebota.</summary>
    public double Speed => Velocity.Length;

    /// <summary>
    /// Velocidad de la ventana suavizada, en px/s. Es la que ve el cuerpo.
    /// </summary>
    /// <remarks>
    /// No es <see cref="Velocity"/>. Aquélla es el estado interno del integrador —durante el
    /// arrastre es la velocidad del resorte, no la del orbe en pantalla— y da saltos entre subpasos.
    /// Ésta se mide sobre el desplazamiento real del cuadro completo, que es lo único que el ojo vio.
    /// </remarks>
    public Vector Smoothed { get; private set; }

    /// <summary>Rapidez suavizada. Es la que decide si el vidrio se retrae.</summary>
    public double SmoothSpeed => Smoothed.Length;

    private int _hitToken;
    private double _hitNormalX;
    private double _hitNormalY;
    private double _hitStrength;

    /// <summary>Lo que hay que contarle al cuerpo en este cuadro.</summary>
    public OrbMotionSample Sample => new(
        Smoothed.X,
        Smoothed.Y,
        IsDragging,
        _hitToken,
        _hitNormalX,
        _hitNormalY,
        _hitStrength);

    /// <summary>Deja el orbe donde se lo pone, sin inercia ni destino pendiente.</summary>
    public void Teleport(Point position)
    {
        Position = position;
        Target = position;
        Velocity = default;
        Smoothed = default;
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

    /// <summary>
    /// Lo lanza hacia un destino con un empujón, en vez de llevarlo con el resorte.
    /// </summary>
    /// <remarks>
    /// Es el viaje entre monitores vecinos. La diferencia con <see cref="Nudge"/> no es de velocidad
    /// sino de tipo de movimiento: el resorte de reposo <em>tira</em> del orbe y llega frenando; esto
    /// lo <em>suelta</em> y llega con la inercia que le quedó, rebotando contra el borde si sobró.
    /// Un objeto que cruza una pantalla tirado por un resorte se ve arrastrado por un hilo; tirado de
    /// un envión, se ve viajando.
    /// <para>
    /// El empuje es proporcional a la distancia —no una velocidad fija— así que cruzar una pantalla
    /// chica y una grande tarda parecido.
    /// </para>
    /// </remarks>
    /// <param name="target">Dónde tiene que terminar.</param>
    /// <param name="kick">Cuánto empuje horizontal por píxel de distancia.</param>
    /// <param name="lift">Empujón vertical del despegue. Negativo arquea hacia arriba.</param>
    public void Launch(Point target, double kick, double lift)
    {
        IsDragging = false;
        IsFlying = true;
        Target = target;
        Velocity = new Vector((target.X - Position.X) * kick, lift);
    }

    /// <summary>Empieza el arrastre. Desde acá el destino lo manda el puntero.</summary>
    /// <remarks>
    /// El destino arranca donde está el orbe y no donde está el dedo: si arrancara en el dedo, el
    /// primer cuadro del arrastre sería un salto de todo el radio del orbe.
    /// </remarks>
    public void BeginDrag()
    {
        IsDragging = true;
        IsFlying = false;
        Target = Position;
    }

    /// <summary>
    /// Mueve el <em>objetivo</em> del arrastre. El orbe llega ahí por el resorte, no de un salto.
    /// </summary>
    /// <remarks>
    /// Acá estaba <c>ReportDragged</c>, que anotaba la posición que Windows le había impuesto a la
    /// ventana con <c>DragMove()</c> y estimaba la velocidad para poder soltarla. Mientras existió,
    /// el resorte de arrastre 146/15,5 nunca corrió: el tick llamaba a <c>ReportDragged</c> y jamás a
    /// <see cref="Step"/>. Y <c>DragMove()</c> clavaba la ventana al cursor, así que el orbe no podía
    /// quedarse atrás de la mano —que es exactamente lo que le da peso—.
    /// </remarks>
    public void DragTo(Point target)
    {
        if (!IsDragging)
        {
            return;
        }

        Target = target;
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
        var previous = Position;

        var remaining = Math.Min(MaxFrame, dt);
        while (remaining > 0.0001)
        {
            var h = remaining > SubStep ? SubStep : remaining;
            remaining -= h;

            if (IsDragging)
            {
                StepDrag(h);
            }
            else if (IsFlying)
            {
                StepFlight(h, bounds);
            }
            else
            {
                StepSettle(h, bounds);
            }
        }

        // La velocidad que ve el cuerpo se mide una vez por cuadro sobre el desplazamiento entero, y
        // no subpaso a subpaso: el ojo vio el cuadro, no los doce subpasos de adentro.
        var span = Math.Max(MinVelocitySpan, dt);
        Smoothed = new Vector(
            (Smoothed.X * VelocityMemory) + ((Position.X - previous.X) / span * (1 - VelocityMemory)),
            (Smoothed.Y * VelocityMemory) + ((Position.Y - previous.Y) / span * (1 - VelocityMemory)));
    }

    /// <summary>
    /// El resorte del arrastre: duro, para seguir al dedo, pero resorte al fin.
    /// </summary>
    /// <remarks>
    /// Es más duro que el de reposo —146 contra 104— porque tiene que alcanzar la mano; y aun así se
    /// queda atrás al arrancar y se pasa un poco al frenar. Esa demora <em>es</em> el peso del orbe.
    /// Con <c>DragMove()</c> no había forma de tenerla: Windows pega la ventana al cursor.
    /// </remarks>
    private void StepDrag(double h)
    {
        Velocity = new Vector(
            Velocity.X + ((DragStiffness * (Target.X - Position.X)) - (DragDamping * Velocity.X)) * h,
            Velocity.Y + ((DragStiffness * (Target.Y - Position.Y)) - (DragDamping * Velocity.Y)) * h);

        Position = new Point(Position.X + (Velocity.X * h), Position.Y + (Velocity.Y * h));
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

        // Recién acá el golpe deja de ser un asunto de la ventana. Antes la ventana rebotaba y nadie
        // le avisaba a la gota que había chocado: el rebote existía y no se veía.
        if (speed <= HitSpeed)
        {
            return;
        }

        _hitToken++;
        _hitNormalX = nx;
        _hitNormalY = ny;
        _hitStrength = Math.Min(1, speed / OrbMotionSample.SpeedReference);
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
