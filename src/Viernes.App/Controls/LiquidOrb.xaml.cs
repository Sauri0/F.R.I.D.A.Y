using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Viernes.App.Controls;

/// <summary>
/// La gota. Un único <see cref="Path"/> cerrado cuyos puntos de control se animan con períodos que
/// no coinciden entre sí, de modo que el contorno nunca repite un ciclo visible.
/// </summary>
/// <remarks>
/// Los estados cambian la <em>viscosidad</em> —la velocidad del mismo fluido— y el color, nunca el
/// vocabulario de formas: sigue siendo la misma sustancia haciendo otra cosa. Girar está reservado
/// para «pensando», así que un giro siempre significa trabajo.
/// </remarks>
internal partial class LiquidOrb : UserControl
{
    private static readonly Duration ColorFade = new(TimeSpan.FromMilliseconds(420));

    /// <summary>
    /// Puntos de control y anclajes de la masa. Las excursiones son amplias a propósito: con
    /// desplazamientos de dos o tres píxeles el contorno se ve rígido, no líquido.
    /// </summary>
    private static readonly (string Segment, string Property, Point Calm, Point Swell)[] ControlPoints =
    [
        ("ArcNE", "Point1", new Point(48, 11), new Point(55, 4)),
        ("ArcNE", "Point2", new Point(59, 22), new Point(67, 17)),
        ("ArcNE", "Point3", new Point(59, 35), new Point(64, 31)),
        ("ArcSE", "Point1", new Point(59, 48), new Point(66, 54)),
        ("ArcSE", "Point2", new Point(48, 59), new Point(42, 67)),
        ("ArcSE", "Point3", new Point(35, 59), new Point(32, 65)),
        ("ArcSW", "Point1", new Point(22, 59), new Point(14, 65)),
        ("ArcSW", "Point2", new Point(11, 48), new Point(3, 43)),
        ("ArcSW", "Point3", new Point(11, 35), new Point(6, 38)),
        ("ArcNW", "Point1", new Point(11, 22), new Point(4, 27)),
        ("ArcNW", "Point2", new Point(22, 11), new Point(28, 3))
    ];

    /// <summary>
    /// Gotas satélite. Orbitan cerca del borde y siempre solapan la masa, así la unión Nonzero
    /// las funde sin costura: se ven como lóbulos que crecen y se reabsorben, no como puntos sueltos.
    /// </summary>
    private static readonly (string Name, Point Calm, Point Swell, double CalmRadius, double SwellRadius, double Seconds)[] Droplets =
    [
        ("Drop1", new Point(50, 26), new Point(58, 18), 9.5, 13.0, 5.3),
        ("Drop2", new Point(21, 44), new Point(12, 50), 8.5, 12.0, 6.7),
        ("Drop3", new Point(38, 52), new Point(33, 62), 8.0, 11.0, 4.9)
    ];

    /// <summary>Períodos deliberadamente no conmensurables: el conjunto no vuelve a alinearse.</summary>
    private static readonly double[] Periods = [4.3, 5.1, 6.7, 5.9, 7.3, 4.7, 6.1, 8.3, 5.5, 7.9, 6.3];

    private Storyboard? _liquid;
    private Storyboard? _sheen;
    private Storyboard? _spin;
    private Storyboard? _tension;

    public LiquidOrb()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(LiquidOrb),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

    internal AssistantVisualState State
    {
        get => (AssistantVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_liquid is not null)
        {
            return;
        }

        _liquid = BuildLiquidStoryboard();
        _sheen = BuildSheenStoryboard();
        _spin = BuildSpinStoryboard();
        _tension = BuildTensionStoryboard();

        _liquid.Begin(this, isControllable: true);
        _sheen.Begin(this, isControllable: true);
        ApplyState(State);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopControllable(_liquid);
        StopControllable(_sheen);
        StopControllable(_spin);
        StopControllable(_tension);
        _liquid = _sheen = _spin = _tension = null;
    }

