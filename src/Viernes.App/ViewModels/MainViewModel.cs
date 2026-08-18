using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Viernes.App.Infrastructure;
using Viernes.App.Services;

namespace Viernes.App.ViewModels;

internal sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAssistantRuntime _runtime;
    private readonly AsyncRelayCommand _sendCommand;
    private AssistantVisualState _state = AssistantVisualState.Idle;
    private string _statusText = "Preparando sistemas locales…";
    private string _messageText = "Un momento, ya estoy acá.";
    private string _inputText = string.Empty;
    private bool _isMuted;
    private bool _isMicrophoneActive;
    private bool _isWakeWordEnabled;
    private bool _isExpanded;
    private bool _isConfirmationVisible;
    private bool _isListeningWhileHidden = true;
    private Controls.OrbShape _orbShape = Controls.OrbShape.Gota;
    private bool _isConversationActive;
    private double _audioLevel;
    private string _confirmationTitle = "Confirmación necesaria";
    private string _confirmationDetail = string.Empty;
    private bool _isPresentingResult;
    private BubbleListKind _listKind = BubbleListKind.Agenda;
    private CancellationTokenSource? _resultPresentationCancellation;

    /// <summary>
    /// Si la conversación en curso se está llevando hablando o escribiendo. <c>null</c> mientras no
    /// hay conversación.
    /// </summary>
    /// <remarks>
    /// El runtime no distingue una de otra —abre el mismo bucle en los dos casos—, así que la marca
    /// quien la abre: el toque en el orbe la reclama escrita, mandar texto la confirma escrita, el
    /// push-to-talk la vuelve hablada, y si se abrió sin que nadie la reclamara fue el wake word,
    /// que es hablada por definición.
    /// </remarks>
    private bool? _isSpokenConversation;

    public MainViewModel(IAssistantRuntime runtime)
    {
        _runtime = runtime;
        _runtime.Updated += RuntimeOnUpdated;
        _runtime.ActivationRequested += RuntimeOnActivationRequested;

        _sendCommand = new AsyncRelayCommand(SendAsync, CanSend, ShowError);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        ToggleWakeWordCommand = new AsyncRelayCommand(ToggleWakeWordAsync, () => true, ShowError);
        ToggleListenWhileHiddenCommand = new AsyncRelayCommand(ToggleListenWhileHiddenAsync, () => true, ShowError);
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        ClearInputCommand = new RelayCommand(() => InputText = string.Empty, () => !string.IsNullOrEmpty(InputText));
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => IsConfirmationVisible, ShowError);
        DismissConfirmationCommand = new RelayCommand(DismissConfirmation, () => IsConfirmationVisible);
    }

    public AssistantVisualState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(IsThinking));
            OnPropertyChanged(nameof(IsSpeaking));
            OnPropertyChanged(nameof(IsAttention));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(IsStateLabelVisible));
            OnPropertyChanged(nameof(StateShortLabel));
            NotifyContentProperties();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Cómo se llama el asistente, para los textos que lo nombran.</summary>
    public string AssistantName => _runtime.AssistantName;

    public string MessageText
    {
        get => _messageText;
        private set => SetProperty(ref _messageText, value);
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
            {
                _sendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(MuteToolTip));
                OnPropertyChanged(nameof(PrivacyGlyph));
                OnPropertyChanged(nameof(PrivacyHint));
            }
        }
    }

    public bool IsMicrophoneActive
    {
        get => _isMicrophoneActive;
        private set
        {
            if (SetProperty(ref _isMicrophoneActive, value))
            {
                OnPropertyChanged(nameof(PrivacyGlyph));
                OnPropertyChanged(nameof(PrivacyHint));
            }
        }
    }

    public bool IsWakeWordEnabled
    {
        get => _isWakeWordEnabled;
        private set
        {
            if (SetProperty(ref _isWakeWordEnabled, value))
            {
                OnPropertyChanged(nameof(PrivacyHint));
                OnPropertyChanged(nameof(WakeWordToolTip));
            }
        }
    }

    public Controls.OrbShape OrbShape
    {
        get => _orbShape;
        private set => SetProperty(ref _orbShape, value);
    }

    /// <summary>Nivel instantáneo del micrófono. La gota lo usa para crecer con tu voz.</summary>
    public double AudioLevel
    {
        get => _audioLevel;
        private set => SetProperty(ref _audioLevel, value);
    }

    public bool IsConversationActive
    {
        get => _isConversationActive;
        private set
        {
            if (SetProperty(ref _isConversationActive, value))
            {
                NotifyContentProperties();
            }
        }
    }

    public async Task SetOrbShapeAsync(Controls.OrbShape shape, CancellationToken cancellationToken)
    {
        await _runtime.SetOrbShapeAsync(shape, cancellationToken);
        OrbShape = _runtime.OrbShape;
    }

    public bool IsListeningWhileHidden
    {
        get => _isListeningWhileHidden;
        private set
        {
            if (SetProperty(ref _isListeningWhileHidden, value))
            {
                OnPropertyChanged(nameof(PrivacyHint));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsInputAreaVisible));
                OnPropertyChanged(nameof(IsConfirmationAreaVisible));
                NotifyContentProperties();
            }
        }
    }

    public bool IsConfirmationVisible
    {
        get => _isConfirmationVisible;
        private set
        {
            if (!SetProperty(ref _isConfirmationVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsInputAreaVisible));
            OnPropertyChanged(nameof(IsConfirmationAreaVisible));
            NotifyShellProperties();
            ((AsyncRelayCommand)ConfirmCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DismissConfirmationCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Pasos del turno en curso. Hacen visible que una respuesta convincente no ejecutó nada por su
    /// cuenta: cada herramienta figura, y se ve si la política la dejó pasar o la bloqueó.
    /// </summary>
    public ObservableCollection<TurnStepViewModel> Steps { get; } = [];

    /// <summary>Se dispara cuando cierra un paso real. El orbe lo convierte en un latido.</summary>
    public event EventHandler? StepAdvanced;

    /// <summary>Filas de agenda, recordatorios o memoria. Vacío en cualquier otro caso.</summary>
    public ObservableCollection<BubbleListItem> ListItems { get; } = [];

    public bool AreStepsVisible => Steps.Count > 0 && State == AssistantVisualState.Thinking;

    public bool AreListItemsVisible => ListItems.Count > 0 && !AreStepsVisible && !IsExpanded;

    public bool IsInputAreaVisible => IsExpanded && !IsConfirmationVisible;
    public bool IsConfirmationAreaVisible => IsExpanded && IsConfirmationVisible;
    public bool IsMessageVisible => !AreStepsVisible && !AreListItemsVisible;
    /// <summary>
    /// Durante una conversación hablada el orbe se queda solo: el color y el fluido ya dicen en qué
    /// estado está, y desplegar la burbuja en cada turno convierte una charla en una ventana que
    /// aparece y desaparece sin parar. La burbuja se abre al tocarla, o cuando hay que decidir algo.
    /// </summary>
    /// <remarks>
    /// Escrita es lo contrario: si escribiste la pregunta, la respuesta se lee, y encogerse a 108 px
    /// la dejaba dibujada adentro de una burbuja colapsada. Sólo se veía cambiar el color del orbe.
    /// Se notaba únicamente con el micrófono silenciado, porque ahí el runtime ni siquiera llega a
    /// abrir la conversación y la burbuja quedaba visible por el otro camino.
    /// </remarks>
    public bool IsMinimalShellVisible =>
        !IsExpanded &&
        !IsConfirmationVisible &&
        (IsConversationActive
            ? _isSpokenConversation != false
            : IsRestingState && !_isPresentingResult);

    /// <summary>
    /// Reposo no es sólo <see cref="AssistantVisualState.Idle"/>: sin clave o sin red tampoco está
    /// haciendo nada, y exigir Idle dejaba la burbuja abierta para siempre en una instalación a
    /// medias —justo la que menos tiene para contar.
    /// </summary>
    private bool IsRestingState => State
        is AssistantVisualState.Idle
        or AssistantVisualState.Unconfigured
        or AssistantVisualState.Offline;

    public bool IsAssistantShellVisible => !IsMinimalShellVisible;
    public double WidgetWidth => IsMinimalShellVisible ? 108 : IsExpanded ? 368 : 360;

    /// <summary>
    /// Dos formas para listas, no una: tira de 120 para la agenda, hoja de 176 para la memoria.
    /// </summary>
    /// <remarks>
    /// Una agenda es una línea de tiempo y se lee de un vistazo; una memoria son registros con
    /// identificador y eso pide fila completa. Darle 176 px a una agenda de dos eventos deja un
    /// vacío que se lee como error. Ninguna de las dos scrollea: el scroll invita a quedarse, y
    /// quedarse es el panel permanente entrando por la ventana.
    /// </remarks>
    public double WidgetHeight => IsMinimalShellVisible
        ? 108
        : IsExpanded
            ? 168
            : AreStepsVisible
                ? 176
                : AreListItemsVisible
                    ? _listKind == BubbleListKind.Memoria ? 176 : 120
                    : 120;

    public string ConfirmationTitle
    {
        get => _confirmationTitle;
        private set => SetProperty(ref _confirmationTitle, value);
    }

    public string ConfirmationDetail
    {
        get => _confirmationDetail;
        private set => SetProperty(ref _confirmationDetail, value);
    }

    public bool IsCloudConfigured => _runtime.IsCloudConfigured;

    /// <summary>Autorización de gasto viva. El orbe la muestra tiñendo el borde de cálido.</summary>
    public bool HasSpendAuthorization => _runtime.HasSpendAuthorization;
    /// <summary>
    /// Nada en mayúsculas, y nada que suene a alarma.
    /// </summary>
    /// <remarks>
    /// Decía «LOCAL SEGURO», que mezcla tres situaciones distintas y ninguna es un error. En
    /// mayúsculas suena a que algo falló, cuando lo que hay que decir es lo contrario: falta
    /// configurar y casi todo sigue funcionando.
    /// </remarks>
    public string ModeLabel => IsCloudConfigured ? "OpenRouter" : "Falta la clave";
    public string MuteGlyph => IsMuted ? "\uE74F" : "\uE767";
    public string MuteToolTip => IsMuted ? "Activar voz" : "Silenciar voz";
    public string PrivacyGlyph => IsMuted ? "\uE74F" : "\uE720";
    public string PrivacyHint => IsMuted
        ? "Micrófono y voz silenciados"
        : IsMicrophoneActive
            ? IsWakeWordEnabled
                ? "Micrófono activo · atento a tu nombre (demo local)"
                : "Micrófono activo · dictado local"
            : IsWakeWordEnabled
                ? IsListeningWhileHidden
                    ? "Atento a tu nombre, incluso oculto · PTT disponible"
                    : "Atento a tu nombre · PTT disponible"
                : "Micrófono inactivo · PTT disponible";
    public string WakeWordToolTip => IsWakeWordEnabled
        ? "Desactivar activación por voz (demo local)"
        : "Activar por voz (demo local)";
    public bool IsIdle => State == AssistantVisualState.Idle;
    public bool IsListening => State == AssistantVisualState.Listening;
    public bool IsThinking => State == AssistantVisualState.Thinking;
    public bool IsSpeaking => State == AssistantVisualState.Speaking;
    public bool IsAttention => State == AssistantVisualState.Attention;
    public bool IsError => State == AssistantVisualState.Error;
    /// <summary>
    /// La cápsula se muestra sólo si tiene texto. Antes bastaba con no estar en reposo, y los
    /// estados sin caso en <see cref="StateShortLabel"/> dejaban una cápsula vacía sobre el orbe.
    /// </summary>
    public bool IsStateLabelVisible => StateShortLabel.Length > 0;
    public string StateShortLabel => State switch
    {
        AssistantVisualState.Listening => "Escuchando",
        AssistantVisualState.Thinking => "Pensando",
        AssistantVisualState.Speaking => "Hablando",
        AssistantVisualState.Attention => "Revisar",
        AssistantVisualState.Error => "Atención",

        // Capacidad reducida no es falla: dicen qué falta, no que algo se rompió.
        AssistantVisualState.Unconfigured => "Sin clave",
        AssistantVisualState.Offline => "Sin red",
        _ => string.Empty
    };

    public ICommand SendCommand => _sendCommand;
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleWakeWordCommand { get; }
    public ICommand ToggleListenWhileHiddenCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand ClearInputCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand DismissConfirmationCommand { get; }

    public event EventHandler<ShellActivationRequest>? ActivationRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _runtime.InitializeAsync(cancellationToken);
        IsMuted = _runtime.IsMuted;
        IsWakeWordEnabled = _runtime.IsWakeWordEnabled;
        IsListeningWhileHidden = _runtime.IsListeningWhileHidden;
        OrbShape = _runtime.OrbShape;

        // El nombre recién se conoce acá: la ventana y la bandeja ya se dibujaron con el de fábrica
        // y se enteran del elegido por este aviso.
        OnPropertyChanged(nameof(AssistantName));
        OnPropertyChanged(nameof(IsCloudConfigured));
        OnPropertyChanged(nameof(ModeLabel));
    }

    public async Task StartPushToTalkAsync(CancellationToken cancellationToken)
    {
        if (IsMuted)
        {
            StatusText = "La voz está silenciada";
            return;
        }

        // Hablar deja el orbe solo: si el turno se dijo, la respuesta se dice.
        _isSpokenConversation = true;
        NotifyShellProperties();
        await _runtime.StartPushToTalkAsync(cancellationToken);
    }

    public Task StopPushToTalkAsync(CancellationToken cancellationToken) =>
        _runtime.StopPushToTalkAsync(cancellationToken);

    public Task CancelVoiceAsync(CancellationToken cancellationToken) =>
        _runtime.CancelSpeechAsync(cancellationToken);

    public Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken) =>
        _runtime.SetShellVisibilityAsync(visible, cancellationToken);

    /// <summary>
    /// Tocar el orbe abre el panel y, además, la conversación: a partir de ahí no hay que repetir
    /// el nombre. Tocarlo de nuevo la cierra, que es el gesto inverso.
    /// </summary>
    public void OpenTextInput()
    {
        // Cerrar es el gesto inverso de abrir, así que tiene que encoger, no desplegar. Antes esta
        // línea estaba arriba del todo y valía para los dos casos: tocar el orbe para terminar la
        // charla abría el panel de golpe y lo dejaba abierto, porque nada volvía a cerrarlo.
        if (_runtime.IsConversationActive)
        {
            IsExpanded = false;
            MessageText = "Listo.";
            StatusText = "Conversación cerrada";
            _ = _runtime.EndConversationAsync("Conversación cerrada", quiet: true, CancellationToken.None);
            return;
        }

        IsExpanded = true;

        // Se reclama escrita antes de abrirla: el runtime avisa que la conversación arrancó dentro
        // de StartConversationAsync, y para entonces la marca ya tiene que estar puesta o la burbuja
        // se calcula como hablada y se encoge.
        _isSpokenConversation = false;

        MessageText = "¿Qué necesitás?";
        StatusText = "Te escucho · escribí, o hablá y decime «listo» para cortar";
        _ = _runtime.StartConversationAsync(CancellationToken.None);
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText) && State is not AssistantVisualState.Thinking;

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        InputText = string.Empty;
        MessageText = text;
        IsExpanded = false;

        // Escribir es la prueba definitiva de que este turno se lee, no se escucha: aunque la
        // conversación la hubiera abierto la voz, a partir de acá la respuesta tiene que verse.
        _isSpokenConversation = false;
        NotifyShellProperties();
        await _runtime.SendAsync(text, cancellationToken);
    }

    /// <summary>
    /// Freno de emergencia. Llega del atajo global, no de un comando ni de la conversación.
    /// </summary>
    /// <remarks>
    /// Deliberadamente delgado: sólo reenvía al runtime y refleja el silencio en la interfaz. Todo
    /// lo que se interponga entre apretar el atajo y que Viernes pare es tiempo en el que sigue
    /// haciendo lo que sea que estaba haciendo.
    /// </remarks>
    public void Panic()
    {
        _runtime.Panic();
        IsMuted = true;
    }

    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _runtime.IsMuted = IsMuted;

        if (IsMuted)
        {
            _ = _runtime.CancelSpeechAsync(CancellationToken.None);
            StatusText = "Voz silenciada · el micrófono permanece apagado";
        }
        else
        {
            StatusText = IsWakeWordEnabled
                ? "Wake demo activo · también podés mantener presionado el núcleo"
                : "Voz activa · mantené presionado el núcleo para hablar";
        }
    }

    private async Task ToggleWakeWordAsync(CancellationToken cancellationToken)
    {
        await _runtime.SetWakeWordEnabledAsync(!IsWakeWordEnabled, cancellationToken);
        IsWakeWordEnabled = _runtime.IsWakeWordEnabled;
    }

    private async Task ToggleListenWhileHiddenAsync(CancellationToken cancellationToken)
    {
        await _runtime.SetListenWhileHiddenAsync(!IsListeningWhileHidden, cancellationToken);
        IsListeningWhileHidden = _runtime.IsListeningWhileHidden;
    }

    private void RuntimeOnActivationRequested(object? sender, ShellActivationRequest request) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            ActivationRequested?.Invoke(this, request));

    private void RuntimeOnUpdated(object? sender, AssistantRuntimeUpdate update)
    {
        // El nivel llega decenas de veces por segundo desde el hilo de audio. Invoke bloquea a quien
        // llama hasta que la interfaz termine: usarlo acá frenaba la captura al ritmo del dibujado.
        // InvokeAsync lo deja seguir, que es lo único que este camino necesita.
        if (update.AudioLevel is { } level)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() => AudioLevel = level);
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var previousState = State;
            State = update.State;
            StatusText = update.Status;

            // La marca se decide antes de publicar el cambio: el setter recalcula la burbuja, y si
            // se hiciera después la primera vuelta se dibujaría con el valor viejo.
            var conversationActive = _runtime.IsConversationActive;
            if (conversationActive && !IsConversationActive)
            {
                // Se abrió sin que nadie la reclamara: la abrió el wake word, y llamarla por su
                // nombre es hablarle.
                _isSpokenConversation ??= true;
            }
            else if (!conversationActive)
            {
                // Cerrada: sin esto, una conversación escrita dejaba su marca puesta y la siguiente
                // —abierta por voz— heredaba la burbuja desplegada.
                _isSpokenConversation = null;
            }

            IsConversationActive = conversationActive;
            if (!string.IsNullOrWhiteSpace(update.Message))
            {
                MessageText = update.Message;
            }

            if (update.MicrophoneActive.HasValue)
            {
                IsMicrophoneActive = update.MicrophoneActive.Value;
            }

            if (update.WakeWordEnabled.HasValue)
            {
                IsWakeWordEnabled = update.WakeWordEnabled.Value;
            }

            if (update.Steps is not null || update.ClearSteps)
            {
                var previousCount = Steps.Count;
                Steps.Clear();
                if (update.Steps is not null)
                {
                    foreach (var step in update.Steps)
                    {
                        Steps.Add(new TurnStepViewModel(step));
                    }
                }

                // Un paso nuevo = un latido. Es la única señal honesta de progreso: nada continuo
                // puede decir «avancé», sólo «sigo». Que aparezca un paso sí significa que algo pasó.
                if (Steps.Count > previousCount)
                {
                    StepAdvanced?.Invoke(this, EventArgs.Empty);
                }

                NotifyContentProperties();
            }

            if (update.Items is not null || update.ClearItems)
            {
                _listKind = update.ListKind;
                ListItems.Clear();
                if (update.Items is not null)
                {
                    foreach (var item in update.Items)
                    {
                        ListItems.Add(item);
                    }
                }

                NotifyContentProperties();
            }

            if (update.ClearConfirmation)
            {
                IsConfirmationVisible = false;
            }

            if (update.Confirmation is not null)
            {
                ConfirmationTitle = update.Confirmation.Title;
                ConfirmationDetail = update.Confirmation.Detail;
                IsConfirmationVisible = true;
                IsExpanded = true;
            }

            if (update.Quiet)
            {
                // Se lo pidió el usuario: nada de mostrar la respuesta unos segundos. Se corta la
                // presentación que estuviera corriendo y se encoge ya, que es lo que se ve como
                // «me callé». Dejarla abierta contando lo que hizo se lee como que sigue trabajando.
                _resultPresentationCancellation?.Cancel();
                _isPresentingResult = false;
                IsExpanded = false;
                Steps.Clear();
                ListItems.Clear();
                NotifyContentProperties();
            }
            else if (update.State == AssistantVisualState.Idle &&
                previousState == AssistantVisualState.Thinking &&
                !string.IsNullOrWhiteSpace(update.Message))
            {
                PresentResultBriefly();
            }

            _sendCommand.RaiseCanExecuteChanged();
        });
    }

    private void ShowError(Exception exception)
    {
        State = AssistantVisualState.Error;
        StatusText = "No pude completar eso";
        MessageText = exception.Message;
    }

    private Task ConfirmAsync(CancellationToken cancellationToken) =>
        _runtime.ConfirmPendingAsync(cancellationToken);

    private void DismissConfirmation()
    {
        _runtime.DismissPending();
        IsConfirmationVisible = false;
        StatusText = "Acción cancelada · no se realizó ningún cambio";
    }

    private void PresentResultBriefly()
    {
        _resultPresentationCancellation?.Cancel();
        _resultPresentationCancellation?.Dispose();
        _resultPresentationCancellation = new CancellationTokenSource();
        var cancellationToken = _resultPresentationCancellation.Token;
        _isPresentingResult = true;
        NotifyShellProperties();

        _ = HideResultAfterDelayAsync(cancellationToken);
    }

    private async Task HideResultAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _isPresentingResult = false;
                NotifyShellProperties();
            });
        }
        catch (OperationCanceledException)
        {
            // A newer response owns the presentation window.
        }
    }

    private void NotifyShellProperties()
    {
        OnPropertyChanged(nameof(IsMinimalShellVisible));
        OnPropertyChanged(nameof(IsAssistantShellVisible));
        OnPropertyChanged(nameof(WidgetWidth));
        OnPropertyChanged(nameof(WidgetHeight));
    }

    private void NotifyContentProperties()
    {
        OnPropertyChanged(nameof(AreStepsVisible));
        OnPropertyChanged(nameof(AreListItemsVisible));
        OnPropertyChanged(nameof(IsMessageVisible));
        NotifyShellProperties();
    }

    public async ValueTask DisposeAsync()
    {
        _resultPresentationCancellation?.Cancel();
        _resultPresentationCancellation?.Dispose();
        _runtime.Updated -= RuntimeOnUpdated;
        _runtime.ActivationRequested -= RuntimeOnActivationRequested;
        await _runtime.DisposeAsync();
    }
}
