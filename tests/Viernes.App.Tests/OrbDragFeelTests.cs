using Viernes.App.Shell;
using Xunit;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// Cómo se siente el arrastre: se queda atrás de la mano, pero no la pasa.
/// </summary>
/// <remarks>
/// El usuario lo dijo así: «siento muy tosco el arrastre, 0 fluido, se traba a veces y como que no
/// sigue tanto el mouse, es medio raro». Eran dos cosas distintas y sólo una se ve en un número.
/// <para>
/// La otra —«se traba»— era que el objetivo del resorte se ponía desde el manejador de
/// <c>MouseMove</c>, que llega por la cola del mismo hilo que dibuja el cuerpo. Con la nube costando
/// unos 11 ms por cuadro los eventos se amontonaban, el resorte tiraba hacia un objetivo viejo
/// durante varios cuadros y después pegaba el tirón. Se arregló leyendo el cursor en el bucle de
/// cuadro, y eso no se puede probar acá: vive en la ventana y pide un mouse de verdad.
/// </para>
/// <para>
/// Lo que sí se prueba es el sobrepaso, que es lo que se lee como «medio raro»: un resorte
/// subamortiguado no cuelga del cursor, lo orbita.
/// </para>
/// </remarks>
public sealed class OrbDragFeelTests
{
    private static readonly Rect Pantalla = new(0, 0, 1920, 1080);

    /// <summary>Arrastra hasta un punto y devuelve todo el recorrido, cuadro por cuadro.</summary>
    private static List<Point> Arrastrar(Point desde, Point hasta, double segundos, double hz)
    {
        var motion = new OrbMotion();
        motion.Teleport(desde);
        motion.BeginDrag();
        motion.DragTo(hasta);

        var paso = 1.0 / hz;
        var recorrido = new List<Point>();
        for (var t = 0.0; t < segundos; t += paso)
        {
            motion.Step(paso, Pantalla);
            recorrido.Add(motion.Position);
        }

        return recorrido;
    }

    [Fact]
    public void ElOrbeNoSePasaDelCursorAlFrenar()
    {
        // El dedo se queda quieto en 900 y el orbe tiene que llegar ahí y quedarse, no cruzarlo.
        //
        // Con la amortiguación vieja (15,5 sobre rigidez 146, ζ ≈ 0,64) se pasaba unos 22 px y
        // volvía. Veintidós píxeles sobre un orbe de 108 es una quinta parte del cuerpo saliéndose
        // por el otro lado del cursor: eso es lo que se lee como que orbita en vez de colgar.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz: 180);

        var maximo = recorrido.Max(punto => punto.X);

        Assert.True(
            maximo <= 900.5,
            $"Se pasó del cursor: llegó a {maximo:0.0} con el objetivo en 900.");
    }

    [Fact]
    public void ElOrbeSeQuedaAtrasDeLaMano_QueEsElPeso()
    {
        // La otra mitad, y hace falta: si el arreglo del sobrepaso hubiera endurecido el resorte
        // hasta pegarlo al cursor, esta prueba fallaría y con razón. La demora ES el peso del orbe;
        // lo que sobraba era el rebote.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz: 180);

        // A los 30 ms de arrancar todavía tiene que faltarle un buen tramo.
        var alos30 = recorrido[(int)(0.030 * 180)];

        Assert.True(
            alos30.X < 800,
            $"Llegó demasiado rápido: {alos30.X:0.0} a los 30 ms, con 400 px por recorrer.");
    }

    [Fact]
    public void LlegaAlCursorAunqueNoLoPase()
    {
        // Y llega: un resorte demasiado amortiguado se arrastraría eternamente sin tocar el objetivo,
        // que se leería como que el orbe nunca termina de acomodarse.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz: 180);

        Assert.True(
            Math.Abs(recorrido[^1].X - 900) < 1,
            $"No llegó: quedó en {recorrido[^1].X:0.0} con el objetivo en 900.");
    }

    /// <summary>Arrastra moviendo la mano a velocidad pareja y suelta. Devuelve el orbe soltado.</summary>
    private static OrbMotion Arrojar(
        double pxPorSegundo,
        double hz = 180,
        double segundos = 0.25,
        double desde = 300)
    {
        var motion = new OrbMotion();
        motion.Teleport(new Point(desde, 500));
        motion.BeginDrag();

        var paso = 1.0 / hz;
        var x = desde;
        for (var t = 0.0; t < segundos; t += paso)
        {
            x += pxPorSegundo * paso;
            motion.DragTo(new Point(x, 500));
            motion.Step(paso, Pantalla);
        }

        motion.Drop();
        return motion;
    }