    private void StopControllable(Storyboard? storyboard)
    {
        try
        {
            storyboard?.Stop(this);
            storyboard?.Remove(this);
        }
        catch (InvalidOperationException)
        {
            // El storyboard no llegó a arrancar; no hay reloj que detener.
        }
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LiquidOrb)d).ApplyState((AssistantVisualState)e.NewValue);

    private void ApplyState(AssistantVisualState state)
    {
        if (_liquid is null)
        {
            return;
        }

        // Viscosidad: el mismo fluido, más suelto cuanto más trabaja.
        var viscosity = state switch
        {
            AssistantVisualState.Listening => 2.6,
            AssistantVisualState.Thinking => 3.4,
            AssistantVisualState.Speaking => 4.5,
            AssistantVisualState.Attention => 1.7,
            AssistantVisualState.Error => 1.4,
            _ => 1.0
        };

        _liquid.SetSpeedRatio(this, viscosity);
        _sheen?.SetSpeedRatio(this, Math.Max(1.0, viscosity * 0.5));

        Toggle(_spin, state == AssistantVisualState.Thinking);
        Toggle(_tension, state is AssistantVisualState.Attention or AssistantVisualState.Error);

        var (body, deep, halo) = PaletteFor(state);
        Animate(MassBody, GradientStop.ColorProperty, body);
        Animate(MassDeep, GradientStop.ColorProperty, deep);
        Animate(GlowInner, GradientStop.ColorProperty, WithAlpha(halo, 0x4D));
        Animate(MassGlow, DropShadowEffect.ColorProperty, halo);
    }

    private void Toggle(Storyboard? storyboard, bool shouldRun)
    {
        if (storyboard is null)
        {
            return;
        }

        if (shouldRun)
        {
            storyboard.Begin(this, isControllable: true);
        }
        else
        {
            StopControllable(storyboard);
        }
    }

    private static void Animate(Animatable target, DependencyProperty property, Color to) =>
        target.BeginAnimation(property, new ColorAnimation(to, ColorFade)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Los mismos valores que ya usaba el shell; la forma cambió, el idioma de color no.</summary>
    private static (Color Body, Color Deep, Color Halo) PaletteFor(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Listening => (
            Color.FromRgb(0x72, 0xF0, 0xC0),
            Color.FromRgb(0x1B, 0x5E, 0x4C),
            Color.FromRgb(0x72, 0xF0, 0xC0)),
        AssistantVisualState.Thinking => (
            Color.FromRgb(0x9B, 0xB7, 0xFF),
            Color.FromRgb(0x2C, 0x3C, 0x74),
            Color.FromRgb(0x9B, 0xB7, 0xFF)),
        AssistantVisualState.Speaking => (
            Color.FromRgb(0xFF, 0xCE, 0x82),
            Color.FromRgb(0x6E, 0x4A, 0x1B),
            Color.FromRgb(0xFF, 0xC5, 0x6B)),
        AssistantVisualState.Attention => (
            Color.FromRgb(0xFF, 0xB3, 0x47),
            Color.FromRgb(0x6E, 0x44, 0x12),
            Color.FromRgb(0xFF, 0xB3, 0x47)),
        AssistantVisualState.Error => (
            Color.FromRgb(0xFF, 0x73, 0x85),
            Color.FromRgb(0x6B, 0x22, 0x2C),
            Color.FromRgb(0xFF, 0x73, 0x85)),
        _ => (
            Color.FromRgb(0x72, 0xD9, 0xFF),
            Color.FromRgb(0x1B, 0x5A, 0x73),
            Color.FromRgb(0x72, 0xD9, 0xFF))
    };

    private Storyboard BuildLiquidStoryboard()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        for (var index = 0; index < ControlPoints.Length; index++)
        {
            var (segment, property, calm, swell) = ControlPoints[index];
            var animation = new PointAnimation
            {
                From = calm,
                To = swell,
                Duration = new Duration(TimeSpan.FromSeconds(Periods[index])),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTargetName(animation, segment);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            storyboard.Children.Add(animation);
        }

        foreach (var (name, calm, swell, calmRadius, swellRadius, seconds) in Droplets)
        {
            var move = new PointAnimation
            {
                From = calm,
                To = swell,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTargetName(move, name);
            Storyboard.SetTargetProperty(move, new PropertyPath(EllipseGeometry.CenterProperty));
            storyboard.Children.Add(move);

            // El radio corre en otro período que el desplazamiento: la gota se hincha fuera de fase.
            storyboard.Children.Add(Timeline(
                name,
                EllipseGeometry.RadiusXProperty,
                calmRadius,
                swellRadius,
                seconds * 1.37));
            storyboard.Children.Add(Timeline(
                name,
                EllipseGeometry.RadiusYProperty,
                swellRadius,
                calmRadius,
                seconds * 1.11));
        }

        // Respiración global: la masa entera se estira, con un desfasaje más que hace de latido lento.
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleXProperty, 0.97, 1.07, 6.9));
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleYProperty, 1.06, 0.95, 5.7));
        storyboard.Children.Add(Rotate("MassTilt", -9, 9, 8.1));
        return storyboard;
    }

    private static Storyboard BuildSheenStoryboard()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        // El brillo se desplaza más que la masa: es luz corriendo sobre una superficie mojada.
        storyboard.Children.Add(Translate("SheenDrift", TranslateTransform.XProperty, -5.5, 6.5, 6.3));
        storyboard.Children.Add(Translate("SheenDrift", TranslateTransform.YProperty, 4.0, -5.0, 7.7));
        storyboard.Children.Add(Scale("GlowScale", ScaleTransform.ScaleXProperty, 0.94, 1.11, 5.9));
        storyboard.Children.Add(Scale("GlowScale", ScaleTransform.ScaleYProperty, 0.94, 1.11, 5.9));
        return storyboard;
    }

    private static Storyboard BuildSpinStoryboard()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(3.4)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTargetName(animation, "MassTilt");
        Storyboard.SetTargetProperty(animation, new PropertyPath(RotateTransform.AngleProperty));
        storyboard.Children.Add(animation);
        return storyboard;
    }

    private static Storyboard BuildTensionStoryboard()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleXProperty, 0.96, 1.08, 0.9));
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleYProperty, 0.96, 1.08, 0.9));
        return storyboard;
    }

    private static DoubleAnimation Scale(string target, DependencyProperty property, double from, double to, double seconds) =>
        Timeline(target, property, from, to, seconds);

    private static DoubleAnimation Translate(string target, DependencyProperty property, double from, double to, double seconds) =>
        Timeline(target, property, from, to, seconds);

    private static DoubleAnimation Rotate(string target, double from, double to, double seconds) =>
        Timeline(target, RotateTransform.AngleProperty, from, to, seconds);

    private static DoubleAnimation Timeline(
        string target,
        DependencyProperty property,
        double from,
        double to,
        double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTargetName(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }
}
