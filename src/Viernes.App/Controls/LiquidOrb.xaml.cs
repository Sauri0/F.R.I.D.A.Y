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
    /// Puntos de control de la masa, partiendo de un círculo de radio 25 (k = r · 0.5523).
    /// Las excursiones son de dos o tres píxeles a propósito: la tensión superficial mantiene
    /// redonda a una gota en reposo. Deformarla más la convierte en una ameba, no en agua.
    /// </summary>
    private static readonly (string Segment, string Property, Point Calm, Point Swell)[] ControlPoints =
    [
        ("ArcNE", "Point1", new Point(48.8, 10.0), new Point(51.5, 7.6)),
        ("ArcNE", "Point2", new Point(60.0, 21.2), new Point(63.2, 19.4)),
        ("ArcNE", "Point3", new Point(60.0, 35.0), new Point(62.8, 33.6)),
        ("ArcSE", "Point1", new Point(60.0, 48.8), new Point(63.0, 51.2)),
        ("ArcSE", "Point2", new Point(48.8, 60.0), new Point(46.6, 63.4)),
        ("ArcSE", "Point3", new Point(35.0, 60.0), new Point(33.8, 63.0)),
        ("ArcSW", "Point1", new Point(21.2, 60.0), new Point(18.4, 62.6)),
        ("ArcSW", "Point2", new Point(10.0, 48.8), new Point(6.8, 46.8)),
        ("ArcSW", "Point3", new Point(10.0, 35.0), new Point(7.2, 36.8)),
        ("ArcNW", "Point1", new Point(10.0, 21.2), new Point(7.0, 23.6)),
        ("ArcNW", "Point2", new Point(21.2, 10.0), new Point(23.4, 6.8))
    ];

    /// <summary>Períodos deliberadamente no conmensurables: el conjunto no vuelve a alinearse.</summary>
    private static readonly double[] Periods = [6.3, 7.1, 8.7, 7.9, 9.3, 6.7, 8.1, 10.3, 7.5, 9.9, 8.3];

    private Storyboard? _liquid;
    private Storyboard? _sheen;
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
        _tension = BuildTensionStoryboard();

        _liquid.Begin(this, isControllable: true);
        _sheen.Begin(this, isControllable: true);
        ApplyState(State);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopControllable(_liquid);
        StopControllable(_sheen);
        StopControllable(_tension);
        _liquid = _sheen = _tension = null;
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

        // Girar 360° un cuerpo ovoide barre la silueta y deja los reflejos flotando fuera del borde.
        // «Pensando» se distingue por viscosidad y color, que es más fiel a un líquido agitándose.
        Toggle(_tension, state is AssistantVisualState.Attention or AssistantVisualState.Error);

        var (body, deep, rim, halo) = PaletteFor(state);
        Animate(MassBody, GradientStop.ColorProperty, body);
        Animate(MassDeep, GradientStop.ColorProperty, deep);
        Animate(MassRim, GradientStop.ColorProperty, rim);
        Animate(GlowInner, GradientStop.ColorProperty, WithAlpha(halo, 0x38));
        Animate(MassGlow, DropShadowEffect.ColorProperty, halo);

        // El rebote de luz se tiñe del estado: en blanco puro parecía suciedad sobre el cuerpo.
        Animate(RimCore, GradientStop.ColorProperty, WithAlpha(Lighten(body), 0xC4));
        Animate(RimEdge, GradientStop.ColorProperty, WithAlpha(body, 0x00));
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

    /// <summary>Lleva el color hacia el blanco sin desaturarlo del todo, para el rebote de luz.</summary>
    private static Color Lighten(Color color) => Color.FromRgb(
        (byte)(color.R + ((255 - color.R) * 0.62)),
        (byte)(color.G + ((255 - color.G) * 0.62)),
        (byte)(color.B + ((255 - color.B) * 0.62)));

    /// <summary>
    /// Cuerpo, profundidad y borde. El borde oscuro es lo que da densidad: sin él la gota se ve
    /// como una mancha de color plana, por más reflejo que tenga encima.
    /// </summary>
    private static (Color Body, Color Deep, Color Rim, Color Halo) PaletteFor(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Listening => (
            Color.FromRgb(0x72, 0xF0, 0xC0),
            Color.FromRgb(0x16, 0x6B, 0x54),
            Color.FromRgb(0x07, 0x36, 0x2A),
            Color.FromRgb(0x72, 0xF0, 0xC0)),
        AssistantVisualState.Thinking => (
            Color.FromRgb(0x9B, 0xB7, 0xFF),
            Color.FromRgb(0x30, 0x44, 0x86),
            Color.FromRgb(0x15, 0x1E, 0x45),
            Color.FromRgb(0x9B, 0xB7, 0xFF)),
        AssistantVisualState.Speaking => (
            Color.FromRgb(0xFF, 0xCE, 0x82),
            Color.FromRgb(0x7D, 0x54, 0x1C),
            Color.FromRgb(0x3E, 0x28, 0x08),
            Color.FromRgb(0xFF, 0xC5, 0x6B)),
        AssistantVisualState.Attention => (
            Color.FromRgb(0xFF, 0xB3, 0x47),
            Color.FromRgb(0x7D, 0x4D, 0x13),
            Color.FromRgb(0x3E, 0x23, 0x04),
            Color.FromRgb(0xFF, 0xB3, 0x47)),
        AssistantVisualState.Error => (
            Color.FromRgb(0xFF, 0x73, 0x85),
            Color.FromRgb(0x78, 0x26, 0x32),
            Color.FromRgb(0x3B, 0x0F, 0x17),
            Color.FromRgb(0xFF, 0x73, 0x85)),
        _ => (
            Color.FromRgb(0x72, 0xD9, 0xFF),
            Color.FromRgb(0x17, 0x60, 0x7F),
            Color.FromRgb(0x08, 0x31, 0x4A),
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

        // Respiración sobre la base ovoide (1.035 × 0.965): se ensancha y se afina, sin latido.
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleXProperty, 1.02, 1.055, 7.3));
        storyboard.Children.Add(Scale("MassScale", ScaleTransform.ScaleYProperty, 0.98, 0.945, 6.1));
        storyboard.Children.Add(Rotate("MassTilt", -3.5, 3.5, 9.7));
        return storyboard;
    }

    private static Storyboard BuildSheenStoryboard()
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        // El reflejo se corre poco: si viaja mucho deja de parecer un reflejo fijo sobre una curva.
        storyboard.Children.Add(Translate("SheenDrift", TranslateTransform.XProperty, -1.2, 1.8, 8.3));
        storyboard.Children.Add(Translate("SheenDrift", TranslateTransform.YProperty, 1.0, -1.4, 9.7));
        storyboard.Children.Add(Scale("GlowScale", ScaleTransform.ScaleXProperty, 0.98, 1.05, 7.9));
        storyboard.Children.Add(Scale("GlowScale", ScaleTransform.ScaleYProperty, 0.98, 1.05, 7.9));
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
