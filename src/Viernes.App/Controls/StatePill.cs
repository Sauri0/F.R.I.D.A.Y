using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Controls;

/// <summary>
/// La píldora de estado: un punto del color del estado y dos palabras, sobre vidrio.
/// </summary>
/// <remarks>
/// Morfea de ancho en vez de aparecer y desaparecer, y esa es toda la idea: el orbe dice el
/// <em>modo</em> con color y forma, y la píldora pone el nombre cuando hace falta. Si cambiara de
/// tamaño de golpe se leería como dos avisos distintos en vez de como uno que cambió.
/// <para>
/// En reposo no muestra nada. Un cartel que dice «acá estoy» todo el día deja de decir algo a los
/// diez minutos, y además tapa el escritorio.
/// </para>
/// <para>
/// Implementa <see cref="IOrbBody"/> aunque no sea un cuerpo: así quien publica estados y ánimos
/// los manda a una sola lista y no tiene que acordarse de que además existe una píldora.
/// </para>
/// </remarks>
internal sealed class StatePill : FrameworkElement, IOrbBody
{
    private const double PillHeight = 27;
    private const double DotSize = 8;
    private const double PaddingLeft = 10;
    private const double PaddingRight = 12;
    private const double Gap = 8;
    private const double FontSize = 11.5;

    /// <summary>Cuánto tarda el ancho en llegar al nuevo. Sale del boceto: 320 ms.</summary>
    private const double MorphSeconds = 0.320;

    /// <summary>Y la opacidad, más rápido: aparecer tiene que sentirse inmediato.</summary>
    private const double FadeSeconds = 0.200;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private readonly OrbMoodClock _moods = new();

    private double _width;
    private double _targetWidth;
    private double _opacity;
    private double _targetOpacity;
    private long _lastTicks;
    private bool _isRunning;
    private string _label = string.Empty;
    private double _maxWidth;

    public StatePill()
    {
        Height = PillHeight;
        IsHitTestVisible = false;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>El estado de fondo.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(StatePill),
        new PropertyMetadata(AssistantVisualState.Idle, (d, _) => ((StatePill)d).Refresh()));

