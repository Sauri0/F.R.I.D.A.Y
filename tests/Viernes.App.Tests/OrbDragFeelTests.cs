using Viernes.App.Shell;
using Xunit;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// Cómo se siente el arrastre, y por qué el peso y el tiro son el mismo número.
/// </summary>
/// <remarks>
/// Este archivo existe porque el arrastre se «arregló» tres veces razonando y las tres quedó peor.
/// El usuario lo cerró así: «sigue pegada al mouse, antes hace unas versiones funcionaba, qué
/// hiciste para empeorarlo». Tenía razón, y el error de fondo fue uno solo.
/// <para>
/// Un resorte que persigue un objetivo que va a velocidad constante se mueve, en régimen,
/// <b>exactamente a esa velocidad</b>, quedándose atrás una distancia fija de c·v/k. O sea que
/// durante el arrastre la velocidad del resorte <em>es</em> la de la mano. Eso no es casualidad ni
/// un efecto lateral de que el resorte siga mal: es la definición de régimen. Yo lo tomé por
/// casualidad, endurecí el resorte a amortiguación crítica para que «siguiera mejor», y con eso
/// maté las dos cosas de un saque —el orbe quedó pegado al cursor y sin velocidad para salir—.
/// Después construí un aparato para medir el cursor aparte y devolverle el tiro: una copia peor de
/// lo que el resorte hacía solo. Arreglarlo fue borrar todo eso.
/// </para>
/// <para>
/// Lo que sí era un defecto de verdad —«se traba»— era que el objetivo se ponía desde el manejador
/// de <c>MouseMove</c>, que llega por la cola del mismo hilo que dibuja el cuerpo. Se arregló
/// leyendo el cursor en el bucle de cuadro, no acá: eso vive en la ventana y pide un mouse de
/// verdad. No toca ninguna constante, y por eso se pudo conservar.
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
    public void ElOrbeSeQuedaAtrasDeLaMano_QueEsElPeso()
    {
        // La demora ES el peso del orbe. Si alguien endurece el resorte hasta pegarlo al cursor,
        // esta prueba falla, y falla con razón: sin demora no hay peso y tampoco hay tiro.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz: 180);

        var alos30 = recorrido[(int)(0.030 * 180)];

        Assert.True(
            alos30.X < 800,
            $"Llegó demasiado rápido: {alos30.X:0.0} a los 30 ms, con 400 px por recorrer.");
    }

    [Fact]
    public void SePasaUnPocoDelCursorYVuelve_PeroNoLoOrbita()
    {
        // El sobrepaso es deliberado: ζ ≈ 0,64. Sin él el resorte no conserva envión cuando la mano
        // se detiene, que es de donde sale el tiro.
        //
        // Lo que hay que acotar es cuánto. Si fuera mucho más, ahí sí se leería como que orbita el
        // cursor en vez de colgar de él, que fue la queja original.
        var recorrido = Arrastrar(new Point(500, 500), new Point(900, 500), segundos: 1.5, hz: 180);

        var maximo = recorrido.Max(punto => punto.X);
        var sobrepaso = maximo - 900;

        Assert.True(sobrepaso > 1, $"No se pasó nada: máximo {maximo:0.0}. Sin sobrepaso no hay tiro.");
        Assert.True(sobrepaso < 40, $"Se pasó {sobrepaso:0.0} px sobre un tirón de 400: eso ya es orbitar.");
    }

    [Fact]
    public void LlegaAlCursorYSeQueda()
    {
        // Y termina llegando: un resorte demasiado amortiguado se arrastraría eternamente sin tocar
        // el objetivo, que se leería como que el orbe nunca termina de acomodarse.
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

    /// <summary>Un envión de 1400 px/s, después la mano quieta, y recién ahí se suelta.</summary>
    private static OrbMotion ArrojarConPausa(double pausaSegundos, double hz = 180)
    {
        var motion = new OrbMotion();
        motion.Teleport(new Point(300, 500));
        motion.BeginDrag();

        var paso = 1.0 / hz;
        var x = 300.0;
        for (var t = 0.0; t < 0.25; t += paso)
        {
            x += 1400 * paso;
            motion.DragTo(new Point(x, 500));
            motion.Step(paso, Pantalla);
        }

        for (var t = 0.0; t < pausaSegundos; t += paso)
        {
            motion.DragTo(new Point(x, 500));
            motion.Step(paso, Pantalla);
        }

        motion.Drop();
        return motion;
    }

    [Theory]
    [InlineData(0, 1200)]
    [InlineData(50, 1000)]
    [InlineData(100, 600)]
    [InlineData(150, 300)]
    public void ElTiroSobreviveALaPausaDeLevantarElDedo(int pausaMs, double minimo)
    {
        // Estos casos salen de trece tiros REALES del usuario, leídos de la bitácora: en seis de
        // trece el tiro salió en CERO, y en todas ésas el cursor estaba exactamente donde lo había
        // dejado el último cuadro. O sea que la mano ya estaba quieta al soltar. Cuánto dura esa
        // quietud no lo elige nadie: es lo que tarda un dedo en levantarse.
        //
        // Lo notable es que el resorte hace esto SOLO, sin ventana de tiempo ni decaimiento ni nada:
        // cuando la mano frena, el resorte venía atrás y sigue de largo. La curva de apagado que yo
        // escribí a mano —gracia de 80 ms y desvanecido de 320— era una aproximación grosera de una
        // exponencial que ya estaba ahí.
        var motion = ArrojarConPausa(pausaMs / 1000.0);

        Assert.True(
            motion.Speed > minimo,
            $"Con {pausaMs} ms de pausa salió a {motion.Speed:0} px/s de un envión de 1400.");
    }

    [Fact]
    public void UnaPausaLargaSiMataElTiro()
    {
        // El otro lado, y hace falta: apoyar el orbe en un rincón después de haberlo movido rápido
        // no puede dispararlo. «Lo dejé quieto» y «lo tiré» tienen que ser distinguibles.
        var motion = ArrojarConPausa(0.5);

        Assert.True(
            motion.Speed < 60,
            $"Salió disparado después de medio segundo quieto: {motion.Speed:0} px/s.");
    }

    [Fact]
    public void ElTiroSeApagaDeAPocoYNoDeGolpe()
    {
        // Que la pausa apague el tiro no alcanza: tiene que apagarlo GRADUALMENTE. Un corte —tira
        // entero hasta los 200 ms y cero a los 201— se sentiría como una lotería, porque nadie
        // controla al milisegundo cuánto tarda en levantar el dedo.
        var velocidades = new[] { 0, 50, 100, 150, 200, 300 }
            .Select(ms => ArrojarConPausa(ms / 1000.0).Speed)
            .ToArray();

        for (var i = 1; i < velocidades.Length; i++)
        {
            Assert.True(
                velocidades[i] < velocidades[i - 1],
                $"El tiro no bajó entre una pausa y la siguiente: {velocidades[i - 1]:0} → {velocidades[i]:0}.");
        }
    }

    [Fact]
    public void SeLoPuedeArrojar()
    {
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
    public void AtajarloEnPlenoVueloLoFrena()
    {
        // Atajar el orbe mientras vuela y sostenerlo quieto tiene que detenerlo: la mano manda sobre
        // la inercia. El objetivo se clava donde estaba el orbe al agarrarlo y NO se mueve con él
        // —seguirlo cuadro a cuadro sería un lazo, la mano persiguiendo lo que la mano causa—.
        //
        // No hay corte especial para esto: es el mismo resorte, sujetando en vez de tirando. Soltar
        // enseguida sí lo deja seguir de largo, y está bien: agarraste algo que se movía y lo
        // soltaste sin frenarlo.
        var motion = Arrojar(pxPorSegundo: 2600);
        for (var i = 0; i < 60; i++)
        {
            motion.Step(1.0 / 180, Pantalla);
        }

        var dondeLoAgarre = motion.Position;
        motion.BeginDrag();
        for (var i = 0; i < 90; i++)
        {
            motion.DragTo(dondeLoAgarre);
            motion.Step(1.0 / 180, Pantalla);
        }

        motion.Drop();

        Assert.True(motion.Speed < 60, $"Lo atajé medio segundo y salió igual a {motion.Speed:0} px/s.");
        Assert.True(
            (motion.Position - dondeLoAgarre).Length < 8,
            $"Se me escapó de la mano: quedó a {(motion.Position - dondeLoAgarre).Length:0} px de donde lo agarré.");
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
    public void ElTiroConPausaSaleIgualATodaFrecuencia(double hz)
    {
        // Y con la pausa de levantar el dedo también. Acá es donde se cayó el aparato anterior: el
        // promedio corrido tenía memoria POR CUADRO, así que a 180 Hz olvidaba el envión en tres
        // cuadros y a 60 Hz no. El resorte no tiene ese problema porque integra en segundos.
        var motion = ArrojarConPausa(0.100, hz);

        Assert.InRange(motion.Speed, 600, 1200);
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
        Assert.True(recorrido.Max(punto => punto.X) <= 940);
    }
}
