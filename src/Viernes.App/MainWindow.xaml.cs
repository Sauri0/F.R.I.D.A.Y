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
using Viernes.App.ViewModels;
using Binding = System.Windows.Data.Binding;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Viernes.App;

public partial class MainWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = -1;

    private readonly MainViewModel _viewModel;
    private readonly WindowPlacementStore _placementStore;
    private bool _pushToTalkActive;
    private CancellationTokenSource? _pushToTalkCancellation;
    private double _appliedWidgetWidth = 108;
    private double _appliedWidgetHeight = 108;
    private bool _expandsLeft;

    internal MainWindow(MainViewModel viewModel, WindowPlacementStore placementStore)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _placementStore = placementStore;
        DataContext = viewModel;

        ApplyOrbShape(viewModel.OrbShape);
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
            var nube = new NubeOrb { Width = 96, Height = 96 };
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
        _placementStore.Restore(this);

        try
        {
            await _viewModel.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Viernes initialization failed: {exception.GetType().Name}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveOrbPlacement();
        if (!App.Current.IsExitRequested)
        {
            e.Cancel = true;
            _ = _viewModel.SetShellVisibilityAsync(false, CancellationToken.None);
            Hide();
            App.Current.NotifyWindowVisibilityChanged(false);
        }
    }

    private void Window_Closed(object? sender, EventArgs e) =>
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.WidgetWidth))
        {
            ApplyWidgetWidth(_viewModel.WidgetWidth);
        }
        else if (e.PropertyName == nameof(MainViewModel.WidgetHeight))
        {
            ApplyWidgetHeight(_viewModel.WidgetHeight);
        }
        else if (e.PropertyName == nameof(MainViewModel.OrbShape))
        {
            ApplyOrbShape(_viewModel.OrbShape);
        }
    }

    private void ApplyWidgetWidth(double targetWidth)
    {
        if (Math.Abs(targetWidth - _appliedWidgetWidth) < 0.5)
        {
            return;
        }

        var workArea = CurrentWorkArea;
        var previousWidth = _appliedWidgetWidth;
        var isBecomingExpanded = previousWidth <= 120 && targetWidth > 120;
        var isBecomingMinimal = previousWidth > 120 && targetWidth <= 120;

        if (isBecomingExpanded)
        {
            _expandsLeft = Left + targetWidth > workArea.Right - 8;
            ConfigureExpansionDirection(_expandsLeft);
            if (_expandsLeft)
            {
                Left -= targetWidth - previousWidth;
            }
        }
        else if (_expandsLeft)
        {
            Left += previousWidth - targetWidth;
        }

        _appliedWidgetWidth = targetWidth;
        Width = targetWidth;

        if (isBecomingMinimal)
        {
            ConfigureExpansionDirection(expandsLeft: false);
            _expandsLeft = false;
        }

        Left = Math.Clamp(Left, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - targetWidth - 4));
    }

    private void ApplyWidgetHeight(double targetHeight)
    {
        if (Math.Abs(targetHeight - _appliedWidgetHeight) < 0.5)
        {
            return;
        }

        var workArea = CurrentWorkArea;
        Top -= (targetHeight - _appliedWidgetHeight) / 2;
        _appliedWidgetHeight = targetHeight;
        Height = targetHeight;
        Top = Math.Clamp(Top, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - targetHeight - 4));
    }

    private void ConfigureExpansionDirection(bool expandsLeft)
    {
        if (expandsLeft)
        {
            LeadingColumn.Width = new GridLength(1, GridUnitType.Star);
            TrailingColumn.Width = new GridLength(96);
            Grid.SetColumn(AssistantBubble, 0);
            Grid.SetColumn(OrbDragSurface, 1);
            OrbDragSurface.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            AssistantBubble.Margin = new Thickness(2, 2, -1, 2);
            AssistantBubble.Padding = new Thickness(8, 10, 14, 8);
            AssistantBubble.CornerRadius = new CornerRadius(24, 5, 5, 24);
            return;
        }

        LeadingColumn.Width = new GridLength(96);
        TrailingColumn.Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(OrbDragSurface, 0);
        Grid.SetColumn(AssistantBubble, 1);
        OrbDragSurface.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        AssistantBubble.Margin = new Thickness(-1, 2, 2, 2);
        AssistantBubble.Padding = new Thickness(14, 10, 8, 8);
        AssistantBubble.CornerRadius = new CornerRadius(5, 24, 24, 5);
    }

    /// <summary>
    /// Presionar en cualquier parte del orbe arrastra; si se suelta sin haberse movido, cuenta como
    /// toque y abre el panel. Antes había que apuntar al aro exterior, que a 96 px es una franja
    /// de pocos píxeles y volvía tedioso algo que se hace todo el tiempo.
    /// </summary>
    private void Orb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        var origin = PointToScreen(e.GetPosition(this));

        try
        {
            // DragMove no vuelve hasta que se suelta el botón.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // El botón se soltó antes de que arrancara el arrastre; sigue contando como toque.
        }

        var travelled = (PointToScreen(Mouse.GetPosition(this)) - origin).Length;
        if (travelled <= 4)
        {
            _viewModel.OpenTextInput();
            PromptTextBox.Focus();
            Keyboard.Focus(PromptTextBox);
            return;
        }

        SaveOrbPlacement();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        CancelActiveVoice();
        _ = _viewModel.SetShellVisibilityAsync(false, CancellationToken.None);
        SaveOrbPlacement();
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
    /// La gota se desprende del borde inferior derecho del monitor donde ya vive —no del monitor
    /// principal— y aterriza en su lugar. Llamarlo no lo muda de pantalla: si lo dejaste en la
    /// segunda, ahí se queda.
    /// </summary>
    internal void PlayArrivalFromTray()
    {
        var workArea = CurrentWorkArea;
        var targetLeft = Left;
        var targetTop = Top;

        var startLeft = Math.Clamp(workArea.Right - 52, workArea.Left, workArea.Right - 8);
        var startTop = Math.Clamp(workArea.Bottom - 34, workArea.Top, workArea.Bottom - 8);
        if (Math.Abs(startLeft - targetLeft) < 2 && Math.Abs(startTop - targetTop) < 2)
        {
            return;
        }

        Left = startLeft;
        Top = startTop;
        Opacity = 0;

        var travel = TimeSpan.FromMilliseconds(420);
        var glide = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slideLeft = new DoubleAnimation(startLeft, targetLeft, travel) { EasingFunction = glide };
        slideLeft.Completed += (_, _) =>
        {
            // Soltar la animación devuelve el control de la posición a DragMove.
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = targetLeft;
            Top = targetTop;
        };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        BeginAnimation(LeftProperty, slideLeft);
        BeginAnimation(TopProperty, new DoubleAnimation(startTop, targetTop, travel) { EasingFunction = glide });

        PlaySquashAndStretch(travel);
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
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    internal void SaveOrbPlacement()
    {
        var anchorLeft = Left + (_expandsLeft ? Math.Max(0, _appliedWidgetWidth - 108) : 0);
        var anchorTop = Top + Math.Max(0, _appliedWidgetHeight - 108) / 2;
        _placementStore.Save(this, anchorLeft, anchorTop);
    }

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