    [Fact]
    public void SeLoPuedeArrojar()
    {
        // El defecto que esto cierra: al soltar, el orbe salía con la velocidad del RESORTE. Con el
        // resorte flojo eso andaba de casualidad —iba tan atrás del cursor que siempre traía
        // inercia— y con la amortiguación crítica dejó de andar: el orbe ya estaba encima del
        // cursor, su velocidad interna era casi cero, y soltarlo lo dejaba caer ahí mismo.
        // «No lo puedo tirar.»
        var motion = Arrojar(pxPorSegundo: 1400);

        Assert.True(motion.IsFlying, "No quedó volando.");
        Assert.True(
            motion.Speed > 900,
            $"Salió demasiado despacio: {motion.Speed:0} px/s con la mano a 1400.");
    }

    [Fact]
    public void ArrojadoFuerteLlegaLejosYRebota()
    {
        // El circuito entero: sale, vuela, choca contra el borde y vuelve. Sin el rebote el orbe se
        // clavaría en el borde y el vuelo terminaría en un tope, no en un pique.
        //
        // Se tira desde cerca del borde a propósito. El rozamiento del vuelo es fuerte —pow(0,075;h),
        // o sea que en un segundo queda el 7,5 % de la velocidad— así que un tiro de 2600 px/s
        // recorre unos 930 px y no llega desde el otro lado de la pantalla. Eso no es un defecto: es
        // lo que hace que el orbe se detenga donde uno lo mandó y no siga de largo.
        var motion = Arrojar(pxPorSegundo: 2600, desde: 1200);

        var maximo = motion.Position.X;
        var rebotó = false;
        for (var i = 0; i < 400; i++)
        {
            motion.Step(1.0 / 180, Pantalla);
            maximo = Math.Max(maximo, motion.Position.X);

            // Volvió para atrás después de haber ido para adelante: eso es el rebote.
            if (maximo > 1000 && motion.Position.X < maximo - 20)
            {
                rebotó = true;
                break;
            }
        }

        Assert.True(maximo > 1000, $"No llegó lejos: máximo {maximo:0}.");
        Assert.True(rebotó, "Llegó al borde y no rebotó.");
    }

    [Fact]
    public void SoltarloQuietoNoLoMandaANingunLado()
    {
        var motion = new OrbMotion();
        motion.Teleport(new Point(500, 500));
        motion.BeginDrag();

        // La mano se queda donde está: unos cuadros sin mover nada.
        for (var i = 0; i < 30; i++)
        {
            motion.DragTo(new Point(500, 500));
            motion.Step(1.0 / 180, Pantalla);
        }

        motion.Drop();

        Assert.True(motion.Speed < 60, $"Salió disparado sin que nadie lo tirara: {motion.Speed:0} px/s.");
    }

    [Fact]
    public void AgarrarloNoLoDispara()
    {
        // El primer cuadro después de agarrarlo no puede derivar una velocidad entre el objetivo
        // viejo y el nuevo: son dos puntos que no tienen nada que ver.
        var motion = Arrojar(pxPorSegundo: 2600);
        for (var i = 0; i < 60; i++)
        {
            motion.Step(1.0 / 180, Pantalla);
        }

        motion.BeginDrag();
        motion.DragTo(motion.Position);
        motion.Step(1.0 / 180, Pantalla);
        motion.Drop();

        Assert.True(motion.Speed < 60, $"Agarrarlo y soltarlo lo disparó a {motion.Speed:0} px/s.");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(180)]
    [InlineData(240)]
    public void ElTiroSaleIgualATodaFrecuencia(double hz)
    {
        // La mano va a la misma velocidad; el orbe tiene que salir con la misma, mida los cuadros
        // que mida la pantalla.
        var motion = Arrojar(pxPorSegundo: 1400, hz);

        Assert.InRange(motion.Speed, 900, 1700);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(180)]
    [InlineData(240)]
    public void ElArrastreSeSienteIgualATodaFrecuencia(double hz)
    {
        // El pedido del usuario incluía «debe estar adaptado a toda frecuencia». Con el subpaso como
        // techo, el mismo arrastre tiene que terminar en el mismo lugar a cualquier cadencia.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz);

        Assert.True(Math.Abs(recorrido[^1].X - 900) < 1);
        Assert.True(recorrido.Max(punto => punto.X) <= 900.5);
    }
}
