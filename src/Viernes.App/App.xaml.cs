using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using Viernes.App.Services;
using Viernes.App.ViewModels;
using Viernes.Platform.Windows.AutoStart;

namespace Viernes.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Local\\Viernes.Desktop.9A027079-478A-4E6E-94E2-EB231CA26113";

    private readonly IAutoStartService _autoStartService = new AutoStartService();
    private Mutex? _singleInstanceMutex;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private TrayIconService? _trayIcon;

    public new static App Current => (App)System.Windows.Application.Current;

    internal bool IsExitRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo de diagnóstico visual: dibuja la gota a PNG y sale. No toca voz, preferencias ni red.
        var renderIndex = Array.IndexOf(e.Args, "--render-orb");
        if (renderIndex >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var outputDirectory = renderIndex + 1 < e.Args.Length
                ? e.Args[renderIndex + 1]
                : Path.Combine(Path.GetTempPath(), "viernes-orb");
            _ = RenderOrbAndExitAsync(outputDirectory);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var runtime = new AssistantRuntime();
        _viewModel = new MainViewModel(runtime);
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _viewModel.ActivationRequested += ViewModelOnActivationRequested;
        _window = new MainWindow(_viewModel, new WindowPlacementStore());

        _trayIcon = new TrayIconService(
            ToggleWindowVisibility,
            ToggleMute,
            ToggleWakeWord,
            ToggleListenWhileHidden,
            ToggleAutoStart,
            ChooseOrbShape,
            RequestExit);

        var autoStartStatus = _autoStartService.GetStatus();
        _trayIcon.SetAutoStart(autoStartStatus.IsConfiguredForCurrentExecutable);
        _trayIcon.SetListenWhileHidden(_viewModel.IsListeningWhileHidden);
        _trayIcon.SetOrbShape(_viewModel.OrbShape.ToString());
        _window.Show();
        _window.Activate();
    }

    private async Task RenderOrbAndExitAsync(string outputDirectory)
    {
        try
        {
            Console.WriteLine(await Diagnostics.OrbSnapshot.RunAsync(outputDirectory, Controls.OrbShape.Gota));
            Console.WriteLine(await Diagnostics.OrbSnapshot.RunAsync(outputDirectory, Controls.OrbShape.Nube));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"No se pudo renderizar la gota: {exception.Message}");
        }
        finally
        {
            Shutdown();
        }
    }

    internal void NotifyWindowVisibilityChanged(bool visible) => _trayIcon?.SetWindowVisible(visible);

    private void ToggleWindowVisibility()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.CancelActiveVoice();
            _window.SaveOrbPlacement();
            _ = _viewModel?.SetShellVisibilityAsync(false, CancellationToken.None);
            _window.Hide();
            _trayIcon?.SetWindowVisible(false);
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _ = _viewModel?.SetShellVisibilityAsync(true, CancellationToken.None);
        _trayIcon?.SetWindowVisible(true);
    }

    private void ToggleMute()
    {
        if (_viewModel?.ToggleMuteCommand.CanExecute(null) == true)
        {
            _viewModel.ToggleMuteCommand.Execute(null);
        }
    }

    private void ToggleWakeWord()
    {
        if (_viewModel?.ToggleWakeWordCommand.CanExecute(null) == true)
        {
            _viewModel.ToggleWakeWordCommand.Execute(null);
        }
    }

    private void ChooseOrbShape(string shape)
    {
        var parsed = string.Equals(shape, "Nube", StringComparison.OrdinalIgnoreCase)
            ? Controls.OrbShape.Nube
            : Controls.OrbShape.Gota;
        _ = _viewModel?.SetOrbShapeAsync(parsed, CancellationToken.None);
    }

    private void ToggleListenWhileHidden()
    {
        if (_viewModel?.ToggleListenWhileHiddenCommand.CanExecute(null) == true)
        {
            _viewModel.ToggleListenWhileHiddenCommand.Execute(null);
        }
    }

    /// <summary>
    /// Viernes pide presencia: se muestra sin robar el foco del teclado, para no interrumpir lo que
    /// el usuario esté escribiendo en otra ventana.
    /// </summary>
    private void ViewModelOnActivationRequested(object? sender, ShellActivationRequest request)
    {
        if (_window is null)
        {
            return;
        }

        var wasHidden = !_window.IsVisible;
        if (wasHidden)
        {
            _window.Show();
            _ = _viewModel?.SetShellVisibilityAsync(true, CancellationToken.None);
            _trayIcon?.SetWindowVisible(true);
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Topmost = true;
        _window.ShowWithoutStealingFocus();

        // Sólo hay llegada cuando realmente venía de estar guardado; si ya estaba a la vista, saltar
        // desde la bandeja sería una animación mentirosa.
        if (wasHidden)
        {
            _window.PlayArrivalFromTray();
        }

        if (request.Reason == ShellActivationReason.Reminder)
        {
            _trayIcon?.ShowBalloon(request.Title, request.Detail);
        }
    }

    private void ToggleAutoStart()
    {
        var status = _autoStartService.GetStatus();
        var result = status.IsRegistered
            ? _autoStartService.Disable()
            : _autoStartService.Enable();

        var refreshedStatus = _autoStartService.GetStatus();
        _trayIcon?.SetAutoStart(refreshedStatus.IsConfiguredForCurrentExecutable);
        _trayIcon?.ShowBalloon(
            "Viernes",
            result.Succeeded
                ? refreshedStatus.IsRegistered
                    ? "Voy a iniciar con tu sesión de Windows."
                    : "Ya no iniciaré automáticamente."
                : result.ErrorMessage ?? "No pude cambiar el inicio automático.");
    }

    private async void RequestExit()
    {
        IsExitRequested = true;
        _window?.SaveOrbPlacement();
        _window?.Hide();
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            _viewModel.ActivationRequested -= ViewModelOnActivationRequested;
            await _viewModel.DisposeAsync();
        }

        Shutdown();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsMuted))
        {
            _trayIcon?.SetMuted(_viewModel.IsMuted);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsWakeWordEnabled))
        {
            _trayIcon?.SetWakeWordEnabled(_viewModel.IsWakeWordEnabled);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsListeningWhileHidden))
        {
            _trayIcon?.SetListenWhileHidden(_viewModel.IsListeningWhileHidden);
        }
        else if (e.PropertyName == nameof(MainViewModel.OrbShape))
        {
            _trayIcon?.SetOrbShape(_viewModel.OrbShape.ToString());
        }
        else if (e.PropertyName == nameof(MainViewModel.StatusText))
        {
            _trayIcon?.SetStatus(_viewModel.StatusText);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process did not own the mutex (secondary instance).
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
