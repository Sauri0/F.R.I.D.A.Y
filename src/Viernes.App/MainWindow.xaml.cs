using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Viernes.App.Controls;
using Viernes.App.Services;
using Viernes.App.Shell;
using Viernes.App.ViewModels;
using Binding = System.Windows.Data.Binding;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App;

/// <summary>
/// La ventana: un orbe que se arrastra y un vidrio que se despliega al lado.
/// </summary>
/// <remarks>
/// La ventana mide siempre lo mismo —el desplegable más ancho, el más alto y el aire de las sombras—
/// y es transparente. Lo que cambia de forma es el vidrio de adentro. Antes cambiaba el tamaño de la
/// ventana y, como el orbe está anclado a una esquina, había que corregir <c>Left</c> y <c>Top</c> en
/// el mismo cuadro: eso es lo que se veía como un salto cada vez que se abría un panel.
/// <para>
/// Una ventana grande y transparente por encima del escritorio no molesta: siendo <em>layered</em>,
/// Windows deja pasar el clic por los píxeles con alfa cero. Donde no hay vidrio ni orbe, el clic va
/// a parar a lo que haya abajo.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = -1;

    /// <summary>Curva de las transiciones del vidrio. Sale medida de la referencia.</summary>
    private static readonly KeySpline GlassEase = new(0.22, 0.68, 0.32, 1);

    private readonly MainViewModel _viewModel;
    private readonly WindowPlacementStore _placementStore;
    private readonly OrbMotion _motion = new();
    private readonly OrbPresence _presence = new();
    private readonly HoldToAuthorize _spendHold = new();

    private bool _pushToTalkActive;
    private CancellationTokenSource? _pushToTalkCancellation;
    private bool _opensRight = true;
    private bool _panelShown;
    private bool _fastMove;
    private bool _hidingToTray;
    private Rect _workArea = Rect.Empty;
    private int _workAreaAge;
    private double _writtenLeft = double.NaN;
    private double _writtenTop = double.NaN;
    private TimeSpan _lastRender;
    private Point _pressOrigin;
    private bool _pressPending;
    private bool _pressStartedOnButton;

    /// <summary>
    /// Memoria de dónde va el orbe en cada monitor, y el vigía que lo lleva al que estás usando.
    /// </summary>
    /// <remarks>
    /// Con dos pantallas el orbe se quedaba clavado en la que le tocó al arrancar. Trabajás en una y
    /// el asistente vive en la otra: para hablarle hay que girar la cabeza, y lo que muestra —los
    /// pasos, la respuesta— pasa en un monitor que no estás mirando.
    /// </remarks>
    private readonly Services.MonitorSlots _monitors = new();

    private System.Windows.Threading.DispatcherTimer? _followTimer;
    private string _currentMonitor = string.Empty;
    private int _stableTicks;

    internal MainWindow(MainViewModel viewModel, WindowPlacementStore placementStore)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _placementStore = placementStore;
        DataContext = viewModel;

        Width = ShellLayout.WindowWidth;
        Height = ShellLayout.WindowHeight;
        Stage.Width = ShellLayout.WindowWidth;
        Stage.Height = ShellLayout.WindowHeight;

        SpendHoldHost.Content = _spendHold;
        _spendHold.Authorized += (_, _) => _viewModel.ClosePanel();

        ApplyOrbShape(viewModel.OrbShape);
        ApplySide(opensRight: true, force: true);
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        // El latido sólo existe en la gota: la nube tiene su propio vocabulario y no lo comparte.
        _viewModel.StepAdvanced += (_, _) => OrbHost.Children
            .OfType<LiquidOrb>()
            .FirstOrDefault()
            ?.Beat();
    }

    /// <summary>
    /// Cambia el cuerpo en vivo. Los dos leen el mismo estado, así que la elección no altera nada
    /// del comportamiento: es puramente cómo se ve.
    /// </summary>
    private void ApplyOrbShape(OrbShape shape)
    {
        OrbHost.Children.Clear();

        if (shape == OrbShape.Nube)
        {
            var nube = new NubeOrb { Width = ShellLayout.OrbSize, Height = ShellLayout.OrbSize };
            nube.SetBinding(NubeOrb.StateProperty, new Binding(nameof(MainViewModel.State)) { Source = _viewModel });
            OrbHost.Children.Add(nube);
            return;
        }

        var gota = new LiquidOrb();
        gota.SetBinding(LiquidOrb.StateProperty, new Binding(nameof(MainViewModel.State)) { Source = _viewModel });
        gota.SetBinding(
            LiquidOrb.IsMicrophoneArmedProperty,
            new Binding(nameof(MainViewModel.IsMicrophoneActive)) { Source = _viewModel });
        gota.SetBinding(
            LiquidOrb.AudioLevelProperty,
            new Binding(nameof(MainViewModel.AudioLevel)) { Source = _viewModel });
        gota.SetBinding(
            LiquidOrb.HasSpendAuthorizationProperty,
            new Binding(nameof(MainViewModel.HasSpendAuthorization)) { Source = _viewModel });
        OrbHost.Children.Add(gota);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreOrbPlacement();
        Glass.Variant = DesktopGlass.Resolve(this);
        DesktopGlass.TryApplySystemBackdrop(this);
        StartSweep();
        StartFollowingActiveMonitor();
        CompositionTarget.Rendering += OnRendering;

        try
        {
            await _viewModel.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Viernes initialization failed: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// El barrido: una banda de luz que cruza el vidrio y después espera. La pausa larga es lo que lo
    /// hace leerse como un reflejo de algo que pasó, y no como una animación en bucle.
    /// </summary>
    private void StartSweep()
    {
        var travel = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(12),
            RepeatBehavior = RepeatBehavior.Forever
        };
        travel.KeyFrames.Add(new LinearDoubleKeyFrame(-160, KeyTime.FromPercent(0)));
        travel.KeyFrames.Add(new SplineDoubleKeyFrame(560, KeyTime.FromPercent(0.22), new KeySpline(0.55, 0, 0.45, 1)));
        travel.KeyFrames.Add(new LinearDoubleKeyFrame(560, KeyTime.FromPercent(1)));
        SweepOffset.BeginAnimation(TranslateTransform.XProperty, travel);

        // La barra del turno no promete un porcentaje: va y viene para decir que algo se mueve.
        var pulse = new DoubleAnimation(0, 240, TimeSpan.FromSeconds(1.6))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        TurnPulseOffset.BeginAnimation(TranslateTransform.XProperty, pulse);
    }

    /// <summary>
    /// Lee la última posición guardada. Lo que se guarda es la esquina del orbe, no la de la ventana:
    /// la ventana es un marco con aire alrededor y su esquina no significa nada para el usuario.
    /// </summary>
    private void RestoreOrbPlacement()
    {
        var workArea = CurrentWorkArea;
        var bounds = ShellLayout.OrbBounds(workArea);

        _placementStore.Restore(this);
        var orb = new Point(Left, Top);

        // Sin archivo guardado, el almacén propone la esquina de una ventana del tamaño de ésta. Como
        // acá la ventana es mucho más grande que el orbe, esa esquina deja al orbe lejos del borde:
        // en ese caso —y sólo en ese— se prefiere el rincón de abajo a la derecha, que es donde un
        // asistente de escritorio espera aparecer la primera vez.
        var fallbackLeft = workArea.Right - ShellLayout.WindowWidth - 24;
        var fallbackTop = workArea.Bottom - ShellLayout.WindowHeight - 24;
        if (Math.Abs(orb.X - fallbackLeft) < 1 && Math.Abs(orb.Y - fallbackTop) < 1)
        {
            orb = new Point(bounds.Right, bounds.Bottom);
        }

        _motion.Teleport(new Point(
            Math.Clamp(orb.X, bounds.Left, bounds.Right),
            Math.Clamp(orb.Y, bounds.Top, bounds.Bottom)));

        ApplySide(ShellLayout.ShouldOpenRight(_motion.Position, workArea), force: true);
        WriteWindowPosition();
    }

    /// <summary>
    /// El cuadro. Acá se integra la física, se resuelve la presencia y se escribe la posición de la
    /// ventana una sola vez.
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        var dt = _lastRender == default ? 1.0 / 60 : (rendering.RenderingTime - _lastRender).TotalSeconds;
        _lastRender = rendering.RenderingTime;
        if (dt <= 0)
        {
            return;
        }

        var workArea = CachedWorkArea();
        var bounds = ShellLayout.OrbBounds(workArea);

        if (_motion.IsDragging)
        {
            // Durante el arrastre la posición la manda Windows: acá sólo se anota, para poder soltar
            // el orbe con la velocidad que traía.
            _motion.ReportDragged(ShellLayout.OrbOriginFor(new Point(Left, Top), _opensRight), dt);
        }
        else
        {
            AdoptExternalMove();
            _motion.ClampInto(bounds);
            _motion.Step(dt, bounds);
            WriteWindowPosition();
        }

        _presence.Step(dt);
        ApplyPresence();
        UpdateSide(workArea);
        UpdatePanelVisibility();
        UpdateDictationLevel();

        if (_hidingToTray && _presence.IsGone)
        {
            _hidingToTray = false;
            FinishHiding();
        }
    }

    /// <summary>
    /// El área útil, medida una vez cada veinte cuadros.
    /// </summary>
    /// <remarks>
    /// Averiguarla cuesta tres llamadas al sistema —el handle, el monitor, el DPI— y en el bucle de
    /// cuadro eso es sesenta veces por segundo para un dato que cambia cuando enchufás un monitor.
    /// Un tercio de segundo de desfase no se nota; lo que sí se nota es el orbe pagando ese peaje.
    /// </remarks>
    private Rect CachedWorkArea()
    {
        if (_workAreaAge++ % 20 == 0 || _workArea.IsEmpty)
        {
            _workArea = CurrentWorkArea;
        }

        return _workArea;
    }

    /// <summary>
    /// Si alguien más movió la ventana —el vigía de monitores, la llegada desde la bandeja—, la
    /// física adopta esa posición en vez de pelearse con ella.
    /// </summary>
    private void AdoptExternalMove()
    {
        if (double.IsNaN(_writtenLeft))
        {
            return;
        }

        if (Math.Abs(Left - _writtenLeft) < 0.5 && Math.Abs(Top - _writtenTop) < 0.5)
        {
            return;
        }

        _motion.Teleport(ShellLayout.OrbOriginFor(new Point(Left, Top), _opensRight));
    }

    private void WriteWindowPosition()
    {
        var origin = ShellLayout.WindowOriginFor(_motion.Position, _opensRight);
        if (Math.Abs(origin.X - Left) > 0.05)
        {
            Left = origin.X;
        }

        if (Math.Abs(origin.Y - Top) > 0.05)
        {
            Top = origin.Y;
        }

        _writtenLeft = Left;
        _writtenTop = Top;
    }

    private void ApplyPresence()
    {
        var scale = _presence.Scale;
        var offset = _presence.Offset;

        OrbPresenceScale.ScaleX = scale;
        OrbPresenceScale.ScaleY = scale;
        OrbPresenceOffset.X = offset.X;
        OrbPresenceOffset.Y = offset.Y;
        OrbDragSurface.Opacity = _presence.Opacity;
        OrbOverlay.Opacity = _presence.Opacity;

        var gone = _presence.IsGone;
        OrbDragSurface.IsHitTestVisible = !gone;
        OrbDragSurface.Visibility = gone ? Visibility.Hidden : Visibility.Visible;
        OrbOverlay.Visibility = gone ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>
    /// El panel se abre hacia donde hay lugar. Si el orbe cruza la mitad de la pantalla, el vidrio se
    /// espeja: cambian las esquinas, el lado por el que entra la luz y el origen de la escala.
    /// </summary>
    private void UpdateSide(Rect workArea)
    {
        var shouldOpenRight = ShellLayout.ShouldOpenRight(_motion.Position, workArea);
        if (shouldOpenRight != _opensRight)
        {
            ApplySide(shouldOpenRight, force: false);
        }
    }

    private void ApplySide(bool opensRight, bool force)
    {
        if (!force && opensRight == _opensRight)
        {
            return;
        }

        _opensRight = opensRight;

        var orbLeft = opensRight ? ShellLayout.OrbLeftWhenOpeningRight : ShellLayout.OrbLeftWhenOpeningLeft;
        Canvas.SetLeft(OrbDragSurface, orbLeft);
        Canvas.SetTop(OrbDragSurface, ShellLayout.OrbTop);
        Canvas.SetLeft(OrbOverlay, orbLeft);
        Canvas.SetTop(OrbOverlay, ShellLayout.OrbTop);
        Canvas.SetLeft(PanelHost, ShellLayout.PanelHostLeft(opensRight));

        Glass.OpensRight = opensRight;
        Glass.HorizontalAlignment = opensRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Glass.RenderTransformOrigin = opensRight ? new Point(0, 0.5) : new Point(1, 0.5);

        // El brillo no se espeja: la luz viene del ambiente, no del panel. La burbuja sí, porque su
        // esquina chica es la que apunta al orbe.
        DictationBubble.HorizontalAlignment = opensRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DictationBubble.Margin = opensRight
            ? new Thickness(2, 0, 0, 116)
            : new Thickness(0, 0, 2, 116);
        DictationBubble.CornerRadius = opensRight
            ? new CornerRadius(20, 20, 20, 7)
            : new CornerRadius(20, 20, 7, 20);

        WriteWindowPosition();
    }

    /// <summary>
    /// El vidrio se retrae mientras el orbe está agarrado o viaja rápido: un panel arrastrado por la
    /// pantalla se lee como una ventana que persigue al mouse.
    /// </summary>
    private void UpdatePanelVisibility()
    {
        if (_fastMove)
        {
            _fastMove = _motion.Speed > 170;
        }
        else
        {
            _fastMove = _motion.Speed > 340;
        }

        var shouldShow = _viewModel.IsPanelOpen &&
            !_presence.IsGone &&
            !_motion.IsDragging &&
            !_fastMove;

        if (shouldShow == _panelShown)
        {
            return;
        }

        _panelShown = shouldShow;
        AnimatePanel(shouldShow);
    }

    private void AnimatePanel(bool show)
    {
        if (show)
        {
            PanelHost.Visibility = Visibility.Visible;
            ApplyPanelShape(animate: false);
        }

        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (!show)
        {
            fade.Completed += (_, _) =>
            {
                if (!_panelShown)
                {
                    PanelHost.Visibility = Visibility.Collapsed;
                }
            };
        }

        Glass.BeginAnimation(OpacityProperty, fade);

        var pop = new DoubleAnimation(show ? 1 : 0.93, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut }
        };
        GlassScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        GlassScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>
    /// El alto y el ancho del vidrio se interpolan; nunca se cierra para volver a abrirse. Es el mismo
    /// objeto cambiando de forma, y eso es lo que lo hace sentir un objeto y no una ventana.
    /// </summary>
    private void ApplyPanelShape(bool animate)
    {
        var spec = _viewModel.ActivePanelSpec;
        Glass.Family = spec.Family;

        if (!animate)
        {
            Glass.BeginAnimation(WidthProperty, null);
            Glass.BeginAnimation(HeightProperty, null);
            Glass.Width = spec.Width;
            Glass.Height = spec.Height;
            return;
        }

        Glass.BeginAnimation(WidthProperty, Morph(spec.Width, 320));
        Glass.BeginAnimation(HeightProperty, Morph(spec.Height, 360));
    }

    private static DoubleAnimationUsingKeyFrames Morph(double to, int milliseconds)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), GlassEase));
        return animation;
    }

    private void UpdateDictationLevel()
    {
        if (DictationBubble.Visibility != Visibility.Visible)
        {
            return;
        }

        DictationLevel.Width = Math.Clamp(_viewModel.AudioLevel, 0, 1) * 60;
    }

    private void Panel_MouseEnter(object sender, MouseEventArgs e) => _viewModel.IsPanelHovered = true;

    private void Panel_MouseLeave(object sender, MouseEventArgs e) => _viewModel.IsPanelHovered = false;

    /// <summary>
    /// Lleva el orbe al monitor donde estás trabajando, sin perseguir el mouse.
    /// </summary>
    /// <remarks>
    /// Se mide el monitor bajo el cursor, pero no se salta apenas cambia: hay que quedarse ahí un
    /// rato. Cruzar la pantalla para llegar a un botón no es «me mudé de monitor», y un orbe que
    /// sigue al mouse en tiempo real es un moscardón.
    /// <para>
    /// La posición de cada monitor se recuerda por separado. Y no se mueve mientras hay un panel
    /// abierto: sería sacarle de las manos algo que estás leyendo.
    /// </para>
    /// </remarks>
    private void StartFollowingActiveMonitor()
    {
        _currentMonitor = Services.MonitorSlots.MonitorAt(_motion.Position).Key;

        _followTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };

        _followTimer.Tick += (_, _) =>
        {
            if (_viewModel.IsPanelOpen || _motion.IsDragging || !IsVisible)
            {
                _stableTicks = 0;
                return;
            }

            var (key, workArea) = Services.MonitorSlots.MonitorUnderCursor();
            if (key == _currentMonitor)
            {
                _stableTicks = 0;
                return;
            }

            // Tres tics seguidos en el otro monitor: unos dos segundos. Menos que eso y se muda al
            // pasar el mouse de largo.
            if (++_stableTicks < 3)
            {
                return;
            }

            _stableTicks = 0;
            _monitors.Remember(_currentMonitor, _motion.Position);

            var destination = _monitors.SlotFor(
                key,
                workArea,
                new Size(ShellLayout.OrbSize, ShellLayout.OrbSize));

            var bounds = ShellLayout.OrbBounds(workArea);
            _motion.Teleport(new Point(
                Math.Clamp(destination.X, bounds.Left, bounds.Right),
                Math.Clamp(destination.Y, bounds.Top, bounds.Bottom)));
            ApplySide(ShellLayout.ShouldOpenRight(_motion.Position, workArea), force: false);
            WriteWindowPosition();
            _currentMonitor = key;
            SaveOrbPlacement();
        };

        _followTimer.Start();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveOrbPlacement();
        if (App.Current.IsExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _followTimer?.Stop();
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActivePanelSpec))
        {
            ApplyPanelShape(animate: _panelShown);
        }
        else if (e.PropertyName == nameof(MainViewModel.OrbShape))
        {
            ApplyOrbShape(_viewModel.OrbShape);
        }
    }

    /// <summary>
    /// Presionar en cualquier parte del orbe arrastra; si se suelta sin haberse movido, cuenta como
    /// toque y abre el panel. Antes había que apuntar al aro exterior, que a 108 px es una franja
    /// de pocos píxeles y volvía tedioso algo que se hace todo el tiempo.
    /// </summary>
    /// <remarks>
    /// Acá sólo se anota dónde empezó la presión: el evento sigue viaje. Antes este handler marcaba
    /// <c>e.Handled</c> y llamaba a <c>DragMove</c> en un evento de túnel colgado del contenedor, así
    /// que el clic moría antes de llegar a <c>OrbButton</c>: el botón nunca tomaba foco de teclado
    /// —que es la precondición del push-to-talk— y el trigger <c>IsPressed</c> de su plantilla era
    /// código muerto. El arrastre no puede compartirse desde acá porque <c>DragMove</c> no vuelve
    /// hasta que soltás el botón; por eso arranca recién cuando el mouse se movió de verdad.
    /// </remarks>
    private void Orb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _pressPending = true;
        _pressStartedOnButton = IsWithin(e.OriginalSource as DependencyObject, OrbButton);
        _pressOrigin = PointToScreen(e.GetPosition(this));
    }

    /// <summary>
    /// El arrastre empieza cuando el mouse se movió lo suficiente, no al presionar.
    /// </summary>
    /// <remarks>
    /// Los cuatro píxeles de tolerancia son los mismos que antes decidían «esto fue un toque»: si no
    /// se llegan a recorrer, nadie mueve nada y el toque lo resuelve el botón.
    /// <para>
    /// <c>DragMove</c> no vuelve hasta que se suelta el botón, así que la inercia se arma con lo que
    /// el bucle de cuadro fue anotando mientras tanto: soltar es empezar a volar con esa velocidad.
    /// </para>
    /// </remarks>
    private void Orb_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressPending)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Se soltó fuera de la ventana y el MouseUp nunca llegó: la presión ya no existe.
            _pressPending = false;
            return;
        }

        if ((PointToScreen(e.GetPosition(this)) - _pressOrigin).Length <= 4)
        {
            return;
        }

        _pressPending = false;

        // La captura se suelta ANTES, no después. Un Button de WPF toma el mouse al presionarlo, y
        // con el mouse capturado por otro elemento DragMove no arranca: tira InvalidOperationException
        // y el orbe queda clavado. Era exactamente eso —«no puedo arrastrarla manteniendo apretada»—
        // y lo introdujo el arreglo que hizo que el clic llegara al botón: el botón empezó a recibir
        // el clic y, con él, la captura.
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        _motion.BeginDrag();

        try
        {
            // DragMove no vuelve hasta que se suelta el botón.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // El botón se soltó antes de que arrancara el arrastre; no hay nada que guardar distinto.
        }

        _motion.ReportDragged(ShellLayout.OrbOriginFor(new Point(Left, Top), _opensRight), 1.0 / 60);
        _motion.Drop();
        SaveOrbPlacement();
    }

    /// <summary>
    /// Soltar sin haber arrastrado es un toque. Sobre el botón lo resuelve su propio Click.
    /// </summary>
    private void Orb_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressPending)
        {
            return;
        }

        _pressPending = false;

        // Adelantarse al Click del botón abriría el panel dos veces, y dos veces es abrir y cerrar.
        if (_pressStartedOnButton)
        {
            return;
        }

        HandleOrbTap();
    }

    private void OrbButton_Click(object sender, RoutedEventArgs e) => HandleOrbTap();

    /// <summary>
    /// Un toque en el orbe abre la conversación y el campo de texto, o la cierra si ya estaba.
    /// </summary>
    /// <remarks>
    /// Al cerrar, el foco se queda en el orbe a propósito: el push-to-talk por barra espaciadora
    /// exige que <c>OrbButton</c> tenga el foco de teclado, y mandarlo a un campo de texto invisible
    /// lo dejaba inalcanzable. Al abrir sí se va al campo, que es donde vas a escribir.
    /// </remarks>
    private void HandleOrbTap()
    {
        if (_presence.IsGone)
        {
            _presence.Aparecer();
            return;
        }

        Keyboard.Focus(OrbButton);
        _viewModel.OpenTextInput();

        if (_viewModel.IsExpanded)
        {
            PromptTextBox.Focus();
            Keyboard.Focus(PromptTextBox);
        }
    }

    /// <summary>Si el clic nació dentro del botón, para no resolver el toque dos veces.</summary>
    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    /// <summary>
    /// Guardarse: el orbe se encoge hacia el borde más cercano y recién ahí la ventana desaparece.
    /// </summary>
    /// <remarks>
    /// La ventana se oculta cuando la animación terminó, no antes: esconder algo que todavía se está
    /// yendo es un corte, y un corte no dice a dónde se fue.
    /// </remarks>
    private void HideToTray()
    {
        CancelActiveVoice();
        _viewModel.ClosePanel();
        SaveOrbPlacement();

        var centre = new Point(
            _motion.Position.X + ShellLayout.OrbSize / 2,
            _motion.Position.Y + ShellLayout.OrbSize / 2);
        _presence.Esconder(centre, CurrentWorkArea);
        _hidingToTray = true;
    }

    private void FinishHiding()
    {
        _ = _viewModel.SetShellVisibilityAsync(false, CancellationToken.None);
        Hide();
        App.Current.NotifyWindowVisibilityChanged(false);
    }

    /// <summary>
    /// Área de trabajo del monitor donde está el orbe, no la del principal.
    /// <see cref="SystemParameters.WorkArea"/> siempre devuelve la del primario, y usarla para
    /// acotar la posición es lo que arrastraba el orbe de vuelta a la pantalla 1.
    /// </summary>
    private Rect CurrentWorkArea
    {
        get
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                var screen = handle == nint.Zero
                    ? System.Windows.Forms.Screen.PrimaryScreen
                    : System.Windows.Forms.Screen.FromHandle(handle);
                if (screen is null)
                {
                    return SystemParameters.WorkArea;
                }

                // WinForms informa píxeles físicos; WPF trabaja en unidades independientes del DPI.
                var dpi = VisualTreeHelper.GetDpi(this);
                var area = screen.WorkingArea;
                return new Rect(
                    area.Left / dpi.DpiScaleX,
                    area.Top / dpi.DpiScaleY,
                    area.Width / dpi.DpiScaleX,
                    area.Height / dpi.DpiScaleY);
            }
            catch (Exception)
            {
                return SystemParameters.WorkArea;
            }
        }
    }

    /// <summary>
    /// La llegada desde la bandeja: el orbe se desprende del borde inferior derecho del monitor donde
    /// ya vive y aterriza en su lugar.
    /// </summary>
    internal void PlayArrivalFromTray()
    {
        var workArea = CurrentWorkArea;
        var bounds = ShellLayout.OrbBounds(workArea);
        var target = _motion.Position;

        var start = new Point(
            Math.Clamp(workArea.Right - 52, bounds.Left, bounds.Right),
            Math.Clamp(workArea.Bottom - 34, bounds.Top, bounds.Bottom));

        _presence.Aparecer();

        if (Math.Abs(start.X - target.X) < 2 && Math.Abs(start.Y - target.Y) < 2)
        {
            return;
        }

        // La física escribe la posición de la ventana cuadro a cuadro, así que la llegada no se anima
        // con Storyboards sobre Left y Top: se teletransporta al punto de partida y se deja que el
        // resorte de reposo la lleve. Es el mismo movimiento que cuando la soltás cerca de un borde.
        _motion.Teleport(start);
        _motion.Nudge(target);
        ApplySide(ShellLayout.ShouldOpenRight(target, workArea), force: false);
        PlaySquashAndStretch(TimeSpan.FromMilliseconds(420));
    }

    private void PlaySquashAndStretch(TimeSpan travel)
    {
        // Se alarga mientras viaja rápido, se aplasta al llegar y se recompone con una oscilación.
        var stretch = new DoubleAnimationUsingKeyFrames { Duration = travel + TimeSpan.FromMilliseconds(240) };
        stretch.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(0.78, KeyTime.FromPercent(0.28)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(0.84, KeyTime.FromPercent(0.6)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.24, KeyTime.FromPercent(0.72)));
        stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            // El diseño prohíbe ElasticEase: a las ocho horas en pantalla, cada oscilación se siente.
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
        });

        var squash = new DoubleAnimationUsingKeyFrames { Duration = travel + TimeSpan.FromMilliseconds(240) };
        squash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.28)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.6)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.78, KeyTime.FromPercent(0.72)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            // El diseño prohíbe ElasticEase: a las ocho horas en pantalla, cada oscilación se siente.
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
        });

        OrbArrivalScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
        OrbArrivalScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
    }

    /// <summary>
    /// Trae el orbe al frente sin activar la ventana. Viernes puede aparecer mientras el usuario
    /// escribe en otra aplicación sin robarle el teclado; sigue siendo presencia, no interrupción.
    /// </summary>
    internal void ShowWithoutStealingFocus()
    {
        _presence.Aparecer();
        _hidingToTray = false;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>Guarda dónde quedó el orbe. Lo que se persiste es su esquina, no la de la ventana.</summary>
    internal void SaveOrbPlacement() =>
        _placementStore.Save(this, _motion.Position.X, _motion.Position.Y);

    internal void CancelActiveVoice()
    {
        if (!_pushToTalkActive)
        {
            return;
        }

        _pushToTalkActive = false;
        _pushToTalkCancellation?.Cancel();
        _pushToTalkCancellation?.Dispose();
        _pushToTalkCancellation = null;
        _ = _viewModel.CancelVoiceAsync(CancellationToken.None);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsPanelOpen)
        {
            e.Handled = true;
            _viewModel.ClosePanel();
            Keyboard.Focus(OrbButton);
            return;
        }

        if (e.Key == Key.Space && OrbButton.IsKeyboardFocused && !e.IsRepeat)
        {
            e.Handled = true;
            await BeginPushToTalkAsync();
        }
    }

    private async void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _pushToTalkActive)
        {
            e.Handled = true;
            await EndPushToTalkAsync();
        }
    }

    private void PromptTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && _viewModel.SendCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.SendCommand.Execute(null);
        }
    }

    private async Task BeginPushToTalkAsync()
    {
        if (_pushToTalkActive)
        {
            return;
        }

        _pushToTalkActive = true;
        _pushToTalkCancellation?.Dispose();
        _pushToTalkCancellation = new CancellationTokenSource();
        try
        {
            await _viewModel.StartPushToTalkAsync(_pushToTalkCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _pushToTalkActive = false;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Push-to-talk start failed: {exception.GetType().Name}");
            _pushToTalkActive = false;
        }
    }

    private async Task EndPushToTalkAsync()
    {
        if (!_pushToTalkActive)
        {
            return;
        }

        _pushToTalkActive = false;
        _pushToTalkCancellation?.Dispose();
        _pushToTalkCancellation = null;
        try
        {
            await _viewModel.StopPushToTalkAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Push-to-talk stop failed: {exception.GetType().Name}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
