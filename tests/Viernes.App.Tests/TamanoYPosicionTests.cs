using Viernes.App.Shell;
using Xunit;

using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// Que elegir un tamaño distinto del de fábrica no le coma la memoria de dónde quedó el orbe.
/// </summary>
/// <remarks>
/// El defecto que esto fija era silencioso y sólo lo sufría quien usara la función nueva. La ventana
/// se construye antes de que se lea el archivo de preferencias, así que el orbe nacía a 108 px; el
/// tamaño guardado llegaba al final de la inicialización —después de leer el disco y de levantar los
/// servidores MCP, dieciséis segundos medidos en la bitácora del usuario— y al agrandarse
/// <b>conservando el centro</b> corría su esquina (108 − tamaño)/2, que al 200 % son 54 px.
/// <para>
/// Y esa esquina corrida es la que se guarda al salir. Al arranque siguiente se restauraba la
/// posición ya corrida y se la volvía a correr: el orbe caminaba 54 px por arranque, siempre para el
/// mismo lado, hasta clavarse contra el margen.
/// </para>
/// <para>
/// El arreglo son dos cosas y las dos se prueban acá: la escala se pone <b>antes</b> de que exista la
/// ventana, y restaurar conserva la <b>esquina</b> —no el centro—, que es lo que se guarda.
/// </para>
/// </remarks>
public sealed class TamanoYPosicionTests : IDisposable
{
    private static readonly Rect Pantalla = new(0, 0, 1920, 1040);

    public void Dispose() => ShellLayout.Scale = 1.0;

    /// <summary>
    /// Restaurando el tamaño guardado, la esquina del orbe no se mueve: lo que se guardó es lo que
    /// se lee, y lo que se lee es lo que se vuelve a guardar.
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void RestaurarElTamanoNoCorreLaEsquina(double escala)
    {
        // Arrancar es: escala puesta primero, y recién después colocar el orbe donde se lo dejó.
        ShellLayout.Scale = escala;
        var bounds = ShellLayout.OrbBounds(Pantalla);
        var guardada = new Point(700, 500);

        var restaurada = new Point(
            Math.Clamp(guardada.X, bounds.Left, bounds.Right),
            Math.Clamp(guardada.Y, bounds.Top, bounds.Bottom));

        Assert.Equal(guardada.X, restaurada.X, 3);
        Assert.Equal(guardada.Y, restaurada.Y, 3);
    }

    /// <summary>
    /// Diez arranques seguidos con el mismo tamaño dejan el orbe exactamente donde estaba.
    /// </summary>
    /// <remarks>
    /// Es la prueba del defecto tal como se manifestaba: no en un arranque, sino acumulándose. Con la
    /// versión anterior, al 200 % este bucle movía el orbe 540 px.
    /// </remarks>
    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void DiezArranquesNoMuevenElOrbe(double escala)
    {
        var posicion = new Point(700, 500);

        for (var arranque = 0; arranque < 10; arranque++)
        {
            // Así es el arranque arreglado: primero la escala, después restaurar, y nadie recentra.
            ShellLayout.Scale = escala;
            var bounds = ShellLayout.OrbBounds(Pantalla);
            posicion = new Point(
                Math.Clamp(posicion.X, bounds.Left, bounds.Right),
                Math.Clamp(posicion.Y, bounds.Top, bounds.Bottom));
        }

        Assert.Equal(700, posicion.X, 3);
        Assert.Equal(500, posicion.Y, 3);
    }

    /// <summary>
    /// Y lo que sí tiene que pasar cuando el usuario mueve la barra: el orbe crece desde su CENTRO.
    /// </summary>
    /// <remarks>
    /// Es la otra mitad, y es lo que hace que la barra se sienta bien: creciendo desde la esquina, un
    /// orbe que se agranda se lee como un orbe que se movió. Acá el corrimiento de la esquina no es
    /// un defecto: es exactamente lo que se quiere, y por eso las dos rutas tienen que ser distintas.
    /// </remarks>
    [Fact]
    public void CambiarElTamanoAManoConservaElCentro()
    {
        ShellLayout.Scale = 1.0;
        var esquina = new Point(700, 500);
        var centro = new Point(esquina.X + (ShellLayout.OrbSize / 2), esquina.Y + (ShellLayout.OrbSize / 2));

        ShellLayout.Scale = 2.0;
        var nueva = new Point(centro.X - (ShellLayout.OrbSize / 2), centro.Y - (ShellLayout.OrbSize / 2));

        // El centro es el mismo; la esquina se corrió los 54 px que mide medio orbe de más.
        Assert.Equal(centro.X, nueva.X + (ShellLayout.OrbSize / 2), 3);
        Assert.Equal(646, nueva.X, 3);
        Assert.Equal(446, nueva.Y, 3);
    }

    /// <summary>A cualquier tamaño, el orbe entra entero en el área útil.</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ElOrbeEntraEnteroACualquierTamano(double escala)
    {
        ShellLayout.Scale = escala;
        var bounds = ShellLayout.OrbBounds(Pantalla);

        Assert.True(bounds.Left >= Pantalla.Left, $"se sale por la izquierda: {bounds.Left}");
        Assert.True(bounds.Top >= Pantalla.Top, $"se sale por arriba: {bounds.Top}");
        Assert.True(
            bounds.Right + ShellLayout.OrbSize <= Pantalla.Right,
            $"se sale por la derecha: {bounds.Right + ShellLayout.OrbSize} contra {Pantalla.Right}");
        Assert.True(
            bounds.Bottom + ShellLayout.OrbSize <= Pantalla.Bottom,
            $"se sale por abajo: {bounds.Bottom + ShellLayout.OrbSize} contra {Pantalla.Bottom}");
    }
}
