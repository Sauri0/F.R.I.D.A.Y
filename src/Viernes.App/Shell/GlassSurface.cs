using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Shell;

/// <summary>
/// El vidrio del desplegable, dibujado capa por capa.
/// </summary>
/// <remarks>
/// Sin <c>backdrop-filter</c> no hay vidrio real: hay que pintarlo. En Windows 11 el desenfoque lo
/// pone el sistema detrás de la ventana y estas capas se apoyan encima; en Windows 10 no hay
/// desenfoque y son estas mismas capas las que hacen que siga leyéndose como vidrio y no como una
/// tarjeta gris. Por eso se dibujan siempre: sólo cambia la variante del cuerpo.
/// <para>
/// Se dibuja en <see cref="OnRender"/> y no con Borders anidados porque son ocho capas: ocho
/// elementos con su propio layout, su propio hit-test y su propia caché son ocho veces el costo de
/// una sola lista de <c>DrawRectangle</c> sobre el mismo contexto.
/// </para>
/// <para>
/// El barrido —la banda de luz que cruza cada 12 s— no está acá: vive como hijo, porque animar un
/// <see cref="System.Windows.Media.TranslateTransform"/> lo compone la GPU sin repintar el panel, y
/// repintar ocho capas sesenta veces por segundo para mover un reflejo no vale la pena.
/// </para>
/// </remarks>
internal sealed class GlassSurface : Decorator
{
    private const double SmallCorner = 8;
    private const double LargeCorner = 28;

