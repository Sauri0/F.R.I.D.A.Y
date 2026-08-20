using System.Windows;
using Viernes.App.Shell;
using Viernes.Core.Configuration;
using Xunit;

using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Viernes.App.Tests;

/// <summary>
/// El orbe cambia de tamaño y nada más que él cambia de tamaño.
/// </summary>
/// <remarks>
/// Las tres cosas que se comprueban acá son las tres que se pueden romper sin que se note hasta que
/// alguien mueve la barra hasta un extremo: que el alto de la ventana no se mueva —de eso depende
/// que ningún desplegable cambie de lugar—, que el solape de 8 px entre el cuerpo y su vidrio siga
/// siendo 8 a cualquier escala, y que un orbe grande siga entrando entero en la pantalla.
/// <para>
/// <see cref="ShellLayout.Scale"/> es estático, así que cada prueba lo deja como lo encontró: por
/// eso la clase es <see cref="IDisposable"/>. Y por eso el ensamblado corre sin paralelismo, ver
/// <c>AssemblyInfo.cs</c>.
/// </para>
/// </remarks>
public sealed class TamanoDelOrbeTests : IDisposable
{
    /// <summary>El margen del orbe contra el borde de la pantalla, de <see cref="OrbMotion"/>.</summary>
    private const double Margin = 20;

    private static readonly Rect Pantalla = new(0, 0, 1920, 1040);

    private readonly double _escalaOriginal = ShellLayout.Scale;

    public void Dispose() => ShellLayout.Scale = _escalaOriginal;

    /// <summary>Las dos puntas del rango y el tamaño de fábrica en el medio.</summary>
    public static TheoryData<double> Escalas()
    {
        var escalas = new TheoryData<double>();
        foreach (var escala in new[] { 0.5, 0.75, 1.0, 1.5, 2.0 })
        {
            escalas.Add(escala);
        }

        return escalas;
    }

    [Fact]
    public void AlCienPorCientoTodoMideLoQueMedíaAntesDeQueEstoExistiera()
    {
        ShellLayout.Scale = 1.0;

        Assert.Equal(108, ShellLayout.OrbSize, 3);
        Assert.Equal(100, ShellLayout.PanelReach, 3);
        Assert.Equal(528, ShellLayout.WindowWidth, 3);
        Assert.Equal(272, ShellLayout.WindowHeight, 3);
        Assert.Equal(82, ShellLayout.OrbTop, 3);
    }

    /// <summary>
    /// El alto de la ventana no se mueve en todo el rango, y ése es el argumento del tope de 200 %.
    /// </summary>
    /// <remarks>
    /// El desplegable más alto mide 220 y el orbe al doble mide 216: entra por cuatro píxeles. Con
    /// un tope más alto, el alto útil pasaría a salir del orbe y la ventana tendría que crecer y
    /// recolocarse también en vertical.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void ElAltoDeLaVentanaNoCambiaConElTamañoDelOrbe(double escala)
    {
        ShellLayout.Scale = 1.0;
        var altoDeFabrica = ShellLayout.WindowHeight;

        ShellLayout.Scale = escala;

        Assert.Equal(altoDeFabrica, ShellLayout.WindowHeight, 3);
        Assert.True(
            ShellLayout.OrbSize <= ShellLayout.ContentHeight,
            $"Al {escala * 100:0} % el orbe mide {ShellLayout.OrbSize} y el alto útil es {ShellLayout.ContentHeight}.");
    }

    /// <summary>El orbe sigue centrado en el alto de la ventana, mida lo que mida.</summary>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void ElOrbeQuedaCentradoEnElAltoDeLaVentana(double escala)
    {
        ShellLayout.Scale = escala;

        Assert.Equal(
            ShellLayout.WindowHeight / 2,
            ShellLayout.OrbTop + (ShellLayout.OrbSize / 2),
            3);
    }

    /// <summary>
    /// El vidrio se mete 8 px por debajo del cuerpo, abra para donde abra y mida lo que mida.
    /// </summary>
    /// <remarks>
    /// Es la comprobación que justifica que <c>PanelReach</c> haya dejado de ser una constante. Con
    /// los 100 px de antes, al 50 % quedaba un hueco de 46 px entre el orbe y su desplegable y al
    /// 200 % el orbe le tapaba 116 px.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void ElSolapeEntreElOrbeYSuVidrioSonSiempreOchoPixeles(double escala)
    {
        ShellLayout.Scale = escala;

        var bordeDerechoDelOrbe = ShellLayout.OrbLeftWhenOpeningRight + ShellLayout.OrbSize;
        Assert.Equal(ShellLayout.PanelOverlap, bordeDerechoDelOrbe - ShellLayout.PanelHostLeft(true), 3);

        var bordeDerechoDelPanel = ShellLayout.PanelHostLeft(false) + PanelCatalog.MaxWidth;
        Assert.Equal(ShellLayout.PanelOverlap, bordeDerechoDelPanel - ShellLayout.OrbLeftWhenOpeningLeft, 3);
    }

