using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Viernes.App.Controls;
using Viernes.App.Infrastructure;
using Viernes.App.Services;
using Viernes.App.Shell;

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
    private PanelKind _activePanel = PanelKind.Escribir;
    private PanelKind? _requestedPanel;
    private bool _isPanelOpen;
    private bool _isPanelHovered;
    private bool _blockedByPolicy;
    private double _panelLifeProgress;
    private DispatcherTimer? _lifeTimer;
    private DateTime _lifeTick;
    private TimeSpan _lifeSpent;
    private TimeSpan _lifeWall;
    private string _dictationText = string.Empty;
    private string _reminderTitle = string.Empty;
    private string _reminderDetail = string.Empty;
    private string _policyMessage = "No hay ninguna regla local frenando nada.";

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
        ClosePanelCommand = new RelayCommand(ClosePanel);
        CancelTurnCommand = new AsyncRelayCommand(CancelTurnAsync, () => true, ShowError);
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
            OnPropertyChanged(nameof(IsDictationVisible));
            OnPropertyChanged(nameof(IsStatePillSuppressed));
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

    /// <summary>
    /// El registro con el que Viernes está diciendo lo que dice. El orbe lo convierte en un gesto.
    /// </summary>
    /// <remarks>
    /// Va como evento y no como propiedad porque un ánimo no es una condición que dure: se dispara,
    /// dura lo que dice su tabla y se apaga solo. Una propiedad obligaría a alguien a acordarse de
    /// volver a ponerla en nulo, y ese alguien tarde o temprano se olvida.
    /// </remarks>
    public event EventHandler<OrbMood>? MoodShown;

    /// <summary>Filas de agenda, recordatorios o memoria. Vacío en cualquier otro caso.</summary>
    public ObservableCollection<BubbleListItem> ListItems { get; } = [];

    public bool AreStepsVisible => Steps.Count > 0 && State == AssistantVisualState.Thinking;

    public bool AreListItemsVisible => ListItems.Count > 0 && !AreStepsVisible && !IsExpanded;

    public bool IsInputAreaVisible => IsExpanded && !IsConfirmationVisible;
    public bool IsConfirmationAreaVisible => IsExpanded && IsConfirmationVisible;
    public bool IsMessageVisible => !AreStepsVisible && !AreListItemsVisible;

    /// <summary>Sólo el orbe, sin vidrio al lado.</summary>
    public bool IsMinimalShellVisible => !IsPanelOpen;

    /// <summary>Hay un desplegable a la vista.</summary>
    public bool IsAssistantShellVisible => IsPanelOpen;

    /// <summary>
    /// Cuál de los trece desplegables está en el vidrio.
    /// </summary>
    /// <remarks>
    /// Nunca hay dos: el vidrio es uno solo y cambia de forma. Por eso esto es un valor y no una
    /// colección de banderas — con banderas basta olvidarse de bajar una para que queden dos paneles
    /// dibujados uno encima del otro.
    /// </remarks>
    public PanelKind ActivePanel
    {
        get => _activePanel;
        private set
        {
            if (SetProperty(ref _activePanel, value))
            {
                OnPropertyChanged(nameof(ActivePanelSpec));
                OnPropertyChanged(nameof(PanelWidth));
                OnPropertyChanged(nameof(PanelHeight));
                OnPropertyChanged(nameof(PanelFamily));
            }
        }
    }

    /// <summary>La ficha del desplegable abierto: medida, familia y tiempo de vida.</summary>
    public PanelSpec ActivePanelSpec => PanelCatalog.For(ActivePanel);

    /// <summary>Ancho del vidrio. Lo anima el vidrio, no la ventana.</summary>
    public double PanelWidth => ActivePanelSpec.Width;

    /// <summary>Alto del vidrio. Lo anima el vidrio, no la ventana.</summary>
    public double PanelHeight => ActivePanelSpec.Height;

    /// <summary>Familia de vidrio del desplegable abierto.</summary>
    public PanelFamily PanelFamily => ActivePanelSpec.Family;

    /// <summary>Si hay un desplegable a la vista.</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        private set
        {
            if (SetProperty(ref _isPanelOpen, value))
            {
                OnPropertyChanged(nameof(IsAssistantShellVisible));
                OnPropertyChanged(nameof(IsMinimalShellVisible));
                OnPropertyChanged(nameof(IsStatePillSuppressed));
            }
        }
    }

    /// <summary>Cuánto avanzó la barra de vida del panel, de 0 a 1.</summary>
    public double PanelLifeProgress
    {
        get => _panelLifeProgress;
        private set => SetProperty(ref _panelLifeProgress, value);
    }

    /// <summary>
    /// El puntero está adentro del panel. Mientras lo esté, el reloj de vida no corre.
    /// </summary>
    /// <remarks>
    /// Un panel que se cierra mientras lo estás leyendo es peor que uno que se queda de más. El tope
    /// de tres vidas existe para que apoyar el mouse encima no lo convierta en una ventana fija.
    /// </remarks>
    public bool IsPanelHovered
    {
        get => _isPanelHovered;
        set => SetProperty(ref _isPanelHovered, value);
    }

    /// <summary>
    /// Lo que se está dictando. Vacío mientras el micrófono todavía no devolvió palabras.
    /// </summary>
    /// <remarks>
    /// Hoy el reconocedor entrega la frase entera al soltar el micrófono: el runtime arranca con
    /// <c>EmitPartialTranscriptions = false</c> y no publica parciales. La burbuja está hecha para
    /// recibirlas —se llena palabra por palabra y la última va en itálica hasta confirmarse—, así que
    /// encenderla es publicar parciales, no rehacer nada de acá.
    /// </remarks>
    public string DictationText
    {
        get => _dictationText;
        private set
        {
            if (SetProperty(ref _dictationText, value))
            {
                OnPropertyChanged(nameof(IsDictationVisible));
                OnPropertyChanged(nameof(IsStatePillSuppressed));
            }
        }
    }

    /// <summary>Si la burbuja de dictado está a la vista.</summary>
    public bool IsDictationVisible =>
        State == AssistantVisualState.Listening ||
        (DictationText.Length > 0 && State == AssistantVisualState.Thinking && !IsPanelOpen);

    /// <summary>
    /// La píldora se calla: con el desplegable abierto o dictando, sobra.
    /// </summary>
    /// <remarks>
    /// El nombre del estado ya está adentro del panel, y en la burbuja de dictado ya está la frase.
    /// Dos veces lo mismo a diez píxeles de distancia se lee como un error de la interfaz.
    /// </remarks>
    public bool IsStatePillSuppressed => IsPanelOpen || IsDictationVisible;

    /// <summary>Qué recordatorio llegó a su hora. Lo llena el pedido de presencia del runtime.</summary>
    public string ReminderTitle
    {
        get => _reminderTitle;
        private set => SetProperty(ref _reminderTitle, value);
    }

    /// <summary>El detalle del recordatorio: la hora, y hace cuánto venció.</summary>
    public string ReminderDetail
    {
        get => _reminderDetail;
        private set => SetProperty(ref _reminderDetail, value);
    }

    /// <summary>Qué regla local frenó la acción. Sale del paso bloqueado del turno.</summary>
    public string PolicyMessage
    {
        get => _policyMessage;
        private set => SetProperty(ref _policyMessage, value);
    }

    /// <summary>
    /// Abre un desplegable a pedido. Es la puerta para todo lo que el runtime todavía no publica.
    /// </summary>
    /// <remarks>
    /// Los paneles que se deducen del estado —escribir, trabajando, permiso, sin red, calendario,
    /// memoria— se abren solos. Los que dependen de datos que el proyecto todavía no tiene —muestras,
    /// caja, gastos, música, presupuesto— se abren llamando acá, y hasta que existan esos datos
    /// muestran que no hay nada conectado en vez de inventar números.
    /// </remarks>
    public void ShowPanel(PanelKind kind)
    {
        _requestedPanel = kind;
        RefreshPanel();
    }

    /// <summary>Cierra lo que esté abierto y vuelve al orbe solo.</summary>
    public void ClosePanel()
    {
        _requestedPanel = null;
        IsExpanded = false;
        RefreshPanel();
    }

    /// <summary>
    /// Decide qué desplegable corresponde ahora mismo, y si corresponde alguno.
    /// </summary>
    /// <remarks>
    /// El orden es la prioridad: una decisión pendiente le gana a todo, porque es lo único que está
    /// esperando al usuario. Lo pedido a mano va después de lo que exige una respuesta y antes de lo
    /// que sólo informa.
    /// </remarks>
    private void RefreshPanel()
    {
        var kind = ResolvePanel();
        if (kind is null)
        {
            _requestedPanel = null;
            IsPanelOpen = false;
            StopLifeClock();
            return;
        }

        var changed = !IsPanelOpen || ActivePanel != kind.Value;
        ActivePanel = kind.Value;
        IsPanelOpen = true;

        if (changed)
        {
            StartLifeClock();
        }
    }

    private PanelKind? ResolvePanel()
    {
        if (IsConfirmationVisible)
        {
            return PanelKind.Permiso;
        }

        if (IsExpanded)
        {
            return PanelKind.Escribir;
        }

        if (AreStepsVisible)
        {
            return PanelKind.Trabajando;
        }

        if (_requestedPanel is { } requested)
        {
            return requested;
        }

        if (State == AssistantVisualState.Offline)
        {
            return PanelKind.SinRed;
        }

        if (AreListItemsVisible)
        {
            return _listKind == BubbleListKind.Memoria ? PanelKind.Memoria : PanelKind.Calendario;
        }

        if (_blockedByPolicy)
        {
            return PanelKind.Politica;
        }

        // El resultado de un turno vive unos segundos en el panel de conversación: si escribiste la
        // pregunta, la respuesta se lee. Si la charla es hablada el orbe se queda solo —el color y el
        // fluido ya dicen en qué estado está—, porque desplegar el vidrio en cada turno convierte una
        // conversación en una ventana que aparece y desaparece sin parar.
        return _isPresentingResult && _isSpokenConversation != true ? PanelKind.Escribir : null;
    }

    private void StartLifeClock()
    {
        StopLifeClock();
        var life = ActivePanelSpec.LifeMs;
        PanelLifeProgress = 0;
        if (life <= 0)
        {
            return;
        }

        _lifeSpent = TimeSpan.Zero;
        _lifeWall = TimeSpan.Zero;
        _lifeTick = DateTime.UtcNow;
        _lifeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _lifeTimer.Tick += (_, _) => TickLifeClock(life);
        _lifeTimer.Start();
    }

    private void TickLifeClock(int lifeMs)
    {
        var now = DateTime.UtcNow;
        var delta = now - _lifeTick;
        _lifeTick = now;
        _lifeWall += delta;

        if (!IsPanelHovered)
        {
            _lifeSpent += delta;
        }

        var life = TimeSpan.FromMilliseconds(lifeMs);
        PanelLifeProgress = Math.Clamp(_lifeSpent / life, 0, 1);

        // El tope de tres vidas: apoyar el mouse encima estira la lectura, no convierte el panel en
        // una ventana que hay que cerrar a mano.
        if (_lifeSpent < life && _lifeWall < life * 3)
        {
            return;
        }

        StopLifeClock();
        _requestedPanel = null;
        _isPresentingResult = false;
        _blockedByPolicy = false;
        RefreshPanel();
    }

    private void StopLifeClock()
    {
        _lifeTimer?.Stop();
        _lifeTimer = null;
        PanelLifeProgress = 0;
    }

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
    /// La cápsula se muestra sólo si hay algo que nombrar. En reposo no dice nada: un cartel que
    /// dice «acá estoy» todo el día deja de decir algo a los diez minutos y tapa el escritorio.
    /// </summary>
    public bool IsStateLabelVisible => State != AssistantVisualState.Idle && StateShortLabel.Length > 0;

    /// <summary>
    /// Cómo se llama el estado en dos palabras.
    /// </summary>
    /// <remarks>
    /// Sale de la misma tabla que dibuja el orbe. Acá había una segunda lista escrita a mano que
    /// cubría siete de los quince estados y devolvía cadena vacía para el resto, así que el orbe
    /// dibujaba «un proyecto te espera» y al lado no había nombre. Dos listas de lo mismo se
    /// desincronizan; una sola, no puede.
    /// </remarks>
    public string StateShortLabel => OrbPalette.For(State).PillLabel;

    public ICommand SendCommand => _sendCommand;
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleWakeWordCommand { get; }
    public ICommand ToggleListenWhileHiddenCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
    public ICommand ClearInputCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand DismissConfirmationCommand { get; }

    /// <summary>Retrae el desplegable sin tocar nada más.</summary>
    public ICommand ClosePanelCommand { get; }

    /// <summary>Corta el turno en curso. Vuelve al reposo sin dejar nada en pantalla.</summary>
    public ICommand CancelTurnCommand { get; }

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
        {
            // Un recordatorio es el único desplegable que el usuario no pidió: llega, viene al frente
            // y habla. Por eso se abre acá y no espera a que algo más lo deduzca.
            if (request.Reason == ShellActivationReason.Reminder)
            {
                ReminderTitle = request.Title;
                ReminderDetail = request.Detail;
                ShowPanel(PanelKind.Recordatorio);
            }

            ActivationRequested?.Invoke(this, request);
        });

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

            // Lo que se dictó se muestra en la burbuja, no en el panel: abrir un desplegable para
            // repetir lo que el usuario acaba de decir es ruido. Al empezar a escuchar se vacía.
            DictationText = update.State switch
            {
                AssistantVisualState.Listening => string.Empty,
                AssistantVisualState.Thinking when previousState == AssistantVisualState.Listening =>
                    update.Message ?? string.Empty,
                _ => DictationText
            };

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

                // Un paso frenado por la política no es un error del turno: es el sistema cumpliendo
                // lo que prometió, y merece su propio desplegable en vez de perderse en una lista.
                var blocked = Steps.FirstOrDefault(step => step.IsBlocked);
                _blockedByPolicy = blocked is not null;
                if (blocked is not null)
                {
                    PolicyMessage = blocked.Label;
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
                _requestedPanel = null;
                _blockedByPolicy = false;
                IsExpanded = false;
                Steps.Clear();
                ListItems.Clear();
                NotifyContentProperties();
            }
            else if (IsResting(update.State) &&
                previousState == AssistantVisualState.Thinking &&
                !string.IsNullOrWhiteSpace(update.Message))
            {
                PresentResultBriefly();
            }

            _sendCommand.RaiseCanExecuteChanged();

            // El ánimo se avisa al final, cuando el estado de fondo al que se monta ya está puesto.
            if (update.Mood is { } mood)
            {
                MoodShown?.Invoke(this, mood);
            }
        });
    }

    /// <summary>
    /// Si el turno terminó, mire lo que mire el orbe.
    /// </summary>
    /// <remarks>
    /// Antes acá decía <c>== Idle</c>, y alcanzaba porque volver al reposo era el único final
    /// posible. Ahora terminar un turno puede dejar el orbe en guardia, esperándote o trabajando de
    /// fondo, y comparar contra reposo se leía como que el turno nunca cerraba: la respuesta no
    /// llegaba a mostrarse.
    /// </remarks>
    private static bool IsResting(AssistantVisualState state) => state is
        AssistantVisualState.Idle or
        AssistantVisualState.Watching or
        AssistantVisualState.Background or
        AssistantVisualState.WaitingForYou or
        AssistantVisualState.ProjectWaiting or
        AssistantVisualState.Deaf;

    private void ShowError(Exception exception)
    {
        State = AssistantVisualState.Error;
        StatusText = "No pude completar eso";
        MessageText = exception.Message;
    }

    private Task ConfirmAsync(CancellationToken cancellationToken) =>
        _runtime.ConfirmPendingAsync(cancellationToken);

    private Task CancelTurnAsync(CancellationToken cancellationToken)
    {
        ClosePanel();
        return _runtime.EndConversationAsync("Turno cancelado", quiet: true, cancellationToken);
    }

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
        RefreshPanel();
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
        StopLifeClock();
        _resultPresentationCancellation?.Cancel();
        _resultPresentationCancellation?.Dispose();
        _runtime.Updated -= RuntimeOnUpdated;
        _runtime.ActivationRequested -= RuntimeOnActivationRequested;
        await _runtime.DisposeAsync();
    }
}
