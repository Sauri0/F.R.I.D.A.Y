using System.Windows;
using Viernes.App.Shell;
using Xunit;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// El borde que une dos pantallas no es una pared, y arriba y abajo del tramo compartido sí lo es.
/// </summary>
/// <remarks>
/// Estos casos no se pueden comprobar corriendo la aplicación en la máquina donde se escribió: ahí
/// hay dos monitores de 1920×1080 pegados de costado, que comparten el borde <em>entero</em>. Dos
/// pantallas de distinta altura y un hueco en el escritorio virtual hay que inventarlos.
/// <para>
/// Las medidas van en unidades de WPF. El orbe mide 108 y su margen contra el borde es 20, así que
/// en un monitor de 1920 de ancho la esquina del orbe va de <c>left+20</c> a <c>left+1792</c>.
/// </para>
/// </remarks>
public class DesktopFieldTests
{
    private const double OrbSize = 108;
    private const double Margin = 20;

    /// <summary>La izquierda en negativo y la derecha en cero, como las dos pantallas del usuario.</summary>
    private static DesktopField SideBySide() => DesktopField.Of(
        ("izquierda", new Rect(-1920, 0, 1920, 1040)),
        ("derecha", new Rect(0, 0, 1920, 1040)));

    [Fact]
    public void ElBordeCompartidoSeAbreHaciaLaIzquierda()
    {
        var field = SideBySide();
        var right = new Rect(0, 0, 1920, 1040);

        // El orbe a media altura, en la pantalla de la derecha.
        var reach = field.Reach(right, new Point(200, 400));

        // Sin vecino, el límite izquierdo sería 0 + 20. Con el vecino abierto llega hasta el margen
        // izquierdo de la otra pantalla.
        Assert.Equal(-1920 + Margin, reach.Left, 3);

        // El otro lado no da a ninguna parte: sigue siendo pared.
        Assert.Equal(1920 - OrbSize - Margin, reach.Right, 3);
    }

    /// <summary>Desde la pantalla de la izquierda: el borde de afuera es pared y el de adentro no.</summary>
    [Fact]
    public void DesdeLaOtraPantallaElBordeDeAfueraSigueSiendoPared()
    {
        var field = SideBySide();
        var left = new Rect(-1920, 0, 1920, 1040);

        var reach = field.Reach(left, new Point(-1800, 400));

        // Pared: a la izquierda de la pantalla izquierda no hay nada.
        Assert.Equal(-1920 + Margin, reach.Left, 3);

        // Abierto: por la derecha se pasa a la otra, hasta su margen del otro extremo.
        Assert.Equal(1920 - OrbSize - Margin, reach.Right, 3);
    }

    [Fact]
    public void SinVecinoNoSeAbreNada()
    {
        var field = DesktopField.Of(("sola", new Rect(0, 0, 1920, 1040)));
        var reach = field.Reach(new Rect(0, 0, 1920, 1040), new Point(200, 400));

        Assert.Equal(Margin, reach.Left, 3);
        Assert.Equal(1920 - OrbSize - Margin, reach.Right, 3);
        Assert.Equal(Margin, reach.Top, 3);
        Assert.Equal(1040 - OrbSize - Margin, reach.Bottom, 3);
    }

    [Fact]
    public void UnHuecoEnElEscritorioVirtualNoSeCruza()
    {
        // Las dos pantallas no se tocan: entre −1920+1900 y 0 hay 20 px de nada.
        var field = DesktopField.Of(
            ("izquierda", new Rect(-1920, 0, 1900, 1040)),
            ("derecha", new Rect(0, 0, 1920, 1040)));

        var reach = field.Reach(new Rect(0, 0, 1920, 1040), new Point(200, 400));

        Assert.Equal(Margin, reach.Left, 3);
    }

    /// <summary>
    /// Dos monitores de distinta altura: a la altura del tramo compartido se pasa, y arriba no.
    /// </summary>
    /// <remarks>
    /// La chica va de y=0 a y=800 y la grande de y=0 a y=1040. Con el orbe a y=900 el vecino chico
    /// ya no existe: ahí hay pared aunque el borde sea el mismo borde.
    /// </remarks>
    [Fact]
    public void ConAlturasDistintasElBordeSeAbreSoloEnElTramoCompartido()
    {
        var field = DesktopField.Of(
            ("chica", new Rect(-1280, 0, 1280, 800)),
            ("grande", new Rect(0, 0, 1920, 1040)));
        var big = new Rect(0, 0, 1920, 1040);

        // A media altura de la chica: se pasa.
        var open = field.Reach(big, new Point(200, 300));
        Assert.Equal(-1280 + Margin, open.Left, 3);

        // Más abajo de donde termina la chica: pared.
        var closed = field.Reach(big, new Point(200, 900));
        Assert.Equal(Margin, closed.Left, 3);
    }

    /// <summary>
    /// El límite del tramo compartido es la <em>celda</em> del vecino, no su área útil.
    /// </summary>
    /// <remarks>
    /// La celda tiene el margen y el tamaño del orbe descontados: la chica termina en y=800, así que
    /// su celda llega hasta 800−108−20 = 672. Con el orbe en y=700 el borde ya está cerrado aunque
    /// el área útil del vecino llegue hasta 800. Cruzar ahí dejaría al orbe en un lugar donde no
    /// podría haber llegado caminando, y el recorte del cuadro siguiente lo subiría de un salto.
    /// </remarks>
    [Fact]
    public void ElTramoCompartidoTerminaDondeTerminaLaCeldaDelVecino()
    {
        var field = DesktopField.Of(
            ("chica", new Rect(-1280, 0, 1280, 800)),
            ("grande", new Rect(0, 0, 1920, 1040)));
        var big = new Rect(0, 0, 1920, 1040);

        Assert.Equal(-1280 + Margin, field.Reach(big, new Point(200, 672)).Left, 3);
        Assert.Equal(Margin, field.Reach(big, new Point(200, 673)).Left, 3);
    }