    /// <summary>El ancho de la ventana crece exactamente lo que crece el orbe, y nada más.</summary>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void LaVentanaSeEnsanchaLoMismoQueElOrbe(double escala)
    {
        ShellLayout.Scale = 1.0;
        var anchoDeFabrica = ShellLayout.WindowWidth;

        ShellLayout.Scale = escala;

        Assert.Equal(
            anchoDeFabrica + (ShellLayout.OrbSize - ShellLayout.DefaultOrbSize),
            ShellLayout.WindowWidth,
            3);
    }

    /// <summary>
    /// A cualquier tamaño el orbe entra entero en el área útil, con su margen de los cuatro lados.
    /// </summary>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void ElOrbeEntraEnteroEnLaPantallaACualquierTamaño(double escala)
    {
        ShellLayout.Scale = escala;
        var alcance = ShellLayout.OrbBounds(Pantalla);

        Assert.Equal(Pantalla.Left + Margin, alcance.Left, 3);
        Assert.Equal(Pantalla.Top + Margin, alcance.Top, 3);
        Assert.Equal(Pantalla.Right - ShellLayout.OrbSize - Margin, alcance.Right, 3);
        Assert.Equal(Pantalla.Bottom - ShellLayout.OrbSize - Margin, alcance.Bottom, 3);
    }

    /// <summary>
    /// Poner la ventana donde el orbe tiene que caer y volver a preguntar dónde cae da lo mismo.
    /// </summary>
    /// <remarks>
    /// Es la ida y vuelta que usa la ventana cada vez que se recoloca. Si se desfasara al cambiar de
    /// tamaño, el orbe se correría un poco cada vez que alguien mueve la barra.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Escalas))]
    public void LaEsquinaDeLaVentanaYLaDelOrbeSiguenSiendoLaMismaCuenta(double escala)
    {
        ShellLayout.Scale = escala;
        var orbe = new Point(640, 480);

        foreach (var abreDerecha in new[] { true, false })
        {
            var ventana = ShellLayout.WindowOriginFor(orbe, abreDerecha);
            var vuelta = ShellLayout.OrbOriginFor(ventana, abreDerecha);

            Assert.Equal(orbe.X, vuelta.X, 3);
            Assert.Equal(orbe.Y, vuelta.Y, 3);
        }
    }

    /// <summary>
    /// Un archivo de preferencias editado a mano no puede dejar un orbe de cinco mil píxeles.
    /// </summary>
    /// <remarks>
    /// El caso real es escribir «50» queriendo decir «50 %»: sin recorte serían 5400 px de orbe, un
    /// ancho de ventana de 5800 y nada visible en ninguna pantalla.
    /// </remarks>
    [Theory]
    [InlineData(50, 2.0)]
    [InlineData(3.5, 2.0)]
    [InlineData(0.01, 0.5)]
    [InlineData(-4, 0.5)]
    [InlineData(0, 0.5)]
    public void UnTamañoAbsurdoSeRecortaAlRangoLegal(double pedido, double esperado)
    {
        ShellLayout.Scale = pedido;

        Assert.Equal(esperado, ShellLayout.Scale, 3);
        Assert.Equal(ShellLayout.DefaultOrbSize * esperado, ShellLayout.OrbSize, 3);
    }

    /// <summary>Lo que no es un número vuelve al tamaño de fábrica, no a un orbe que no se dibuja.</summary>
    [Fact]
    public void UnTamañoQueNoEsUnNúmeroVuelveAlDeFábrica()
    {
        ShellLayout.Scale = double.NaN;
        Assert.Equal(OrbScaleRange.Default, ShellLayout.Scale, 3);

        ShellLayout.Scale = double.PositiveInfinity;
        Assert.Equal(OrbScaleRange.Default, ShellLayout.Scale, 3);
    }

    /// <summary>
    /// El lado por el que se abre el panel tiene en cuenta el tamaño del orbe.
    /// </summary>
    /// <remarks>
    /// Un orbe grande necesita más lugar a la derecha para que el panel entre; si esta cuenta se
    /// quedara con los 108 de fábrica, al 200 % el panel se abriría hacia la derecha en un lugar
    /// donde ya no entra y se vería recortado contra el borde.
    /// </remarks>
    [Fact]
    public void UnOrbeMásGrandeNecesitaMásLugarParaAbrirHaciaLaDerecha()
    {
        // Justo el lugar donde el panel entra a la derecha con el orbe de fábrica y no con el doble.
        var orbe = new Point(Pantalla.Right - 108 - PanelCatalog.MaxWidth, 400);

        ShellLayout.Scale = 1.0;
        Assert.True(ShellLayout.ShouldOpenRight(orbe, Pantalla));

        ShellLayout.Scale = 2.0;
        Assert.False(ShellLayout.ShouldOpenRight(orbe, Pantalla));
    }
}
