using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// Cómo se está dibujando el vidrio: con desenfoque real detrás, o sin él.
/// </summary>
/// <remarks>
/// En Windows 11 el desenfoque lo pone el sistema y el cuerpo puede bajar de opacidad, porque hay
/// algo abajo que sostiene la lectura. En Windows 10 no hay desenfoque: el cuerpo tiene que ser casi
/// opaco o el texto se pierde contra un escritorio cualquiera. Son dos recetas, no una con un
/// interruptor, y se diseñó para la peor.
/// </remarks>
internal enum GlassVariant
{
    /// <summary>Acrílico del sistema, escritorio oscuro.</summary>
    AcrilicoOscuro,

    /// <summary>Acrílico del sistema, escritorio claro.</summary>
    AcrilicoClaro,

    /// <summary>Sin desenfoque: cuerpo casi opaco. Windows 10, o acrílico deshabilitado.</summary>
    Opaco
}

/// <summary>
/// Las tres capas de color de una variante: el cuerpo plano, el brillo que lo cruza y el contorno.
/// </summary>
/// <param name="Body">Tinte plano del cuerpo. Va primero y es lo que da la densidad.</param>
/// <param name="Sheen">Gradiente a 152° que se apoya encima. Sin esto es una tarjeta gris.</param>
/// <param name="Border">Contorno de 1 px. Nunca lleva el color del estado.</param>
internal sealed record GlassRecipe(Brush Body, Brush Sheen, Brush Border);

/// <summary>
/// La receta del vidrio, familia por familia y variante por variante.
/// </summary>
/// <remarks>
/// Los valores salen medidos de la referencia ejecutable. El detalle que más se nota si se pierde es
/// que el brillo tiene tres o cuatro paradas y no dos: una caída lineal se lee como plástico.
/// </remarks>
internal static class GlassPalette
{
    private static readonly Dictionary<(PanelFamily Family, GlassVariant Variant), GlassRecipe> Recipes = Build();

    /// <summary>La receta de una familia en una variante.</summary>
    public static GlassRecipe For(PanelFamily family, GlassVariant variant) => Recipes[(family, variant)];

    private static Dictionary<(PanelFamily, GlassVariant), GlassRecipe> Build()
    {
        var table = new Dictionary<(PanelFamily, GlassVariant), GlassRecipe>();

        Add(table, PanelFamily.Neutro,
            darkBody: "#661A1A1E",
            darkSheen: [("#30FFFFFF", 0), ("#16FFFFFF", 0.38), ("#0DFFFFFF", 0.72), ("#13FFFFFF", 1)],
            lightBody: "#AD18181C",
            lightSheen: [("#24FFFFFF", 0), ("#0DFFFFFF", 0.40), ("#07FFFFFF", 1)],
            solid: [("#F0343437", 0), ("#F5212124", 0.46), ("#F717171A", 1)],
            borderDark: "#33FFFFFF", borderLight: "#26FFFFFF", borderSolid: "#21FFFFFF");

        Add(table, PanelFamily.Ambar,
            darkBody: "#70261F16",
            darkSheen: [("#33FFECCD", 0), ("#13FFDCAA", 0.40), ("#0DFFD296", 1)],
            lightBody: "#B3231C13",
            lightSheen: [("#26FFECCD", 0), ("#0DFFDCAA", 0.40), ("#08FFD296", 1)],
            solid: [("#F03A3329", 0), ("#F526201A", 0.46), ("#F71B1713", 1)],
            borderDark: "#66FFCD82", borderLight: "#57FFCD82", borderSolid: "#47FFC56B");

        Add(table, PanelFamily.Rojo,
            darkBody: "#7528181B",
            darkSheen: [("#30FFD6DC", 0), ("#12FFB4BE", 0.40), ("#0BFFAAB4", 1)],
            lightBody: "#B3251619",
            lightSheen: [("#24FFD6DC", 0), ("#0DFFB4BE", 0.40), ("#08FFAAB4", 1)],
            solid: [("#F03A2C2E", 0), ("#F5271C1E", 0.46), ("#F71C1416", 1)],
            borderDark: "#61FF96A5", borderLight: "#52FF96A5", borderSolid: "#42FF7385");

        Add(table, PanelFamily.Gris,
            darkBody: "#751C1D1E",
            darkSheen: [("#29FFFFFF", 0), ("#12FFFFFF", 0.40), ("#0AFFFFFF", 1)],
            lightBody: "#B31A1B1C",
            lightSheen: [("#21FFFFFF", 0), ("#0DFFFFFF", 0.40), ("#07FFFFFF", 1)],
            solid: [("#F0303132", 0), ("#F51E1F20", 0.46), ("#F7151617", 1)],
            borderDark: "#2BFFFFFF", borderLight: "#24FFFFFF", borderSolid: "#1AFFFFFF");

        return table;
    }

    private static void Add(
        Dictionary<(PanelFamily, GlassVariant), GlassRecipe> table,
        PanelFamily family,
        string darkBody,
        (string Color, double Offset)[] darkSheen,
        string lightBody,
        (string Color, double Offset)[] lightSheen,
        (string Color, double Offset)[] solid,
        string borderDark,
        string borderLight,
        string borderSolid)
    {
        table[(family, GlassVariant.AcrilicoOscuro)] =
            new GlassRecipe(Flat(darkBody), Diagonal(darkSheen), Flat(borderDark));
        table[(family, GlassVariant.AcrilicoClaro)] =
            new GlassRecipe(Flat(lightBody), Diagonal(lightSheen), Flat(borderLight));

        // Sin desenfoque el cuerpo ya es un gradiente completo, así que el brillo no se repite: se
        // duplicaría el degradado y el panel se vería sucio arriba.
        table[(family, GlassVariant.Opaco)] =
            new GlassRecipe(Diagonal(solid, 0.18), Flat("#00000000"), Flat(borderSolid));
    }

    private static SolidColorBrush Flat(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// El gradiente a 152° de la referencia, aproximado con los puntos que WPF sabe interpolar.
    /// </summary>
    private static LinearGradientBrush Diagonal((string Color, double Offset)[] stops, double lean = 0.22)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(lean, 0),
            EndPoint = new Point(1 - lean, 1)
        };

        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        }

        brush.Freeze();
        return brush;
    }
}
