using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
/// diez minutos, y además tapa el escritorio. La única excepción es el movimiento reducido, y no es
/// una excepción menor: ver <see cref="IsSuppressed"/>.
/// </para>
/// <para>
/// Dice hasta tres cosas a la vez, y esa es la razón de que exista: el <b>ánimo</b> o el
/// <b>grupo de silueta</b> adelante, el <b>estado de fondo</b> atrás, y a la derecha el
/// <b>detalle</b> —cuánto falta para el próximo reintento, hace cuánto que espera, cuánto lleva
/// pensando—. Con el signo puesto el estado sigue legible porque nunca se lo tapa: se le agrega
/// adelante.
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

    /// <summary>Antes de esto no se cuenta el tiempo de una tarea: no toda tarea merece un reloj.</summary>
    private const double CounterAfterSeconds = 1.4;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    private readonly DispatcherTimer _detailClock = new() { Interval = TimeSpan.FromSeconds(1) };

    private OrbStateChannel _channel = new();

    private double _width;
    private double _targetWidth;
    private double _opacity;
    private double _targetOpacity;
    private long _lastTicks;
    private bool _isRunning;
    private string _label = string.Empty;
    private string _detail = string.Empty;
    private double _maxWidth;

    public StatePill()
    {
        Height = PillHeight;
        IsHitTestVisible = false;
        _detailClock.Tick += (_, _) => RefreshDetail();
        _channel.Changed += OnChannelChanged;

        Loaded += (_, _) =>
        {
            OrbQuiet.Changed -= OnQuietChanged;
            OrbQuiet.Changed += OnQuietChanged;
            _channel.Changed -= OnChannelChanged;
            _channel.Changed += OnChannelChanged;
            Refresh();
        };

        Unloaded += (_, _) =>
        {
            // Un canal compartido vive más que la píldora: si no se suelta, la píldora muerta se
            // queda colgada del evento.
            OrbQuiet.Changed -= OnQuietChanged;
            _channel.Changed -= OnChannelChanged;
            _detailClock.Stop();
            Stop();
        };
    }

    /// <summary>El estado de fondo.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(AssistantVisualState),
        typeof(StatePill),
        new PropertyMetadata(AssistantVisualState.Idle, OnStateChanged));

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
    /// Se pide que la píldora se calle: la conversación está abierta o hay dictado en curso.
    /// </summary>
    /// <remarks>
    /// Con el panel abierto el nombre del estado ya está adentro, y dos veces lo mismo a diez píxeles
    /// de distancia se lee como un error de la interfaz.
    /// <para>
    /// <b>Es un pedido, no una orden, y en movimiento reducido no se le hace caso.</b> Sin
    /// movimiento la forma sólo puede cargar siete siluetas y el texto carga los quince estados: si
    /// se pudiera apagar el texto, ocho estados dejarían de existir. Por eso la supresión efectiva
    /// se calcula acá adentro y no la decide quien instancia el control — no hay manera de escribir
    /// el código que la apague en modo quieto, que es exactamente la intención.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty IsSuppressedProperty = DependencyProperty.Register(
        nameof(IsSuppressed),
        typeof(bool),
        typeof(StatePill),
        new PropertyMetadata(false, (d, _) => ((StatePill)d).Refresh()));

    /// <summary>Se pide que la píldora se calle. En movimiento reducido no se le hace caso.</summary>
    internal bool IsSuppressed
    {
        get => (bool)GetValue(IsSuppressedProperty);
        set => SetValue(IsSuppressedProperty, value);
    }

    /// <summary>
    /// El canal que arbitra estados y ánimos. Tiene que ser el mismo que el del cuerpo.
    /// </summary>
    /// <remarks>
    /// Si el orbe y la píldora tienen canales distintos van a decidir lo mismo casi siempre —las dos
    /// decisiones son deterministas y llegan con los mismos datos— pero no exactamente al mismo
    /// tiempo, y una píldora que dice «pensando» un cuadro después que el cuerpo se ve como un
    /// desperfecto. Compartirlo también es lo que hace que un ánimo disparado sobre el orbe aparezca
    /// escrito acá.
    /// </remarks>
    internal OrbStateChannel Channel
    {
        get => _channel;
        set
        {
            _channel.Changed -= OnChannelChanged;
            _channel = value;
            _channel.Changed += OnChannelChanged;
            Refresh();
        }
    }

    /// <inheritdoc />
    public void ShowMood(OrbMood mood)
    {
        _channel.RequestMood(mood);
        Refresh();
        Start();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Se mide una vez con la combinación más larga de todas y ya no vuelve a pedir layout: el
        // ancho que morfea se dibuja adentro, centrado. Animar el tamaño de un elemento obliga a
        // rehacer el layout sesenta veces por segundo, y esto está encima de un orbe que ya está
        // dibujando.
        if (_maxWidth <= 0)
        {
            _maxWidth = MeasureWidest();
        }

        return new Size(_maxWidth, PillHeight);
    }

    private double MeasureWidest()
    {
        var widestLabel = 0.0;
        foreach (var state in Enum.GetValues<AssistantVisualState>())
        {
            var name = OrbPalette.For(state).PillLabel;
            widestLabel = Math.Max(widestLabel, Text(name).WidthIncludingTrailingWhitespace);
            widestLabel = Math.Max(widestLabel, Text(Join(OrbQuiet.GroupFor(state).Name, name)).WidthIncludingTrailingWhitespace);

            foreach (var mood in Enum.GetValues<OrbMood>())
            {
                widestLabel = Math.Max(
                    widestLabel,
                    Text(Join(OrbMoods.Label(mood), name)).WidthIncludingTrailingWhitespace);
            }
        }

        // El detalle más largo posible: la cuenta regresiva del reintento más lejano.
        var widestDetail = 0.0;
        foreach (var sample in new[] { "reintento en 0:45", "reintentando", "hace 3 días", "10:00" })
        {
            widestDetail = Math.Max(widestDetail, Text(sample).WidthIncludingTrailingWhitespace);
        }

        return PaddingLeft + DotSize + Gap + widestLabel + Gap + widestDetail + PaddingRight;
    }

    private double WidthFor(string label, string detail)
    {
        if (label.Length == 0)
        {
            return 0;
        }

        var width = PaddingLeft + DotSize + Gap + Text(label).WidthIncludingTrailingWhitespace + PaddingRight;
        if (detail.Length > 0)
        {
            width += Gap + Text(detail).WidthIncludingTrailingWhitespace;
        }

        return width;
    }

    private FormattedText Text(string label) => new(
        label,
        CultureInfo.GetCultureInfo("es-AR"),
        System.Windows.FlowDirection.LeftToRight,
        Face,
        FontSize,
        Brushes.White,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Join(string first, string second) => first + " · " + second;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (StatePill)d;
        pill._channel.Request((AssistantVisualState)e.NewValue);
        pill.Refresh();
    }

    private void OnChannelChanged(object? sender, EventArgs e) => Refresh();

    private void OnQuietChanged(object? sender, EventArgs e)
    {
        // Cambió la preferencia de movimiento: puede que la píldora acabe de volverse obligatoria.
        _maxWidth = 0;
        InvalidateMeasure();
        Refresh();
    }

    /// <summary>Recalcula qué dice y cuánto mide, y arranca la animación si cambió algo.</summary>
    private void Refresh()
    {
        var state = _channel.State;
        var quiet = OrbQuiet.IsReduced;

        // Acá está la regla: el pedido de silencio no vale en movimiento reducido.
        var silent = IsSuppressed && !quiet;

        string label;
        if (silent)
        {
            label = string.Empty;
        }
        else if (_channel.Mood is { } mood)
        {
            // El ánimo manda sobre el estado mientras dura, pero no lo tapa: la píldora dice las dos
            // cosas. Si dijera sólo «¡listo!» habría que mirar el orbe para saber sobre qué.
            label = Join(OrbMoods.Label(mood), OrbPalette.For(state).PillLabel);
        }
        else if (quiet)
        {
            // Sin movimiento la forma sólo carga siete siluetas: la píldora dice cuál es y además
            // cuál de los quince estados cayó en ella.
            label = Join(OrbQuiet.GroupFor(state).Name, OrbPalette.For(state).PillLabel);
        }
        else
        {
            label = state == AssistantVisualState.Idle ? string.Empty : OrbPalette.For(state).PillLabel;
        }

        var detail = label.Length == 0 ? string.Empty : DetailFor(state);

        if (label != _label || detail != _detail)
        {
            _label = label;
            _detail = detail;
            _targetWidth = WidthFor(label, detail);
            _targetOpacity = label.Length == 0 ? 0 : 1;

            // Si estaba invisible aparece ya con el ancho nuevo: morfear desde cero se ve como que
            // la píldora crece desde un punto, y eso es una animación de aparición, no de cambio.
            if (_opacity <= 0.001)
            {
                _width = _targetWidth;
            }

            Start();
        }

        SyncDetailClock(state, label.Length > 0);
        InvalidateVisual();
    }

    /// <summary>
    /// Lo que va a la derecha del nombre del estado. Es la parte que cambia sola con el reloj.
    /// </summary>
    private string DetailFor(AssistantVisualState state)
    {
        if (state == AssistantVisualState.Deaf)
        {
            return OrbDeafRetry.Caption(_channel.Retry);
        }

        if (state == AssistantVisualState.WaitingForYou)
        {
            return OrbAge.Caption(_channel.WaitingAge);
        }

        if (state is not (AssistantVisualState.Thinking or AssistantVisualState.Attention
            or AssistantVisualState.Background))
        {
            return string.Empty;
        }

        var elapsed = _channel.TimeInState.TotalSeconds;
        if (elapsed <= CounterAfterSeconds)
        {
            return string.Empty;
        }

        // El boceto formatea siempre como «0:SS» y a los sesenta segundos escribe «0:75». Acá se
        // cuenta bien: una tarea que pasa el minuto es justamente la que más necesita que el número
        // se entienda.
        var total = (int)elapsed;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{total / 60}:{total % 60:00}");
    }

    /// <summary>
    /// El reloj del detalle late una vez por segundo, no una vez por cuadro.
    /// </summary>
    /// <remarks>
    /// Una cuenta regresiva es un reloj, no una animación. Colgarla del bucle de render obligaría a
    /// Viernes a dibujar sesenta veces por segundo mientras espera —y esperar es justo lo que hace
    /// la mayor parte del día—, para mover un número cada mil milisegundos.
    /// </remarks>
    private void SyncDetailClock(AssistantVisualState state, bool visible)
    {
        var needed = visible && state is AssistantVisualState.Deaf or AssistantVisualState.WaitingForYou
            or AssistantVisualState.Thinking or AssistantVisualState.Attention
            or AssistantVisualState.Background;

        if (needed == _detailClock.IsEnabled)
        {
            return;
        }

        if (needed)
        {
            _detailClock.Start();
        }
        else
        {
            _detailClock.Stop();
        }
    }

    private void RefreshDetail()
    {
        // Reafirma el pedido en cada cuadro, igual que los cuerpos: la propiedad es el pedido y el
        // canal es lo que se ve, y volver a escribirle el mismo valor a la propiedad no notifica
        // nada. Sin esto, una divergencia entre los dos se queda pegada.
        _channel.Poll(State);
        Refresh();
    }

    /// <summary>
    /// Engancha el bucle de render, pero sólo si la píldora está en el árbol.
    /// </summary>
    /// <remarks>
    /// La guarda de <see cref="FrameworkElement.IsLoaded"/> no es de más. <c>Unloaded</c> suelta los
    /// dos eventos y para el reloj, pero los callbacks de las propiedades siguen vivos: cualquier
    /// enlace que le escriba <see cref="State"/> o <see cref="IsSuppressed"/> después de sacarla del
    /// árbol llega igual a <c>Refresh</c>, y de ahí acá. Y <see cref="CompositionTarget.Rendering"/>
    /// es estático: suscribirse desde un elemento descargado lo vuelve a enraizar y lo deja
    /// dibujando —e invalidando visual— hasta que se cierre la aplicación.
    /// </remarks>
    private void Start()
    {
        if (_isRunning || !IsLoaded)
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

        // Mientras haya un ánimo vivo o algo en la cola, la píldora es la que hace correr el canal.
        // Si comparte canal con un cuerpo, esto no cuesta nada: el reloj del canal es absoluto y
        // consultarlo dos veces no lo adelanta.
        _channel.Poll();

        _width = Approach(_width, _targetWidth, delta / MorphSeconds);
        _opacity = Approach(_opacity, _targetOpacity, delta / FadeSeconds);

        InvalidateVisual();

        // Cuando ya no queda nada moviéndose se baja del bucle de render. Una píldora quieta no
        // tiene por qué costar un cuadro por cuadro en una aplicación que está encendida todo el día.
        if (_channel.Mood is null &&
            !_channel.HasQueue &&
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

        // El punto mantiene el color del ESTADO aunque haya un ánimo puesto: el ánimo se lee en el
        // texto y en el cuerpo, y si el punto también cambiara de color el estado de fondo
        // desaparecería justo cuando más falta hace saber sobre qué pasó lo que pasó.
        var state = _channel.State;
        var accent = OrbNight.Tint(OrbQuiet.Apply(OrbPalette.For(state), state).Body, night);

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
        var textLeft = left + PaddingLeft + DotSize + Gap;
        context.DrawText(text, new Point(textLeft, (PillHeight - text.Height) / 2));

        if (_detail.Length == 0)
        {
            context.Pop();
            return;
        }

        // El detalle va más apagado que el nombre: es un dato de apoyo, y si compitiera en peso la
        // píldora tendría dos titulares.
        var detail = Text(_detail);
        detail.SetForegroundBrush(new SolidColorBrush(light
            ? Color.FromArgb(0x9E, 0, 0, 0)
            : Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF)));
        context.DrawText(
            detail,
            new Point(textLeft + text.WidthIncludingTrailingWhitespace + Gap, (PillHeight - detail.Height) / 2));

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