    /// <inheritdoc />
    public AssistantVisualState State
    {
        get => (AssistantVisualState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Sobre escritorio claro el vidrio y el texto se invierten.</summary>
    public static readonly DependencyProperty IsLightDesktopProperty = DependencyProperty.Register(
        nameof(IsLightDesktop),
        typeof(bool),
        typeof(StatePill),
        new PropertyMetadata(false, (d, _) => ((StatePill)d).InvalidateVisual()));

    /// <inheritdoc />
    public bool IsLightDesktop
    {
        get => (bool)GetValue(IsLightDesktopProperty);
        set => SetValue(IsLightDesktopProperty, value);
    }

    /// <summary>Modo madrugada, de 0 a 1.</summary>
    public static readonly DependencyProperty NightModeProperty = DependencyProperty.Register(
        nameof(NightMode),
        typeof(double),
        typeof(StatePill),
        new PropertyMetadata(0.0, (d, _) => ((StatePill)d).InvalidateVisual()));

    /// <inheritdoc />
    public double NightMode
    {
        get => (double)GetValue(NightModeProperty);
        set => SetValue(NightModeProperty, value);
    }

    /// <summary>
    /// La conversación está abierta o hay dictado en curso: la píldora se calla.
    /// </summary>
    /// <remarks>
    /// Con el panel abierto el nombre del estado ya está adentro, y dos veces lo mismo a diez píxeles
    /// de distancia se lee como un error de la interfaz.
    /// </remarks>
    public static readonly DependencyProperty IsSuppressedProperty = DependencyProperty.Register(
        nameof(IsSuppressed),
        typeof(bool),
        typeof(StatePill),
        new PropertyMetadata(false, (d, _) => ((StatePill)d).Refresh()));

    /// <summary>La conversación está abierta: la píldora se calla.</summary>
    internal bool IsSuppressed
    {
        get => (bool)GetValue(IsSuppressedProperty);
        set => SetValue(IsSuppressedProperty, value);
    }

    /// <inheritdoc />
    public void ShowMood(OrbMood mood)
    {
        _moods.Trigger(mood);
        Refresh();
        Start();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Se mide una vez con la etiqueta más larga de todas y ya no vuelve a pedir layout: el ancho
        // que morfea se dibuja adentro, centrado. Animar el tamaño de un elemento obliga a rehacer
        // el layout sesenta veces por segundo, y esto está encima de un orbe que ya está dibujando.
        if (_maxWidth <= 0)
        {
            _maxWidth = MeasureWidest();
        }

        return new Size(_maxWidth, PillHeight);
    }

    private double MeasureWidest()
    {
        var widest = 0.0;
        foreach (var state in Enum.GetValues<AssistantVisualState>())
        {
            widest = Math.Max(widest, WidthFor(OrbPalette.For(state).PillLabel));
        }

        foreach (var mood in Enum.GetValues<OrbMood>())
        {
            widest = Math.Max(widest, WidthFor(OrbMoods.Label(mood)));
        }

        return widest;
    }

    private double WidthFor(string label) =>
        label.Length == 0 ? 0 : PaddingLeft + DotSize + Gap + Text(label).WidthIncludingTrailingWhitespace + PaddingRight;

    private FormattedText Text(string label) => new(
        label,
        CultureInfo.GetCultureInfo("es-AR"),
        System.Windows.FlowDirection.LeftToRight,
        Face,
        FontSize,
        Brushes.White,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Recalcula qué dice y cuánto mide, y arranca la animación si cambió algo.</summary>
    private void Refresh()
    {
        // El ánimo manda sobre el estado mientras dura: si acaba de decir «¡listo!», eso es lo que
        // hay que leer, no el estado de fondo al que va a volver en un segundo y medio.
        var label = _moods.Current is { } mood
            ? OrbMoods.Label(mood)
            : State == AssistantVisualState.Idle || IsSuppressed
                ? string.Empty
                : OrbPalette.For(State).PillLabel;

        if (label != _label)
        {
            _label = label;
            _targetWidth = WidthFor(label);
            _targetOpacity = label.Length == 0 ? 0 : 1;

            // Si estaba invisible aparece ya con el ancho nuevo: morfear desde cero se ve como que
            // la píldora crece desde un punto, y eso es una animación de aparición, no de cambio.
            if (_opacity <= 0.001)
            {
                _width = _targetWidth;
            }

            Start();
        }

        InvalidateVisual();
    }

    private void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _lastTicks = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        var ticks = rendering.RenderingTime.Ticks;
        var delta = _lastTicks == 0 ? 0 : (ticks - _lastTicks) / (double)TimeSpan.TicksPerSecond;
        _lastTicks = ticks;
        delta = Math.Clamp(delta, 0, 0.1);

        var before = _moods.Current;
        _moods.Advance(delta, OrbBody.Gota);
        if (before != _moods.Current)
        {
            // El ánimo se apagó solo: la píldora vuelve al estado de fondo sin que nadie la reponga.
            Refresh();
        }

        _width = Approach(_width, _targetWidth, delta / MorphSeconds);
        _opacity = Approach(_opacity, _targetOpacity, delta / FadeSeconds);

        InvalidateVisual();

        // Cuando ya no queda nada moviéndose se baja del bucle de render. Una píldora quieta no
        // tiene por qué costar un cuadro por cuadro en una aplicación que está encendida todo el día.
        if (_moods.Current is null &&
            Math.Abs(_width - _targetWidth) < 0.05 &&
            Math.Abs(_opacity - _targetOpacity) < 0.005)
        {
            _width = _targetWidth;
            _opacity = _targetOpacity;
            Stop();
        }
    }

    /// <summary>Se acerca al destino cubriendo una fracción de lo que falta. Nunca lo pasa.</summary>
    private static double Approach(double current, double target, double step) =>
        current + ((target - current) * Math.Clamp(step * 2.2, 0, 1));

    protected override void OnRender(DrawingContext context)
    {
        if (_opacity <= 0.004 || _width <= 1)
        {
            return;
        }

        var night = Math.Clamp(NightMode, 0, 1);
        var light = IsLightDesktop;
        var accent = OrbNight.Tint(OrbPalette.For(State).Body, night);

        var left = (ActualWidth - _width) / 2;
        var rect = new Rect(left, 0, _width, PillHeight);
        var radius = PillHeight / 2;

        context.PushOpacity(_opacity * (1 - (0.25 * night)));

        // El vidrio: un degradado diagonal claro encima de un velo oscuro. Sin desenfoque real —eso
        // lo pone el acrílico de la ventana en Win 11, y en Win 10 no lo pone nadie—, pero con el
        // brillo del canto superior, que es lo que hace que se lea como vidrio y no como una caja.
        context.DrawRoundedRectangle(GlassBrush(light), BorderPen(light), rect, radius, radius);

        var dotCenter = new Point(left + PaddingLeft + (DotSize / 2), PillHeight / 2);
        context.DrawEllipse(GlowBrush(accent), null, dotCenter, DotSize, DotSize);
        context.DrawEllipse(new SolidColorBrush(accent), null, dotCenter, DotSize / 2, DotSize / 2);

        var text = Text(_label);
        text.SetForegroundBrush(light ? Brushes.Black : Brushes.White);
        context.DrawText(text, new Point(
            left + PaddingLeft + DotSize + Gap,
            (PillHeight - text.Height) / 2));

        context.Pop();
    }

    private static Brush GlassBrush(bool light)
    {
        var brush = new LinearGradientBrush(
            light ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x9E, 0x2E, 0x2E, 0x34),
            light ? Color.FromArgb(0xC8, 0xF2, 0xF2, 0xF5) : Color.FromArgb(0x9E, 0x18, 0x18, 0x1C),
            152);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Pen BorderPen(bool light)
    {
        var pen = new System.Windows.Media.Pen(
            new SolidColorBrush(light ? Color.FromArgb(0x2E, 0x1E, 0x1E, 0x24) : Color.FromArgb(0x31, 0xFF, 0xFF, 0xFF)),
            1);
        pen.Freeze();
        return pen;
    }

    /// <summary>El resplandor del punto. Es lo único que lo separa de un círculo pintado.</summary>
    private static Brush GlowBrush(Color color)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            [
                new GradientStop(Color.FromArgb(0xBF, color.R, color.G, color.B), 0.25),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1)
            ]
        };
        brush.Freeze();
        return brush;
    }
}