    /// <summary>Familia de vidrio del panel abierto.</summary>
    public static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        nameof(Family),
        typeof(PanelFamily),
        typeof(GlassSurface),
        new FrameworkPropertyMetadata(PanelFamily.Neutro, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Con desenfoque del sistema detrás, o sin él.</summary>
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant),
        typeof(GlassVariant),
        typeof(GlassSurface),
        new FrameworkPropertyMetadata(GlassVariant.AcrilicoOscuro, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Si el panel nace a la derecha del orbe. Espeja esquinas, tinte y floración.</summary>
    public static readonly DependencyProperty OpensRightProperty = DependencyProperty.Register(
        nameof(OpensRight),
        typeof(bool),
        typeof(GlassSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    /// <summary>Color del estado del orbe. Tiñe el cuerpo, nunca el contorno.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint),
        typeof(Color),
        typeof(GlassSurface),
        new FrameworkPropertyMetadata(Color.FromRgb(0x72, 0xD9, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cuánto queda del tiempo de vida del panel, de 0 a 1.</summary>
    public static readonly DependencyProperty LifeProgressProperty = DependencyProperty.Register(
        nameof(LifeProgress),
        typeof(double),
        typeof(GlassSurface),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TopSheen = Vertical([("#21FFFFFF", 0), ("#05FFFFFF", 0.64), ("#00FFFFFF", 1)]);
    private static readonly Brush BottomShade = Vertical([("#00000000", 0), ("#42000000", 1)]);
    private static readonly Brush CornerBloom = BuildBloom();
    private static readonly Brush Grain = BuildGrain();
    private static readonly Brush TopEdge = Flat("#57FFFFFF");
    private static readonly Brush BottomEdge = Flat("#66000000");

    /// <inheritdoc cref="FamilyProperty"/>
    public PanelFamily Family
    {
        get => (PanelFamily)GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    /// <inheritdoc cref="VariantProperty"/>
    public GlassVariant Variant
    {
        get => (GlassVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    /// <inheritdoc cref="OpensRightProperty"/>
    public bool OpensRight
    {
        get => (bool)GetValue(OpensRightProperty);
        set => SetValue(OpensRightProperty, value);
    }

    /// <inheritdoc cref="TintProperty"/>
    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <inheritdoc cref="LifeProgressProperty"/>
    public double LifeProgress
    {
        get => (double)GetValue(LifeProgressProperty);
        set => SetValue(LifeProgressProperty, value);
    }

    /// <summary>La silueta del vidrio. Se usa para recortar el barrido y el contenido.</summary>
    public Geometry ShapeFor(Size size) => BuildShape(size, OpensRight);

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateClip();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2)
        {
            return;
        }

        var recipe = GlassPalette.For(Family, Variant);
        var shape = BuildShape(new Size(w, h), OpensRight);

        // 1 y 2 · cuerpo. El tinte plano primero: es lo que da la densidad, y el brillo se apoya.
        dc.DrawGeometry(recipe.Body, null, shape);
        dc.DrawGeometry(recipe.Sheen, null, shape);

        dc.PushClip(shape);

        // 3 · brillo superior sobre el 46 % del alto. Tres paradas: la caída lineal se ve plástica.
        dc.DrawRectangle(TopSheen, null, new Rect(0, 0, w, h * 0.46));

        // 4 · floración de esquina: la luz entra por donde el vidrio nace, o sea del lado del orbe.
        var bloom = new Rect(-0.14 * w, -0.5 * h, 0.78 * w, 1.4 * h);
        if (!OpensRight)
        {
            bloom = new Rect(w - bloom.Right, bloom.Y, bloom.Width, bloom.Height);
        }

        dc.DrawRectangle(CornerBloom, null, bloom);

        // 5 · tinte del orbe: 56 px del color de estado, degradando a nada.
        dc.DrawRectangle(
            EdgeTint(),
            null,
            OpensRight ? new Rect(0, 0, 56, h) : new Rect(w - 56, 0, 56, h));

        // 6 · grano. Vidrio esmerilado: puntos de medio píxel cada tres.
        dc.DrawRectangle(Grain, null, new Rect(0, 0, w, h));

        // 7 · asiento inferior: la sombra interna que apoya el panel en vez de dejarlo flotando.
        dc.DrawRectangle(BottomShade, null, new Rect(0, h * 0.68, w, h * 0.32));

        // 8 · cantos. Un píxel de luz arriba y uno de sombra abajo: el espesor del cristal.
        dc.DrawRectangle(TopEdge, null, new Rect(0, 0, w, 1));
        dc.DrawRectangle(BottomEdge, null, new Rect(0, h - 1, w, 1));

        // La barra de vida. No promete un porcentaje de trabajo: dice cuánto le queda al panel.
        if (LifeProgress > 0)
        {
            var life = Math.Clamp(LifeProgress, 0, 1) * w;
            var x = OpensRight ? 0 : w - life;
            dc.DrawRectangle(LifeBrush(), null, new Rect(x, h - 2, life, 2));
        }

        dc.Pop();

        // El contorno va último para que ninguna capa lo pise.
        dc.DrawGeometry(null, new Pen(recipe.Border, 1), BuildShape(new Size(w - 1, h - 1), OpensRight, 0.5));
    }

    private static void OnShapeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((GlassSurface)sender).UpdateClip();

    private void UpdateClip()
    {
        Clip = ActualWidth < 2 || ActualHeight < 2
            ? null
            : BuildShape(new Size(ActualWidth, ActualHeight), OpensRight);
    }

    private Brush EdgeTint()
    {
        var tinted = Color.FromArgb(
            0x16,
            (byte)(Tint.R + (255 - Tint.R) * 0.55),
            (byte)(Tint.G + (255 - Tint.G) * 0.55),
            (byte)(Tint.B + (255 - Tint.B) * 0.55));

        var brush = new LinearGradientBrush
        {
            StartPoint = OpensRight ? new Point(0, 0) : new Point(1, 0),
            EndPoint = OpensRight ? new Point(1, 0) : new Point(0, 0)
        };
        brush.GradientStops.Add(new GradientStop(tinted, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, tinted.R, tinted.G, tinted.B), 1));
        brush.Freeze();
        return brush;
    }

    private Brush LifeBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = OpensRight ? new Point(0, 0) : new Point(1, 0),
            EndPoint = OpensRight ? new Point(1, 0) : new Point(0, 0)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, Tint.R, Tint.G, Tint.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, Tint.R, Tint.G, Tint.B), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// La silueta: casi recta del lado que nace en el orbe, redondeada del lado libre.
    /// </summary>
    private static Geometry BuildShape(Size size, bool opensRight, double inset = 0)
    {
        var w = Math.Max(0, size.Width);
        var h = Math.Max(0, size.Height);
        var near = Math.Min(SmallCorner, Math.Min(w, h) / 2);
        var far = Math.Min(LargeCorner, Math.Min(w, h) / 2);

        var topLeft = opensRight ? near : far;
        var topRight = opensRight ? far : near;
        var bottomRight = topRight;
        var bottomLeft = topLeft;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(inset + topLeft, inset), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(inset + w - topRight, inset), true, false);
            ctx.ArcTo(new Point(inset + w, inset + topRight), new Size(topRight, topRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(inset + w, inset + h - bottomRight), true, false);
            ctx.ArcTo(new Point(inset + w - bottomRight, inset + h), new Size(bottomRight, bottomRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(inset + bottomLeft, inset + h), true, false);
            ctx.ArcTo(new Point(inset, inset + h - bottomLeft), new Size(bottomLeft, bottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(inset, inset + topLeft), true, false);
            ctx.ArcTo(new Point(inset + topLeft, inset), new Size(topLeft, topLeft), 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Brush BuildBloom()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.32, 0.62),
            GradientOrigin = new Point(0.32, 0.62),
            RadiusX = 0.56,
            RadiusY = 0.50
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x24, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.72));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// El grano es un mosaico de 3 × 3 con un punto de medio píxel. Cuesta nada y es lo que separa
    /// el vidrio esmerilado del plano de color.
    /// </summary>
    private static Brush BuildGrain()
    {
        var dot = new GeometryDrawing(
            Flat("#0EFFFFFF"),
            null,
            new EllipseGeometry(new Point(1.5, 1.5), 0.5, 0.5));

        var brush = new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 3, 3),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 3, 3),
            ViewboxUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private static Brush Vertical((string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        }

        brush.Freeze();
        return brush;
    }

    private static Brush Flat(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
