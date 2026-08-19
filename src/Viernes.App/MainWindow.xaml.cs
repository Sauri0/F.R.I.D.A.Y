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

// El proyecto arrastra WinForms por la bandeja y los monitores, así que el menú existe dos veces.
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
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

    /// <summary>
    /// Cada cuánto se vuelve a mirar la hora y el tema del escritorio.
    /// </summary>
    /// <remarks>
    /// <b>Este número no sale del boceto</b>: el boceto corre en un navegador y no tiene ni hora ni
    /// tema del escritorio que releer. Se deduce de lo único que cambia con la hora acá adentro.
    /// <see cref="OrbNight.For"/> sube de 0 a 1 en la rampa de 22 a 23 y baja de 1 a 0 en la de 5 a
    /// 6 —una hora cada una—, así que el paso más grande que puede dar el modo madrugada entre una
    /// lectura y la siguiente es 2/60, un 3,3 %, y a ojo eso no existe. Preguntarlo por cuadro sería
    /// leer el registro de Windows sesenta veces por segundo para enterarse de algo que cambia dos
    /// veces al día.
    /// </remarks>
    private static readonly TimeSpan AmbienceInterval = TimeSpan.FromMinutes(2);

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

    /// <summary>Si hay un arrastre en curso. Mientras dure, la ventana tiene el mouse capturado.</summary>
    private bool _dragging;

    /// <summary>Dónde agarró el dedo, respecto de la esquina del orbe. Sin esto el orbe salta al dedo.</summary>
    private Vector _grab;

    /// <summary>
    /// El cuerpo que está puesto, para pasarle el movimiento cuadro a cuadro.
    /// </summary>
    /// <remarks>
    /// Se guarda en vez de buscarlo en <c>OrbHost.Children</c> cada cuadro: recorrer una colección
    /// de WPF sesenta veces por segundo asigna un enumerador cada vez para encontrar siempre al
    /// mismo hijo. Lo escribe <see cref="ApplyOrbShape"/>, que es el único que cambia el cuerpo.
    /// </remarks>
    private IOrbMotionSink? _orbMotion;

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
    private System.Windows.Threading.DispatcherTimer? _ambienceTimer;
    private string _currentMonitor = string.Empty;
    private int _stableTicks;

    /// <summary>
    /// Cada cuánto se pregunta si lo que está adelante ocupa el monitor entero.
    /// </summary>
    /// <remarks>
    /// Un segundo. Son tres llamadas al sistema y una comparación de rectángulos, y entrar o salir de
    /// pantalla completa es algo que el usuario hace, no algo que pasa sesenta veces por segundo.
    /// <para>
    /// Se eligió preguntar y no engancharse a <c>SetWinEventHook</c> por costo de riesgo, no de CPU:
    /// un <em>hook</em> global mete una llamada de vuelta del sistema en el hilo de la interfaz, y si
    /// el delegado se recolecta el proceso se cae. Un segundo de demora en esconderse no lo nota
    /// nadie; ese cuelgue sí.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan FullScreenInterval = TimeSpan.FromSeconds(1);

    /// <summary>Cuánto dura la píldora sola sobre una pantalla completa antes de retraerse al filete.</summary>
    /// <remarks>Cuatro segundos, del LEEME de la referencia.</remarks>
    private static readonly TimeSpan UrgentPillWindow = TimeSpan.FromSeconds(4);

    private System.Windows.Threading.DispatcherTimer? _fullScreenTimer;
    private UrgentSliver? _sliver;
    private bool _sliverShown;
    private bool _fullScreen;
    private bool _hiddenByFullScreen;

    /// <summary>Pasó algo urgente mientras había una pantalla completa adelante, y todavía no lo vio.</summary>
    private bool _urgentPending;

    /// <summary>Mientras esto está puesto se dibuja la píldora y nada más: ni cuerpo ni desplegable.</summary>
    private bool _pillOnly;

    private System.Windows.Threading.DispatcherTimer? _pillTimer;

    /// <summary>La mudanza de monitor en vuelo, o <c>null</c> si el orbe está donde vive.</summary>
    private MonitorTravel? _travel;

    /// <summary>El medio segundo entre irse por un borde y volver por el otro.</summary>
    private System.Windows.Threading.DispatcherTimer? _crossTimer;

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

        Pill.SetBinding(StatePill.StateProperty, new Binding(nameof(MainViewModel.State)) { Source = _viewModel });
        Pill.SetBinding(
            StatePill.IsSuppressedProperty,
            new Binding(nameof(MainViewModel.IsStatePillSuppressed)) { Source = _viewModel });

        ApplyOrbShape(viewModel.OrbShape);
        ApplySide(opensRight: true, force: true);
        MeasurePillSlack();
        BuildOrbMenu();

        // Los tres se sueltan en Window_Closed. El ViewModel vive más que la ventana —lo crea la
        // aplicación y sobrevive a esconderse en la bandeja—, así que una ventana cerrada que
        // siguiera colgada de sus eventos se quedaría repartiendo latidos y ánimos a un árbol
        // visual muerto. Con una sola ventana por sesión no se nota; el día que se recree, sí.
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.StepAdvanced += ViewModelOnStepAdvanced;
        _viewModel.MoodShown += ViewModelOnMoodShown;

        // El cuarto: un recordatorio que llega con una pantalla completa adelante es lo único urgente
        // que el modelo de vista no publica como propiedad.
        _viewModel.ActivationRequested += ViewModelOnActivationRequested;
    }

    /// <summary>
    /// El menú del botón derecho: la puerta de todo lo que el usuario abre a mano.
    /// </summary>
    /// <remarks>
    /// Se arma en código y no en el XAML por una razón concreta: acá <see cref="PanelKind"/> es un
    /// tipo y el compilador verifica cada entrada. En el marcado sería una cadena —igual que el
    /// <c>ConverterParameter</c> de cada panel—, y una cadena mal escrita no la detecta nadie: el
    /// menú abre un desplegable vacío y nada avisa.
    /// <para>
    /// No están los diecinueve. Los que se deducen del estado —escribir, trabajando, permiso,
    /// política, sin red, recordatorio— llegan solos y ponerlos acá sería pedirle al usuario que
    /// invoque un error. Presupuesto tampoco: es una autorización de gasto, y una autorización se
    /// pide cuando hace falta, no se va a buscar a un menú.
    /// </para>
    /// </remarks>
    private void BuildOrbMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("MenuDelOrbe")
        };

        AddPanelItem(menu, "Misiones abiertas", PanelKind.Misiones);
        AddPanelItem(menu, "La pregunta pendiente", PanelKind.Pregunta);
        AddPanelItem(menu, "Proyectos", PanelKind.Proyectos);
        AddSeparator(menu);
        AddPanelItem(menu, "Lo que aprendí de vos", PanelKind.Aprendido);
        AddPanelItem(menu, "Permisos que me diste", PanelKind.Autonomia);
        AddSeparator(menu);
        AddPanelItem(menu, "Cuánto llevo gastado", PanelKind.Consumo);
        AddPanelItem(menu, "Gastos", PanelKind.Gastos);
        AddPanelItem(menu, "Caja", PanelKind.Caja);
        AddSeparator(menu);
        AddPanelItem(menu, "Muestras", PanelKind.Muestras);
        AddPanelItem(menu, "Música", PanelKind.Musica);
        AddSeparator(menu);
        AddSimpleItem(menu, "Guardarse en la bandeja", HideToTray);

        OrbDragSurface.ContextMenu = menu;
    }

    private void AddPanelItem(ContextMenu menu, string header, PanelKind kind)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("ItemDelMenu")
        };

        item.Click += (_, _) => _viewModel.ShowPanel(kind);
        menu.Items.Add(item);
    }

    private void AddSimpleItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("ItemDelMenu")
        };

        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void AddSeparator(ContextMenu menu) =>
        menu.Items.Add(new Separator { Style = (Style)FindResource("RayaDelMenu") });

    /// <summary>El latido sólo existe en la gota: la nube tiene su propio vocabulario y no lo comparte.</summary>
    private void ViewModelOnStepAdvanced(object? sender, EventArgs e) => OrbHost.Children
        .OfType<LiquidOrb>()
        .FirstOrDefault()
        ?.Beat();

    /// <summary>
    /// El ánimo va a los dos cuerpos y a la píldora por la misma puerta.
    /// </summary>
    /// <remarks>
    /// Quien lo dispara no tiene que saber cuál de los dos cuerpos está puesto ni acordarse de que
    /// además hay píldora. Nadie lo apaga —dura lo que dice su tabla y se va solo—.
    /// </remarks>
    private void ViewModelOnMoodShown(object? sender, OrbMood mood)
    {
        foreach (var body in Bodies())
        {
            body.ShowMood(mood);
        }

        if (mood == OrbMood.Urgente)
        {
            MarkUrgent();
        }
    }

    /// <summary>Un recordatorio llegó a su hora. Con una pantalla completa adelante, es urgente.</summary>
    private void ViewModelOnActivationRequested(object? sender, ShellActivationRequest request)
    {
        if (request.Reason == ShellActivationReason.Reminder)
        {
            MarkUrgent();
        }
    }

    /// <summary>
    /// Dónde va la píldora respecto del borde de arriba del orbe. Sale del boceto.
    /// </summary>
    /// <remarks>
    /// En el fuente se posiciona con <c>left:54px; top:-36px</c> y <c>translateX(-50%)</c> sobre un
    /// orbe de 108: centrada, y 36 px por encima del borde superior.
    /// </remarks>
    private const double PillTop = -36;

    /// <summary>
    /// Le abre a la píldora el ancho que necesita, en una celda que mide lo que mide el orbe.
    /// </summary>
    /// <remarks>
    /// El contenedor de la píldora mide 108 —lo que mide el orbe, que es a lo que tiene que seguir—
    /// y las etiquetas largas miden bastante más. Cuando un hijo pide más ancho que su lugar, WPF le
    /// pone un clip de layout: la píldora sale cortada, y justo la etiqueta más larga es la que más
    /// importa que se lea. El margen lateral negativo ensancha el lugar sin mover el centro.
    /// <para>
    /// El número sale de preguntarle a la píldora cuánto mide, no de probar hasta que dejó de verse
    /// el corte. <see cref="StatePill"/> se mide con la combinación más larga posible —estado, ánimo
    /// y detalle, incluidas las del modo quieto—, así que una sola medición alcanza para siempre y
    /// un estado nuevo de nombre largo entra solo. Antes acá había un −110 a cada lado escrito a
    /// mano; el día que la etiqueta más larga pase de 328 px, ese número falla sin avisar.
    /// </para>
    /// </remarks>
    private void MeasurePillSlack()
    {
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var slack = Math.Max(0, (Pill.DesiredSize.Width - ShellLayout.OrbSize) / 2);
        Pill.Margin = new Thickness(-slack, PillTop, -slack, 0);
    }

    /// <summary>
    /// Todo lo que habla el idioma del orbe: el cuerpo que esté puesto y la píldora.
    /// </summary>
    /// <remarks>
    /// La píldora no es un cuerpo, pero implementa lo mismo justamente para poder estar en esta
    /// lista: el estado, la madrugada, el escritorio claro y el ánimo se reparten una sola vez y no
    /// hay forma de acordarse de tres y olvidarse del cuarto.
    /// </remarks>
    private IEnumerable<IOrbBody> Bodies()
    {
        foreach (var body in OrbHost.Children.OfType<IOrbBody>())
        {
            yield return body;
        }

        yield return Pill;
    }

    /// <summary>
    /// Reparte la hora y el tema del escritorio a todo lo que dibuja.
    /// </summary>
    /// <remarks>
    /// Las dos valen para los dos cuerpos y para la píldora, y por eso se ponen desde un solo lado:
    /// que el orbe esté de madrugada y la píldora de mediodía sería peor que no tener modo noche.
    /// </remarks>
    private void ApplyAmbience()
    {
        var night = OrbNight.For(TimeOnly.FromDateTime(DateTime.Now));
        var light = DesktopGlass.IsLightDesktop;

        foreach (var body in Bodies())
        {
            body.NightMode = night;
            body.IsLightDesktop = light;
        }
    }

    /// <summary>
    /// Cambia el cuerpo en vivo. Los dos leen el mismo estado, así que la elección no altera nada
    /// del comportamiento: es puramente cómo se ve.
    /// </summary>
    private void ApplyOrbShape(OrbShape shape)
    {
        OrbHost.Children.Clear();
        _orbMotion = null;

        if (shape == OrbShape.Nube)
        {
            var nube = new NubeOrb { Width = ShellLayout.OrbSize, Height = ShellLayout.OrbSize };
            nube.SetBinding(NubeOrb.StateProperty, new Binding(nameof(MainViewModel.State)) { Source = _viewModel });
            OrbHost.Children.Add(nube);
            _orbMotion = nube;

            // El cuerpo nuevo nace sin hora y sin escritorio: se los pasa el mismo reparto de siempre.
            ApplyAmbience();
            return;
        }

        var gota = new LiquidOrb();
        gota.SetBinding(LiquidOrb.StateProperty, new Binding(nameof(MainViewModel.State)) { Source = _viewModel });
        // No hay enlace de «micrófono armado»: eso ahora es un estado —guardia— y entra por State,
        // igual que los otros catorce y en los dos cuerpos.
        gota.SetBinding(
            LiquidOrb.AudioLevelProperty,
            new Binding(nameof(MainViewModel.AudioLevel)) { Source = _viewModel });
        gota.SetBinding(
            LiquidOrb.HasSpendAuthorizationProperty,
            new Binding(nameof(MainViewModel.HasSpendAuthorization)) { Source = _viewModel });
        OrbHost.Children.Add(gota);
        _orbMotion = gota;
        ApplyAmbience();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreOrbPlacement();
        Glass.Variant = DesktopGlass.Resolve(this);
        DesktopGlass.TryApplySystemBackdrop(this);
        StartSweep();
        StartFollowingActiveMonitor();
        StartWatchingAmbience();
        StartWatchingFullScreen();
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
        // Primero se restaura y DESPUÉS se mide. El área útil que importa es la del monitor donde
        // quedó el orbe, y leerla antes es leer la del monitor donde la ventana estaba sin colocar.
        // Con el orbe guardado en una pantalla secundaria a la izquierda —coordenadas negativas—,
        // eso lo recortaba contra los límites del primario y reaparecía pegado al borde izquierdo
        // del monitor 1 en cada arranque.
        _placementStore.Restore(this);
        var orb = new Point(Left, Top);

        var screen = ScreenAt(orb) ?? System.Windows.Forms.Screen.PrimaryScreen;
        var workArea = screen is null ? SystemParameters.WorkArea : ToLogical(screen.WorkingArea);
        var bounds = ShellLayout.OrbBounds(workArea);

        // Sin archivo guardado, el almacén propone la esquina de una ventana del tamaño de ésta. Como
        // acá la ventana es mucho más grande que el orbe, esa esquina deja al orbe lejos del borde:
        // en ese caso —y sólo en ese— se prefiere el rincón de abajo a la derecha, que es donde un
        // asistente de escritorio espera aparecer la primera vez.
        //
        // Se compara contra el área del monitor primario porque es la que usó el almacén para
        // proponerla; medirla en otro monitor haría que la comparación nunca dé.
        var primary = SystemParameters.WorkArea;
        var fallbackLeft = primary.Right - ShellLayout.WindowWidth - 24;
        var fallbackTop = primary.Bottom - ShellLayout.WindowHeight - 24;
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

        // Mudándose, el orbe puede estar en cualquiera de los dos monitores. Recortarlo al de origen
        // cuadro a cuadro es lo que impedía que el viaje arrancara.
        var bounds = _travel?.Bounds ?? ShellLayout.OrbBounds(workArea);

        // Mientras se arrastra no se adopta ni se recorta. Adoptar sería leer la posición que este
        // mismo bucle acaba de escribir, y recortar contra el área útil le sacaría al resorte
        // justamente el sobrepaso que le da peso: el orbe tiene que poder pasarse del borde y volver.
        if (!_motion.IsDragging)
        {
            AdoptExternalMove();
            _motion.ClampInto(bounds);
        }

        _motion.Step(dt, bounds);

        if (!_motion.IsDragging)
        {
            SettleTravel();
        }

        WriteWindowPosition();

        // La costura. El cuerpo se entera de que se está moviendo acá y en ningún otro lado: de esto
        // salen el achatamiento, la inclinación hacia donde va, el polvo que queda atrás y el golpe.
        _orbMotion?.ReportMotion(_motion.Sample);

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

        if (!_pillOnly)
        {
            return;
        }

        // Un aviso urgente sobre una pantalla completa muestra la píldora y nada más. El cuerpo
        // queda apagado aunque la presencia diga otra cosa, y no se puede tocar: aparecer arriba de
        // un juego con una superficie que agarra clics es peor que no avisar.
        OrbDragSurface.IsHitTestVisible = false;
        OrbDragSurface.Visibility = Visibility.Hidden;
        OrbOverlay.Visibility = Visibility.Visible;
        OrbOverlay.Opacity = 1;
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
        // Con la velocidad suavizada y no con la del integrador: es la misma que ve el cuerpo, así
        // que el vidrio se retrae exactamente cuando el orbe empieza a estirarse, y no un cuadro
        // antes ni después. Durante el arrastre la del integrador es la del resorte, que puede ser
        // enorme sin que el orbe se haya movido todavía.
        if (_fastMove)
        {
            _fastMove = _motion.SmoothSpeed > 170;
        }
        else
        {
            _fastMove = _motion.SmoothSpeed > 340;
        }

        // El desplegable NO se abre nunca con una pantalla completa adelante. La condición vive acá,
        // del lado de la ventana, y no en el modelo de vista: el modelo abre el panel de permiso por
        // su cuenta cuando llega una confirmación, y si esta regla estuviera allá habría que
        // acordarse de repetirla en cada camino que abre un panel.
        var shouldShow = _viewModel.IsPanelOpen &&
            !_fullScreen &&
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

    private void StartWatchingAmbience()
    {
        _ambienceTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = AmbienceInterval
        };
        _ambienceTimer.Tick += (_, _) => ApplyAmbience();
        _ambienceTimer.Start();
    }

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
            // También mientras hay una mudanza en vuelo o una pantalla completa adelante: en el
            // primer caso ya se está yendo a algún lado, en el segundo no hay nada que mover.
            if (_viewModel.IsPanelOpen || _motion.IsDragging || !IsVisible ||
                _travel is not null || _fullScreen || _hidingToTray)
            {
                _stableTicks = 0;
                return;
            }

            var (key, _) = Services.MonitorSlots.MonitorUnderCursor();
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
            MoveToMonitor(key, System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position));
        };

        _followTimer.Start();
    }

    /// <summary>
    /// La mudanza: viaja con estela si el otro monitor es vecino, y si no se va por el borde y vuelve
    /// por el otro.
    /// </summary>
    /// <remarks>
    /// Nunca cruza contenido. Entre dos pantallas que no se tocan hay escritorio, ventanas y a veces
    /// un hueco del escritorio virtual; atravesarlo en línea recta se lee como un objeto pasando por
    /// encima de todo lo que el usuario está mirando.
    /// <para>
    /// El destino se calcula en unidades independientes del DPI, que es el espacio donde viven
    /// <c>Left</c> y <c>Top</c>: <c>MonitorSlots</c> informa píxeles físicos, y pasárselos tal cual a
    /// la geometría del orbe sólo funciona si las dos pantallas están al 100 %. Acá se convierten. Lo
    /// que <b>no</b> está resuelto es la conversión entre dos monitores de escalas distintas: la
    /// escala sale de la ventana, que está parada en uno de los dos.
    /// </para>
    /// </remarks>
    private void MoveToMonitor(string key, System.Windows.Forms.Screen target)
    {
        var toWork = ToLogical(target.WorkingArea);
        var toBounds = ShellLayout.OrbBounds(toWork);
        var slot = _monitors.SlotFor(key, toWork, new Size(ShellLayout.OrbSize, ShellLayout.OrbSize));
        var destination = new Point(
            Math.Clamp(slot.X, toBounds.Left, toBounds.Right),
            Math.Clamp(slot.Y, toBounds.Top, toBounds.Bottom));

        _currentMonitor = key;
        var adjacent = MonitorMove.AreAdjacent(CurrentMonitorBounds, ToLogical(target.Bounds));

        // Queda escrito por qué viajó como viajó: mirando la pantalla, «se fue volando» y «salió por
        // el borde» se distinguen, pero por qué eligió uno u otro no se ve.
        Diagnostics.RuntimeTrace.Write(
            "monitor.mudanza",
            $"{(adjacent ? "vecino · viaja con estela" : "lejos · sale por el borde")} → " +
            $"({destination.X:0};{destination.Y:0})");

        if (adjacent)
        {
            // Los límites de los dos monitores juntos mientras dura: el recorte por cuadro lo
            // devolvería al de origen y el viaje no arrancaría nunca.
            _travel = new MonitorTravel(
                Rect.Union(ShellLayout.OrbBounds(CurrentWorkArea), toBounds),
                destination,
                DateTime.UtcNow + MonitorMove.Longest);
            _motion.Launch(destination, MonitorMove.Kick, MonitorMove.Lift);
            return;
        }

        LeaveByTheEdge(destination, toWork);
    }

    /// <summary>
    /// Se va por el borde más cercano y vuelve por el otro lado, medio segundo después.
    /// </summary>
    /// <remarks>
    /// Es la misma retirada de «guardarse»: <see cref="OrbPresence.Esconder"/> elige el borde por el
    /// que se va midiendo cuál está más cerca. En el fuente de la referencia hay dos líneas que fijan
    /// esa dirección a mano justo antes de llamar a <c>esconder()</c>, y no hacen nada: <c>esconder()</c>
    /// la recalcula. Se copió el comportamiento, no las dos líneas muertas.
    /// </remarks>
    private void LeaveByTheEdge(Point destination, Rect targetWorkArea)
    {
        var centre = new Point(
            _motion.Position.X + ShellLayout.OrbSize / 2,
            _motion.Position.Y + ShellLayout.OrbSize / 2);
        _presence.Esconder(centre, CurrentWorkArea);

        _crossTimer?.Stop();
        _crossTimer = new System.Windows.Threading.DispatcherTimer { Interval = MonitorMove.EdgeGap };
        _crossTimer.Tick += (_, _) =>
        {
            _crossTimer?.Stop();
            _crossTimer = null;

            _motion.Teleport(destination);
            ApplySide(ShellLayout.ShouldOpenRight(destination, targetWorkArea), force: false);
            WriteWindowPosition();
            _presence.Aparecer();
            SaveOrbPlacement();
        };

        _crossTimer.Start();
    }

    /// <summary>
    /// Cierra la mudanza en vuelo cuando el orbe llegó, o cuando se acabó el tiempo.
    /// </summary>
    /// <remarks>
    /// Mientras dura, el orbe tiene puestos los límites de los dos monitores: si esto no cerrara
    /// nunca, quedaría libre de pararse en el medio de la costura entre las dos pantallas.
    /// </remarks>
    private void SettleTravel()
    {
        if (_travel is not { } travel)
        {
            return;
        }

        var expired = DateTime.UtcNow > travel.DeadlineUtc;
        if (_motion.IsFlying && !expired)
        {
            return;
        }

        // El vuelo terminó donde lo dejó la inercia; el resorte de reposo lo lleva al lugar exacto.
        _motion.Nudge(travel.Destination);

        var arrived = (_motion.Position - travel.Destination).Length < 3 && _motion.Speed < 40;
        if (!arrived && !expired)
        {
            return;
        }

        _travel = null;
        SaveOrbPlacement();
        Diagnostics.RuntimeTrace.Write(
            "monitor.mudanza",
            $"{(arrived ? "llegó" : "se quedó")} en ({_motion.Position.X:0};{_motion.Position.Y:0})");
    }

    /// <summary>
    /// El vigía de pantalla completa.
    /// </summary>
    /// <remarks>
    /// Tres reglas, y las tres son fáciles de romper sin darse cuenta:
    /// <list type="number">
    /// <item><b>Nunca robar el foco.</b> Si hay algo en pantalla completa el usuario está mirando
    /// eso; una ventana que se pone adelante y se lleva el teclado es lo peor que puede hacer un
    /// asistente de escritorio. Acá no se llama a <c>Activate</c>, ni a <c>Show</c>, ni a
    /// <c>Focus</c>: sólo se apaga el dibujo.</item>
    /// <item><b>El desplegable no se abre nunca.</b> Se apaga en
    /// <see cref="UpdatePanelVisibility"/>, del lado de la ventana y no del modelo de vista, para
    /// que valga aunque el modelo decida abrirlo por su cuenta —una confirmación pendiente lo abre
    /// sin que nadie lo pida—.</item>
    /// <item><b>Sin nada urgente no queda nada.</b> El filete es lo único, y sólo si hay
    /// algo.</item>
    /// </list>
    /// <para>
    /// La ventana no se oculta con <c>Hide</c>: eso es «guardarse en la bandeja», lo maneja
    /// <c>App</c> y cambia lo que dice el menú del área de notificación. Acá se apaga el orbe y se
    /// suelta el <c>Topmost</c>, con lo que la ventana queda transparente entera y sin píxeles con
    /// alfa: los clics la atraviesan y no hay nada que ver.
    /// </para>
    /// </remarks>
    private void StartWatchingFullScreen()
    {
        _fullScreenTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FullScreenInterval
        };

        _fullScreenTimer.Tick += (_, _) => CheckFullScreen();
        _fullScreenTimer.Start();
        CheckFullScreen();
    }

    private void CheckFullScreen()
    {
        // Guardado en la bandeja no es asunto de esto: quien lo guardó decide cuándo vuelve. Se mira
        // también la retirada en curso, que dura media pantalla y todavía informa IsVisible.
        if (!IsVisible || _hidingToTray)
        {
            return;
        }

        var full = FullScreenWatch.IsForegroundFullScreen(
            new WindowInteropHelper(this).Handle,
            OrbScreen());
        if (full == _fullScreen)
        {
            UpdateSliver();
            return;
        }

        _fullScreen = full;

        // Queda en la bitácora porque «Viernes desapareció» y «Viernes no se esconde» son el mismo
        // reporte visto desde los dos lados, y sin esta línea hay que adivinar cuál de los dos es.
        Diagnostics.RuntimeTrace.Write("pantalla.completa", full ? "entra" : "sale");

        if (full)
        {
            EnterFullScreen();
        }
        else
        {
            LeaveFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        _viewModel.IsUnderFullScreen = true;
        _viewModel.ClosePanel();

        var centre = new Point(
            _motion.Position.X + ShellLayout.OrbSize / 2,
            _motion.Position.Y + ShellLayout.OrbSize / 2);
        _presence.Esconder(centre, CurrentWorkArea);
        _hiddenByFullScreen = true;

        // Soltar el Topmost no es cosmético: una ventana siempre-arriba encima de un juego en
        // pantalla completa exclusiva lo puede sacar de ese modo, y eso se ve como un parpadeo del
        // juego cada vez que Viernes se dibuja.
        Topmost = false;
        UpdateSliver();
    }

    private void LeaveFullScreen()
    {
        // Ya no está en pantalla completa: si había algo urgente, lo va a ver entero. El filete
        // cumplió y se apaga.
        _viewModel.IsUnderFullScreen = false;
        _urgentPending = false;
        _pillOnly = false;
        _pillTimer?.Stop();
        UpdateSliver();

        Topmost = true;
        if (!_hiddenByFullScreen)
        {
            return;
        }

        _hiddenByFullScreen = false;
        ShowWithoutStealingFocus();
    }

    /// <summary>
    /// Algo urgente pasó. Con una pantalla completa adelante, se ve la píldora y nada más.
    /// </summary>
    /// <remarks>
    /// Nunca el desplegable y nunca el foco: sólo el nombre del estado flotando donde estaría el
    /// orbe. A los cuatro segundos se retrae al filete, que es lo que dice el LEEME.
    /// <para>
    /// Fuera de pantalla completa no hace nada, y está bien que así sea: ahí el orbe entero ya está
    /// a la vista y no hay nada que anunciar de otra manera.
    /// </para>
    /// </remarks>
    private void MarkUrgent()
    {
        if (!_fullScreen)
        {
            return;
        }

        _urgentPending = true;
        _pillOnly = true;

        // Vuelve a estar arriba, y sólo ahora: el orbe soltó el Topmost al esconderse, y sin
        // recuperarlo la píldora se dibuja debajo del video y no la ve nadie. Es el único momento en
        // que Viernes se pone adelante de una pantalla completa, y aun así no toma el foco.
        Topmost = true;
        Diagnostics.RuntimeTrace.Write("pantalla.completa", "aviso urgente · sólo la píldora");

        // Los cuatro segundos van en su propio reloj y no en el bucle de cuadro. Escondido no hay
        // nada que dibujar, WPF deja de componer y CompositionTarget.Rendering deja de dispararse:
        // contando ahí, la píldora se quedaba puesta para siempre y el filete no llegaba nunca.
        _pillTimer ??= CreatePillTimer();
        _pillTimer.Stop();
        _pillTimer.Start();
        UpdateSliver();
    }

    private System.Windows.Threading.DispatcherTimer CreatePillTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = UrgentPillWindow };
        timer.Tick += (_, _) => RetractUrgentPill();
        return timer;
    }

    /// <summary>
    /// Retrae la píldora al filete cuando se cumplieron los cuatro segundos.
    /// </summary>
    /// <remarks>
    /// Vuelve a esconder la presencia, y eso no es redundante: el pedido de presencia que dispara el
    /// aviso lo atiende también <c>App</c>, que llama a <c>ShowWithoutStealingFocus</c> y con eso el
    /// orbe empieza a volver. Mientras dura la píldora no se nota —el cuerpo está tapado a mano—,
    /// pero al soltar esa tapa aparecería entero encima del juego. Sin esta línea, el aviso urgente
    /// termina haciendo justo lo que la regla prohíbe.
    /// </remarks>
    private void RetractUrgentPill()
    {
        _pillTimer?.Stop();
        if (!_pillOnly)
        {
            return;
        }

        _pillOnly = false;

        if (_fullScreen)
        {
            var centre = new Point(
                _motion.Position.X + ShellLayout.OrbSize / 2,
                _motion.Position.Y + ShellLayout.OrbSize / 2);
            _presence.Esconder(centre, CurrentWorkArea);
            _hiddenByFullScreen = true;

            // Y suelta el frente otra vez: lo que queda es el filete, que es una ventana propia.
            Topmost = false;
        }

        UpdateSliver();
    }

    /// <summary>
    /// Muestra u oculta el filete. Se llama en cada transición, no por cuadro.
    /// </summary>
    /// <remarks>
    /// Mostrar y arrancar la respiración están del lado de la transición a propósito: llamarlos en
    /// cada tic del vigía reiniciaría la animación una vez por segundo, y una respiración que se
    /// reinicia no respira, parpadea.
    /// </remarks>
    private void UpdateSliver()
    {
        var shouldShow = _fullScreen && _urgentPending && !_pillOnly;
        if (!shouldShow)
        {
            if (_sliverShown)
            {
                _sliverShown = false;
                _sliver?.Hide();
            }

            return;
        }

        _sliver ??= new UrgentSliver();
        _sliver.SnapNear(
            new Rect(_motion.Position.X, _motion.Position.Y, ShellLayout.OrbSize, ShellLayout.OrbSize),
            CurrentMonitorBounds,
            _viewModel.State);

        if (_sliverShown)
        {
            return;
        }

        _sliverShown = true;
        _sliver.Show();
        _sliver.StartBreathing();
        Diagnostics.RuntimeTrace.Write(
            "pantalla.completa",
            $"filete de 3 px en ({_sliver.Left:0};{_sliver.Top:0}) sobre {CurrentMonitorBounds}");
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
        _ambienceTimer?.Stop();
        _fullScreenTimer?.Stop();
        _pillTimer?.Stop();
        _crossTimer?.Stop();

        // El filete es una ventana aparte: si no se cierra, el proceso no termina nunca.
        _sliver?.Close();
        _sliver = null;
        _sliverShown = false;

        // Los cuatro, no uno. Se soltaban sólo los cambios de propiedad y quedaban colgados el
        // latido, el ánimo y el pedido de presencia, que capturan el árbol visual de esta ventana.
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _viewModel.StepAdvanced -= ViewModelOnStepAdvanced;
        _viewModel.MoodShown -= ViewModelOnMoodShown;
        _viewModel.ActivationRequested -= ViewModelOnActivationRequested;
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
        else if (e.PropertyName == nameof(MainViewModel.IsConfirmationVisible) &&
            _viewModel.IsConfirmationVisible)
        {
            // Algo está esperando una decisión del usuario: es lo más urgente que hay.
            MarkUrgent();
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
    /// código muerto. Por eso el arrastre arranca recién cuando el mouse se movió de verdad.
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
    /// Acá había un <c>DragMove()</c>. Se fue por dos razones: clavaba la ventana al cursor —así que
    /// el orbe no podía quedarse atrás de la mano, que es todo su peso— y no volvía hasta que se
    /// soltaba el botón, así que el resorte de arrastre de <see cref="OrbMotion"/> nunca corrió una
    /// sola vez. Ahora el cursor es un <em>objetivo</em> y la ventana la mueve el resorte.
    /// </para>
    /// </remarks>
    private void Orb_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // Se soltó sin que llegara el MouseUp —pasó fuera de la ventana, o lo comió otro—.
                EndDrag();
                return;
            }

            _motion.DragTo(PointerPosition() - _grab);
            return;
        }

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
        BeginDrag();
    }

    /// <summary>
    /// Toma el mouse y arranca el arrastre.
    /// </summary>
    /// <remarks>
    /// La captura ajena se suelta ANTES de tomar la propia. Un <c>Button</c> de WPF se queda con el
    /// mouse al presionarlo, y ese fue un bug pago: con el botón capturando, el arrastre entero
    /// dejaba de existir y el orbe quedaba clavado —«no puedo arrastrarla manteniendo apretada»—. Lo
    /// introdujo el arreglo que hizo que el clic llegara al botón. Sacarle la captura tiene además
    /// el efecto que se quiere: el botón cancela su presión y no dispara <c>Click</c> al soltar, así
    /// que arrastrar nunca abre el panel de yapa.
    /// </remarks>
    private void BeginDrag()
    {
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        // La ventana captura, no el contenedor del orbe: durante el arrastre el cursor se va bien
        // afuera de los 108 px del orbe y hay que seguir recibiendo sus movimientos igual.
        if (!CaptureMouse())
        {
            return;
        }

        _dragging = true;
        _grab = PointerPosition() - _motion.Position;
        _motion.BeginDrag();
    }

    /// <summary>Termina el arrastre y lo suelta con la velocidad que traía el resorte.</summary>
    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _motion.Drop();
        SaveOrbPlacement();

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// Dónde está el puntero, en el mismo espacio en el que vive la esquina del orbe.
    /// </summary>
    /// <remarks>
    /// Se pregunta por la posición absoluta del cursor y no por <c>e.GetPosition(this)</c>. Durante
    /// el arrastre la ventana se mueve debajo del cursor en cada cuadro —la mueve el resorte—, así
    /// que medir el puntero <em>respecto de la ventana</em> sería mezclar el movimiento que estamos
    /// causando con el que estamos midiendo: el objetivo empujaría a la ventana y la ventana al
    /// objetivo. Absoluto no tiene ese lazo.
    /// <para>
    /// El cursor viene en píxeles físicos y se divide por la escala del monitor, que es exactamente
    /// la conversión inversa a la de <see cref="ScreenAt"/>. Las dos tienen que ser la misma o el
    /// orbe se iría corriendo del dedo en un escritorio escalado.
    /// </para>
    /// </remarks>
    private Point PointerPosition()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        return new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
    }

    /// <summary>
    /// Soltar sin haber arrastrado es un toque. Sobre el botón lo resuelve su propio Click.
    /// </summary>
    private void Orb_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            EndDrag();
            return;
        }

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

    /// <summary>
    /// Si alguien más se lleva el mouse —otra ventana, un Alt-Tab—, el arrastre termina ahí.
    /// </summary>
    /// <remarks>
    /// Sin esto el orbe quedaría pegado al último objetivo y <c>IsDragging</c> encendido para
    /// siempre: la física no volvería a volar ni a imantar, y el vidrio no se abriría nunca más.
    /// </remarks>
    private void Orb_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

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
                var screen = OrbScreen();
                return screen is null ? SystemParameters.WorkArea : ToLogical(screen.WorkingArea);
            }
            catch (Exception)
            {
                return SystemParameters.WorkArea;
            }
        }
    }

    /// <summary>
    /// En qué monitor está <b>el orbe</b>, que no siempre es en el que está la ventana.
    /// </summary>
    /// <remarks>
    /// La ventana mide 528 de ancho —el orbe más el lugar del desplegable más el aire de las
    /// sombras— y el orbe mide 108. Con el orbe a veinte píxeles de la costura entre dos pantallas,
    /// la ventana está mayormente del otro lado, y preguntarle a ella devuelve el monitor
    /// equivocado.
    /// <para>
    /// Eso no era teórico: al mudarse a un destino cerca de la costura, el orbe llegaba, la ventana
    /// quedaba mayormente en el monitor de origen, el lado del desplegable se calculaba con el área
    /// útil de <em>ése</em> y se espejaba, lo que corría la ventana 368 px más hacia allá, lo que
    /// confirmaba el monitor equivocado, y el recorte por cuadro terminaba devolviendo el orbe al
    /// borde de donde había salido. Se veía como que la mudanza «rebotaba».
    /// </para>
    /// <para>
    /// Antes de la primera escritura de posición no hay orbe todavía: ahí sí manda la ventana.
    /// </para>
    /// </remarks>
    private System.Windows.Forms.Screen? OrbScreen()
    {
        if (double.IsNaN(_writtenLeft))
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle == nint.Zero
                ? System.Windows.Forms.Screen.PrimaryScreen
                : System.Windows.Forms.Screen.FromHandle(handle);
        }

        return ScreenAt(new Point(
            _motion.Position.X + (ShellLayout.OrbSize / 2),
            _motion.Position.Y + (ShellLayout.OrbSize / 2)));
    }

    /// <summary>En qué monitor cae un punto. <c>Screen</c> habla en píxeles físicos; el orbe, no.</summary>
    private System.Windows.Forms.Screen? ScreenAt(Point logical)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(logical.X * dpi.DpiScaleX),
            (int)(logical.Y * dpi.DpiScaleY)));
    }

    /// <summary>
    /// Los límites del monitor donde está el orbe, barra de tareas incluida.
    /// </summary>
    /// <remarks>
    /// El filete de pantalla completa se apoya contra esto y no contra el área útil: en pantalla
    /// completa la barra de tareas no está, y usar el área útil dejaría el filete flotando a unos
    /// píxeles del borde real.
    /// </remarks>
    private Rect CurrentMonitorBounds
    {
        get
        {
            try
            {
                var screen = OrbScreen();
                return screen is null ? SystemParameters.WorkArea : ToLogical(screen.Bounds);
            }
            catch (Exception)
            {
                return SystemParameters.WorkArea;
            }
        }
    }

    /// <summary>
    /// Pasa un rectángulo de WinForms —píxeles físicos— al espacio en el que viven <c>Left</c> y
    /// <c>Top</c>.
    /// </summary>
    /// <remarks>
    /// WinForms informa píxeles físicos; WPF trabaja en unidades independientes del DPI. La escala
    /// sale de <em>esta</em> ventana, así que el resultado es correcto para el monitor donde está
    /// parada. <b>Con dos monitores de escalas distintas, un rectángulo del otro monitor convertido
    /// con esta escala queda mal</b>; no hay forma de arreglarlo sin saber a qué espacio pertenece
    /// cada número, y eso pide medirlo con dos pantallas de escalas distintas, cosa que acá no se
    /// pudo hacer.
    /// </remarks>
    private Rect ToLogical(System.Drawing.Rectangle physical)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            physical.Left / dpi.DpiScaleX,
            physical.Top / dpi.DpiScaleY,
            physical.Width / dpi.DpiScaleX,
            physical.Height / dpi.DpiScaleY);
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

    /// <summary>
    /// Enter contesta la pregunta pendiente, igual que en el campo de escribir.
    /// </summary>
    /// <remarks>
    /// Es el mismo gesto en los dos campos de texto que tiene la interfaz. Que uno mande con Enter y
    /// el otro obligue a apuntarle al botón se aprende a los golpes.
    /// </remarks>
    private void AnswerTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            Keyboard.Modifiers == ModifierKeys.None &&
            _viewModel.Board.AnswerCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.Board.AnswerCommand.Execute(null);
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
