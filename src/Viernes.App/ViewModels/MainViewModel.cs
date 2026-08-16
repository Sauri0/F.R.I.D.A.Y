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
    private string _confirmationTitle = "Confirmación necesaria";
    private string _confirmationDetail = string.Empty;
    private bool _isPresentingResult;
    private CancellationTokenSource? _resultPresentationCancellation;

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

    /// <summary>Filas de agenda, recordatorios o memoria. Vacío en cualquier otro caso.</summary>
    public ObservableCollection<BubbleListItem> ListItems { get; } = [];

    public bool AreStepsVisible => Steps.Count > 0 && State == AssistantVisualState.Thinking;

    public bool AreListItemsVisible => ListItems.Count > 0 && !AreStepsVisible && !IsExpanded;

    public bool IsInputAreaVisible => IsExpanded && !IsConfirmationVisible;
    public bool IsConfirmationAreaVisible => IsExpanded && IsConfirmationVisible;
    public bool IsMessageVisible => !AreStepsVisible && !AreListItemsVisible;
    public bool IsMinimalShellVisible =>
        State == AssistantVisualState.Idle && !IsExpanded && !IsConfirmationVisible && !_isPresentingResult;
    public bool IsAssistantShellVisible => !IsMinimalShellVisible;
    public double WidgetWidth => IsMinimalShellVisible ? 78 : IsExpanded ? 352 : 344;

    // 168 px es la altura de excepción para pasos y listas: sigue siendo temporal, nunca un panel.
    public double WidgetHeight => IsMinimalShellVisible
        ? 78
        : IsExpanded
            ? 158
            : AreStepsVisible || AreListItemsVisible
                ? 168
                : 112;

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
    public string ModeLabel => IsCloudConfigured ? "OPENROUTER" : "LOCAL SEGURO";
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
    public bool IsStateLabelVisible => State != AssistantVisualState.Idle;
    public string StateShortLabel => State switch
    {
        AssistantVisualState.Listening => "Escuchando",
        AssistantVisualState.Thinking => "Pensando",
        AssistantVisualState.Speaking => "Hablando",
        AssistantVisualState.Attention => "Revisar",
        AssistantVisualState.Error => "Atención",
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

        await _runtime.StartPushToTalkAsync(cancellationToken);
    }

    public Task StopPushToTalkAsync(CancellationToken cancellationToken) =>
        _runtime.StopPushToTalkAsync(cancellationToken);

    public Task CancelVoiceAsync(CancellationToken cancellationToken) =>
        _runtime.CancelSpeechAsync(cancellationToken);

    public Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken) =>
        _runtime.SetShellVisibilityAsync(visible, cancellationToken);

    public void OpenTextInput()
    {
        IsExpanded = true;
        MessageText = "¿Qué necesitás?";
        StatusText = "Escribí y presioná Enter";
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
        await _runtime.SendAsync(text, cancellationToken);
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
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var previousState = State;
            State = update.State;
            StatusText = update.Status;
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
                Steps.Clear();
                if (update.Steps is not null)
                {
                    foreach (var step in update.Steps)
                    {
                        Steps.Add(new TurnStepViewModel(step));
                    }
                }

                NotifyContentProperties();
            }

            if (update.Items is not null || update.ClearItems)
            {
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

            if (update.State == AssistantVisualState.Idle &&
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
