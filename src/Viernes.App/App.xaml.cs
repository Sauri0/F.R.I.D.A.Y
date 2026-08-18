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
    private PanicSwitch? _panicSwitch;
    private Controls.EdgeMark? _edgeMark;
    private readonly MonitorSlots _monitorSlots = new();

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

        if (e.Args.Contains("--check-voice") ||
            e.Args.Contains("--check-listen") ||
            e.Args.Contains("--check-whisper") ||
            e.Args.Contains("--check-mic"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = CheckVoiceAndExitAsync(
                e.Args.Contains("--check-listen"),
                e.Args.Contains("--check-whisper"),
                e.Args.Contains("--check-mic"));
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Diagnostics.RuntimeTrace.Reset();

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

        // El freno se engancha antes de nada más: si algo falla después, el corte ya existe.
        _panicSwitch = new PanicSwitch(() => _viewModel?.Panic());
        var panicReady = _panicSwitch.TryAttach(_window);
        Diagnostics.RuntimeTrace.Write(
            "freno",
            panicReady ? $"{PanicSwitch.Shortcut} registrado" : "NO se pudo registrar el atajo");
        if (!panicReady)
        {
            // Que otra aplicación tenga tomado el atajo no puede quedar en silencio: un freno que
            // nadie sabe que no existe es peor que no tener freno.
            _trayIcon.ShowBalloon(
                "Viernes",
                $"No pude registrar {PanicSwitch.Shortcut}: otra aplicación lo tiene tomado. " +
                "El corte de emergencia por teclado no está disponible.");
        }

        var autoStartStatus = _autoStartService.GetStatus();
        _trayIcon.SetAutoStart(autoStartStatus.IsConfiguredForCurrentExecutable);
        _trayIcon.SetListenWhileHidden(_viewModel.IsListeningWhileHidden);
        _trayIcon.SetOrbShape(_viewModel.OrbShape.ToString());
        // Aparecer no puede robar el teclado. Show() activa la ventana y Activate() insistía: si en
        // ese momento estabas escribiendo en otra aplicación, las teclas siguientes se las comía
        // Viernes. La ventana ya declara ShowActivated="False" —lo mismo que hace OrbSnapshot para
        // dibujar sin molestar— y esto la trae al frente sin activarla, que es presencia sin
        // interrupción.
        _window.Show();
        _window.ShowWithoutStealingFocus();
    }

    private async Task CheckVoiceAndExitAsync(bool listenOnly, bool whisperOnly = false, bool micOnly = false)
    {
        try
        {
            var report = micOnly
                ? await Diagnostics.VoiceDiagnostics.MeasureMicrophoneAsync()
                : whisperOnly
                    ? await Diagnostics.WhisperBenchmark.RunAsync()
                    : listenOnly
                        ? await Diagnostics.VoiceDiagnostics.ListenAsync()
                        : await Diagnostics.VoiceDiagnostics.RunAsync();
            var path = Path.Combine(Path.GetTempPath(), "viernes-voice-check.txt");
            await File.WriteAllTextAsync(path, report);
            Console.WriteLine(report);
            Console.WriteLine(path);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"El diagnóstico de voz falló: {exception}");
        }
        finally
        {
            Shutdown();
        }
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

    /// <summary>
    /// Muestra u oculta la marca según la única regla que la justifica: si el micrófono puede tomar
    /// audio y el orbe no está a la vista, tiene que haber algo en pantalla.
    /// </summary>
    /// <summary>
    /// Lleva el orbe al monitor donde está el cursor, a la posición que recordaba de ese monitor.
    /// </summary>
    /// <remarks>
    /// Si ya está en ese monitor no toca nada: mover el orbe sin motivo es movimiento gratuito, y a
    /// las ocho horas de uso el movimiento gratuito es el enemigo. Sólo se reubica cuando lo llamaste
    /// desde otra pantalla, que es el caso donde hacerte buscarlo sería peor.
    /// </remarks>
    private void MoveToCursorMonitor()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var (targetKey, workArea) = MonitorSlots.MonitorUnderCursor();
            var currentCentre = new System.Windows.Point(
                _window.Left + (_window.Width / 2),
                _window.Top + (_window.Height / 2));
            var (currentKey, _) = MonitorSlots.MonitorAt(currentCentre);

            if (string.Equals(currentKey, targetKey, StringComparison.Ordinal))
            {
                return;
            }

            var size = new System.Windows.Size(_window.Width, _window.Height);
            var slot = _monitorSlots.SlotFor(targetKey, workArea, size);
            _window.Left = slot.X;
            _window.Top = slot.Y;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Si la topología de pantallas cambió justo ahora, quedarse donde está es aceptable.
        }
    }

    private void UpdateEdgeMark()
    {
        if (_window is null || _viewModel is null)
        {
            return;
        }

        var shouldShow = !_window.IsVisible &&
            _viewModel.IsWakeWordEnabled &&
            !_viewModel.IsMuted;

        if (!shouldShow)
        {
            HideEdgeMark();
            return;
        }

        _edgeMark ??= new Controls.EdgeMark(ToggleWindowVisibility);
        var bounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
        _edgeMark.SnapNear(bounds, SystemParameters.WorkArea);
        _edgeMark.Show();
        _edgeMark.StartBreathing();
    }

    private void HideEdgeMark()
    {
        _edgeMark?.Hide();
    }

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
            UpdateEdgeMark();
            return;
        }

        HideEdgeMark();
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

        HideEdgeMark();
        MoveToCursorMonitor();
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
        _panicSwitch?.Dispose();
        _panicSwitch = null;
        _edgeMark?.Close();
        _edgeMark = null;
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

            // Silenciar es el corte duro: sin micrófono no hay nada que anunciar, y ésa es la única
            // forma de que no quede absolutamente nada en pantalla.
            UpdateEdgeMark();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsWakeWordEnabled))
        {
            _trayIcon?.SetWakeWordEnabled(_viewModel.IsWakeWordEnabled);
            UpdateEdgeMark();
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
        else if (e.PropertyName == nameof(MainViewModel.AssistantName))
        {
            _trayIcon?.SetAssistantName(_viewModel.AssistantName);
            if (_window is not null)
            {
                _window.Title = _viewModel.AssistantName;
            }
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