    /// <summary>Un vecino arriba abre el borde de arriba, con el mismo criterio en el otro eje.</summary>
    [Fact]
    public void UnVecinoApiladoAbreElBordeDeArriba()
    {
        var field = DesktopField.Of(
            ("arriba", new Rect(0, -1040, 1920, 1040)),
            ("abajo", new Rect(0, 0, 1920, 1040)));
        var below = new Rect(0, 0, 1920, 1040);

        var reach = field.Reach(below, new Point(400, 300));

        Assert.Equal(-1040 + Margin, reach.Top, 3);
        Assert.Equal(1040 - OrbSize - Margin, reach.Bottom, 3);
    }

    /// <summary>
    /// Ya metido en la costura, el alto se recorta a lo que las dos celdas tienen en común.
    /// </summary>
    /// <remarks>
    /// Sin esto, un orbe cruzando en diagonal entre dos pantallas de distinta altura podría salirse
    /// del tramo compartido mientras cruza y aparecer del otro lado pegando un salto vertical.
    /// </remarks>
    [Fact]
    public void EnLaCosturaElAltoSeRecortaALoComun()
    {
        var field = DesktopField.Of(
            ("chica", new Rect(-1280, 0, 1280, 800)),
            ("grande", new Rect(0, 0, 1920, 1040)));
        var big = new Rect(0, 0, 1920, 1040);

        // Todavía en su propia celda: el alto es el de la pantalla grande.
        var before = field.Reach(big, new Point(200, 400));
        Assert.Equal(1040 - OrbSize - Margin, before.Bottom, 3);

        // Ya pasado el borde de su celda —la costura—: el alto es el de la chica.
        var inside = field.Reach(big, new Point(-30, 400));
        Assert.Equal(800 - OrbSize - Margin, inside.Bottom, 3);
    }

    /// <summary>El monitor se decide con el centro del orbe y no con su esquina.</summary>
    [Fact]
    public void LaPantallaSeDecideConElCentroDelOrbe()
    {
        var field = SideBySide();

        // Esquina en −40: el orbe va de −40 a 68 y su centro cae en 14, o sea a la derecha.
        Assert.Equal("derecha", field.KeyAt(new Point(-40, 400)));

        // Esquina en −70: el centro cae en −16, a la izquierda.
        Assert.Equal("izquierda", field.KeyAt(new Point(-70, 400)));
    }

    /// <summary>Con el centro en un hueco del escritorio virtual gana la pantalla más cercana.</summary>
    [Fact]
    public void EnUnHuecoGanaLaPantallaMasCercana()
    {
        var field = DesktopField.Of(
            ("izquierda", new Rect(-1920, 0, 1000, 1040)),
            ("derecha", new Rect(0, 0, 1920, 1040)));

        // Los dos centros caen en el hueco, entre −920 y 0. Gana el borde que tienen más cerca.
        Assert.Equal("izquierda", field.KeyAt(new Point(-900, 400)));
        Assert.Equal("derecha", field.KeyAt(new Point(-100, 400)));
    }

    /// <summary>
    /// Apoyado contra el borde de abajo, la costura tiene que seguir abierta.
    /// </summary>
    /// <remarks>
    /// Este es el defecto que hacía que un tiro hacia la otra pantalla saliera <b>para el lado
    /// contrario</b>, y se veía como «no lo puedo tirar».
    /// <para>
    /// Durante el arrastre nadie recortaba la posición del orbe, así que al soltarlo podía tener una
    /// Y por debajo del límite legal —952 en una pantalla de 1080—. Con esa Y cruda, la prueba de
    /// «¿el vecino existe a esta altura?» no daba, la costura se cerraba y volvía a ser pared. El
    /// orbe chocaba contra ella y rebotaba con restitución 0,46: lo tirabas a la derecha y salía a la
    /// izquierda. Medido con la geometría del usuario, soltado a 1200 px/s hacia la derecha, terminó
    /// 223 px a la izquierda.
    /// </para>
    /// <para>
    /// Es exactamente la queja que <c>DesktopField</c> existe para arreglar —«no debería rebotar en
    /// el borde que une las dos pantallas»— volviendo por la puerta de atrás.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(400)]      // A media altura: siempre anduvo.
    [InlineData(1020 - OrbSize - Margin)]  // Justo en el límite legal de abajo.
    [InlineData(1000)]     // Pasado el límite, que es donde lo deja el arrastre.
    [InlineData(1200)]     // Bien pasado.
    [InlineData(-50)]      // Y del otro lado también.
    public void LaCosturaSigueAbiertaAunqueElOrbeEsteFueraDeLosLimites(double y)
    {
        var field = SideBySide();
        var right = new Rect(0, 0, 1920, 1040);

        var reach = field.Reach(right, new Point(200, y));

        Assert.Equal(-1920 + Margin, reach.Left, 3);
    }

    /// <summary>Y el borde que no da a ninguna parte sigue siendo pared, esté donde esté el orbe.</summary>
    [Theory]
    [InlineData(400)]
    [InlineData(1200)]
    [InlineData(-50)]
    public void ElBordeDeAfueraSigueSiendoParedAunqueElOrbeEsteFueraDeLosLimites(double y)
    {
        var field = SideBySide();
        var right = new Rect(0, 0, 1920, 1040);

        var reach = field.Reach(right, new Point(200, y));

        Assert.Equal(1920 - OrbSize - Margin, reach.Right, 3);
    }
}
