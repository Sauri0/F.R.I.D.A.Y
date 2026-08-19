using System.Windows;
using Viernes.App.Shell;
using Xunit;

// El proyecto arrastra WinForms por la bandeja y los monitores: Point y Rect existen dos veces.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// La física con los límites abiertos: el orbe tirado contra la costura pasa, y contra el borde de
/// afuera rebota.
/// </summary>
/// <remarks>
/// Se integra igual que lo hace la ventana —el mismo <see cref="OrbMotion.Step"/> con los mismos
/// límites que le pasaría <c>MainWindow</c>— pero con un dt fijo, que acá es lo correcto: lo que se
/// prueba es la geometría, no la fluidez, y un dt que dependa de la máquina haría que la prueba
/// contestara distinto según lo ocupada que esté.
/// </remarks>
public class OrbMotionCrossingTests
{
    private static readonly Rect Left = new(-1920, 0, 1920, 1040);
    private static readonly Rect Right = new(0, 0, 1920, 1040);

    private static DesktopField Field() => DesktopField.Of(("izquierda", Left), ("derecha", Right));

    /// <summary>
    /// Corre el vuelo un rato, recalculando los límites por cuadro como hace la ventana.
    /// </summary>
    /// <param name="seconds">Cuánto se lo deja volar.</param>
    private static Point Fly(OrbMotion motion, DesktopField field, double seconds)
    {
        const double Step = 1.0 / 120;
        var work = Right;

        for (var t = 0.0; t < seconds; t += Step)
        {
            // Igual que MainWindow: el área útil es la del monitor donde está el centro del orbe.
            work = field.KeyAt(motion.Position) == "izquierda" ? Left : Right;
            var bounds = field.Reach(work, motion.Position);
            motion.ClampInto(bounds);
            motion.Step(Step, bounds);
        }

        return motion.Position;
    }

    [Fact]
    public void TiradoContraLaCosturaPasaALaOtraPantalla()
    {
        var motion = new OrbMotion();
        motion.Teleport(new Point(400, 400));

        // Un envión hacia la izquierda, del orden del que da un arrastre soltado con ganas.
        motion.Launch(new Point(-600, 400), kick: 3.4, lift: 0);

        var end = Fly(motion, Field(), 3.0);

        Assert.Equal("izquierda", Field().KeyAt(end));
        Assert.True(end.X < 0, $"debería haber cruzado, quedó en {end.X:0}");
    }

    [Fact]
    public void TiradoContraElBordeDeAfueraRebota()
    {
        var motion = new OrbMotion();
        motion.Teleport(new Point(1600, 400));

        // Hacia la derecha, contra el borde que no da a ninguna parte.
        motion.Launch(new Point(3000, 400), kick: 3.4, lift: 0);

        var end = Fly(motion, Field(), 3.0);

        Assert.Equal("derecha", Field().KeyAt(end));

        // Nunca pasa del margen: el borde de afuera lo frena y el imán lo deja apoyado.
        Assert.True(end.X <= 1920 - 108 - 20 + 0.5, $"se salió de la pantalla: {end.X:0}");
    }

    /// <summary>
    /// Con un hueco en el escritorio virtual, el mismo tiro rebota en vez de cruzar.
    /// </summary>
    /// <remarks>
    /// Es la comprobación de que «abierto» no es una propiedad del borde sino de la geometría: acá
    /// las dos pantallas están del mismo lado y a la misma altura que en la prueba de arriba, y lo
    /// único que cambia es que no se tocan.
    /// </remarks>
    [Fact]
    public void ConUnHuecoNoCruzaYSeQuedaDeEsteLado()
    {
        var field = DesktopField.Of(
            ("izquierda", new Rect(-1920, 0, 1900, 1040)),
            ("derecha", Right));

        var motion = new OrbMotion();
        motion.Teleport(new Point(400, 400));
        motion.Launch(new Point(-600, 400), kick: 3.4, lift: 0);

        var end = Fly(motion, field, 3.0);

        Assert.Equal("derecha", field.KeyAt(end));
        Assert.True(end.X >= 20 - 0.5, $"se metió en el hueco: {end.X:0}");
    }
}
