using System.Net.Http;
using Viernes.App.Controls;
using Viernes.App.Diagnostics;
using Viernes.App.ViewModels;
using Viernes.Core;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Scheduling;
using Viernes.Core.Tools;
using Viernes.Core.Usage;
using Viernes.Core.Voice;
using Viernes.Memory.Persistence;
using Viernes.Platform.Windows.Actions;
using Viernes.Platform.Windows.Speech;
using Viernes.Platform.Windows.Speech.Recognition;
using Viernes.Platform.Windows.Speech.WakeWord;
using Viernes.Platform.Windows.Storage;

// Ambos ensamblados declaran el modo de activación; el shell usa el de la capa de plataforma,
// que es el que se persiste en las preferencias locales.
using VoiceActivationMode = Viernes.Platform.Windows.Storage.VoiceActivationMode;
using SpeechRecognitionResult = Viernes.Platform.Windows.Speech.SpeechRecognitionResult;

namespace Viernes.App.Services;

/// <summary>
/// Conecta el orbe WPF con el core, Whisper/SAPI locales y una demo visible de wake word.
/// Las credenciales se resuelven sólo desde el entorno; nunca pasan por settings ni logs.
/// </summary>
internal sealed class AssistantRuntime : IAssistantRuntime
{
    private const int MaximumSpokenCharacters = 1_200;

    /// <summary>Identifica la confirmación de gasto; no es una tool y nunca llega al modelo.</summary>
    private const string BudgetOverrideCallId = "viernes:budget-override";

    private static readonly System.Globalization.CultureInfo ArgentineCulture =
        System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    private readonly ViernesOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ConversationOrchestrator _orchestrator;
    private readonly UsageLedger _usageLedger;
    private readonly LocalCommandRouter _localCommands;
    private readonly ISpeechService _speechSynthesizer;
    private readonly OpenRouterSpeechClient _neuralVoice;
    private readonly NeuralSpeechPlayer _neuralPlayer = new();
    private CancellationTokenSource? _speechCancellation;

    /// <summary>
    /// Conversación abierta: mientras dure, el micrófono vuelve a abrirse solo después de cada
    /// respuesta y no hace falta repetir el nombre. Se cierra únicamente cuando el usuario lo dice.
    /// </summary>
    private bool _conversationActive;
    private CancellationTokenSource? _conversationCancellation;
    private int _conversationLoopRunning;

    /// <summary>Lo que dijo el usuario en la charla, para destilar al cerrarla. No se persiste.</summary>
    private readonly List<string> _conversationTurns = [];

    /// <summary>
    /// Frases de cierre. Es el inverso del wake word: la palabra que lo despide. Sin esto, un
    /// asistente que no deja de escuchar es un micrófono abierto sin salida.
    /// </summary>
    private static readonly string[] ClosingPhrases =
    [
        "listo", "gracias", "chau", "nada más", "nada mas", "dejá", "deja", "ya está", "ya esta",
        "terminamos", "cortá", "corta", "basta", "salí", "sali", "adiós", "adios"
    ];
    private readonly JsonUserDataStore _dataStore = new();
    private readonly JsonPersonalMemoryStore _memory = new();
    private readonly ReminderScheduler _reminderScheduler;
    private readonly LocalSettingsStore _settingsStore = new();
    private readonly WakeWordRecognitionCoordinator _wakeCoordinator = new();
    private readonly SemaphoreSlim _voiceTransitionGate = new(1, 1);
    private readonly object _confirmationGate = new();

    private ISpeechRecognitionProvider? _recognition;
    private IWakeWordService? _wakeWord;
    private ViernesLocalSettings _settings = new();
    private PendingConfirmation? _pendingConfirmation;

    /// <summary>
    /// Autorización de gasto, deliberadamente frágil: vive sólo en memoria, no se persiste y muere
    /// con el proceso o con el día. Un botón que gasta plata no debería sobrevivir a un reinicio.
    /// </summary>
    private DateOnly? _budgetOverrideDay;
    private string? _budgetOverridePendingInput;
    private CancellationTokenSource? _wakeHandoffCancellation;
    private AssistantVisualState _lastVisualState = AssistantVisualState.Idle;
    private string _recognitionProviderName = "Preparando voz local";
    private string? _recognitionFallbackReason;
    private bool _isMuted;
    private bool _isWakeWordEnabled;
    private bool _listenWhileHidden = true;
    private bool _isInitialized;
    private bool _isShellVisible = true;
    private bool _isDisposed;
    private int _wakeHandoffActive;
    private int _requestActive;

    public AssistantRuntime()
    {
        _options = ViernesOptions.FromEnvironment();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        _usageLedger = ViernesCoreFactory.CreateUsageLedger(_options);
        _orchestrator = ViernesCoreFactory.CreateDefault(
            _httpClient,
            _options,
            _dataStore,
            _usageLedger,
            new WindowsPcActionExecutor());
        _localCommands = new LocalCommandRouter(_orchestrator, _memory);
        _reminderScheduler = new ReminderScheduler(_dataStore);
        _reminderScheduler.ReminderDue += ReminderSchedulerOnReminderDue;
        _neuralVoice = new OpenRouterSpeechClient(
            _httpClient,
            _options,
            SpeechSynthesisOptions.FromEnvironment());
        _speechSynthesizer = new SpeechService(new SpeechServiceOptions
        {
            RecognitionCulture = "es-AR",
            SynthesisCulture = "es-AR",
            EmitPartialTranscriptions = false
        });

        _orchestrator.StateChanged += OrchestratorOnStateChanged;
        _orchestrator.ProgressChanged += OrchestratorOnProgressChanged;
    }

    public event EventHandler<AssistantRuntimeUpdate>? Updated;

    public event EventHandler<ShellActivationRequest>? ActivationRequested;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value)
            {
                return;
            }

            _isMuted = value;
            _ = ApplyMuteAsync(value);
        }
    }

    public bool IsCloudConfigured => _options.HasApiKey;

    public bool IsWakeWordEnabled => _isWakeWordEnabled;

    public bool IsListeningWhileHidden => _listenWhileHidden;

    public bool IsConversationActive => _conversationActive;

    public OrbShape OrbShape { get; private set; } = OrbShape.Gota;

    public async Task SetOrbShapeAsync(OrbShape shape, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (OrbShape == shape)
        {
            return;
        }

        OrbShape = shape;
        await PersistVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            shape == OrbShape.Nube ? "Ahora soy una nube" : "Ahora soy una gota"));
    }

    public bool IsWakeWordDemo => _wakeWord?.IsDemoOnly ?? true;

    public string RecognitionProviderName => _recognitionProviderName;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isInitialized)
        {
            return;
        }

        var loaded = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _settings = loaded.Settings;
        _isMuted = _settings.MicrophoneMuted;
        _isWakeWordEnabled = ResolveWakeEnabled(_settings.VoiceActivation);
        _listenWhileHidden = ResolveListenWhileHidden(_settings.ListenWhileHidden);
        OrbShape = string.Equals(_settings.OrbShape, "Nube", StringComparison.OrdinalIgnoreCase)
            ? OrbShape.Nube
            : OrbShape.Gota;

        var selection = CreateRecognitionSelection(_settings);
        _recognition = selection.Provider;
        _recognitionProviderName = selection.Provider.Info.DisplayName;
        _recognitionFallbackReason = selection.UsedFallback ? selection.FallbackReason : null;
        SubscribeRecognition(_recognition);

        _wakeWord = new SapiWakeWordService(new WakeWordServiceOptions
        {
            Phrases = ResolveWakePhrases(_settings.WakeWordPhrases),
            RecognitionCulture = _settings.RecognitionCulture,
            MinimumConfidence = ResolveWakeConfidence()
        });
        SubscribeWakeWord(_wakeWord);

        await _recognition.SetMicrophoneMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);
        await _speechSynthesizer.SetMicrophoneMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);
        await _wakeWord.SetMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);

        var wakeStarted = false;
        if (_isWakeWordEnabled && !_isMuted)
        {
            var wakeResult = await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
            wakeStarted = wakeResult.Succeeded;
            if (!wakeStarted)
            {
                _isWakeWordEnabled = false;
            }
        }

        _isInitialized = true;
        _reminderScheduler.Start();
        RuntimeTrace.Write(
            "inicio",
            $"stt={_recognitionProviderName} · wake={(wakeStarted ? "escuchando" : "apagado")} · " +
            $"muted={_isMuted} · nube={IsCloudConfigured}");
        var providerStatus = selection.Availability.IsAvailable
            ? $"{_recognitionProviderName} listo"
            : "entrada de voz no disponible";
        if (!string.IsNullOrWhiteSpace(_recognitionFallbackReason))
        {
            providerStatus += " · respaldo SAPI";
        }

        var wakeStatus = _isMuted
            ? "voz silenciada"
            : wakeStarted
                ? $"wake demo activo · decí “{_wakeWord.Phrases[0]}”"
                : "PTT disponible";
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            $"{providerStatus} · {wakeStatus}",
            IsCloudConfigured
                ? "Lista para ayudarte."
                : "Modo local seguro; OpenRouter permanece desconectado.",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    public Task<string> SendAsync(string text, CancellationToken cancellationToken) =>
        SendAsync(text, spoken: false, cancellationToken);

    /// <summary><paramref name="spoken"/> pide una respuesta para decir, no para leer.</summary>
    public async Task<string> SendAsync(string text, bool spoken, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (Interlocked.CompareExchange(ref _requestActive, 1, 0) != 0)
        {
            const string busy = "Estoy terminando la solicitud anterior.";
            Publish(new AssistantRuntimeUpdate(_lastVisualState, busy));
            return busy;
        }

        try
        {
            await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
            _speechCancellation?.Cancel();
            _neuralPlayer.Stop();
            await _speechSynthesizer.StopSpeakingAsync(cancellationToken).ConfigureAwait(false);
            return await ProcessRequestAsync(text, spoken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _requestActive, 0);
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<string> ProcessRequestAsync(string text, bool spoken, CancellationToken cancellationToken)
    {
        DismissPending(publish: false);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Thinking,
            IsCloudConfigured ? "Pensando con el perfil rápido…" : "Procesando localmente…",
            text,
            MicrophoneActive: IsAnyMicrophoneActive(),
            ClearConfirmation: true));

        var localOutcome = await _localCommands.TryExecuteAsync(text, cancellationToken).ConfigureAwait(false);
        if (localOutcome is not null)
        {
            return await FinishLocalCommandAsync(localOutcome, cancellationToken).ConfigureAwait(false);
        }

        if (IsCloudConfigured)
        {
            var authorizedToday = _budgetOverrideDay == DateOnly.FromDateTime(DateTime.Now);
            var guard = await _usageLedger.EvaluateAsync(
                new BudgetCheckRequest(ModelRole.Fast, ExplicitBudgetOverride: authorizedToday),
                cancellationToken).ConfigureAwait(false);
            if (!guard.CanProceed)
            {
                return OfferBudgetOverride(text, guard);
            }
        }

        var result = await _orchestrator.ProcessAsync(text, spoken, cancellationToken).ConfigureAwait(false);
        var pending = result.ToolResults.FirstOrDefault(tool =>
            tool.Status == ToolExecutionStatus.NeedsConfirmation);

        if (pending is not null)
        {
            var confirmation = new PendingConfirmation(
                pending.ToolCallId,
                GetConfirmationTitle(pending.ToolName),
                pending.Message);
            lock (_confirmationGate)
            {
                _pendingConfirmation = confirmation;
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Attention,
                "Esperando tu decisión · no se realizó la acción",
                result.Text,
                Confirmation: confirmation));
            await SpeakIfEnabledAsync(result.Text, cancellationToken).ConfigureAwait(false);
            return result.Text;
        }

        var state = result.State == AssistantState.Error
            ? AssistantVisualState.Error
            : AssistantVisualState.Idle;
        Publish(new AssistantRuntimeUpdate(
            state,
            result.IsLocalMode ? "Modo local · no se enviaron datos" : "Listo",
            result.Text,
            ClearConfirmation: true));

        if (state != AssistantVisualState.Error)
        {
            await SpeakIfEnabledAsync(result.Text, cancellationToken).ConfigureAwait(false);
        }

        return result.Text;
    }

    private async Task<string> FinishLocalCommandAsync(
        LocalCommandOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.ToolResult?.Status == ToolExecutionStatus.NeedsConfirmation)
        {
            var confirmation = new PendingConfirmation(
                outcome.ToolResult.ToolCallId,
                GetConfirmationTitle(outcome.ToolResult.ToolName),
                outcome.ToolResult.Message);
            lock (_confirmationGate)
            {
                _pendingConfirmation = confirmation;
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Attention,
                "Esperando tu decisión · no se realizó la acción",
                outcome.Text,
                Confirmation: confirmation));
        }
        else
        {
            var failed = outcome.ToolResult?.Status is ToolExecutionStatus.Failed or ToolExecutionStatus.Denied;
            Publish(new AssistantRuntimeUpdate(
                failed ? AssistantVisualState.Error : AssistantVisualState.Idle,
                failed ? "La política local no permitió la acción" : "Completado localmente",
                outcome.Text,
                ClearConfirmation: true,
                ClearSteps: true,
                Items: outcome.Items,
                ClearItems: outcome.Items is null));
        }

        await SpeakIfEnabledAsync(outcome.Text, cancellationToken).ConfigureAwait(false);
        return outcome.Text;
    }

    public async Task StartPushToTalkAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_isInitialized || _recognition is null)
        {
            return;
        }

        if (IsMuted)
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Idle,
                "La voz está silenciada",
                MicrophoneActive: false));
            return;
        }

        if (Volatile.Read(ref _requestActive) != 0 || Volatile.Read(ref _wakeHandoffActive) != 0)
        {
            Publish(new AssistantRuntimeUpdate(_lastVisualState, "Estoy terminando la interacción actual"));
            return;
        }

        await _voiceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
            _speechCancellation?.Cancel();
        _neuralPlayer.Stop();
        await _speechSynthesizer.StopSpeakingAsync(cancellationToken).ConfigureAwait(false);
            _orchestrator.SetListening(true);
            var result = await _recognition.StartPushToTalkAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _orchestrator.SetListening(false);
                Publish(new AssistantRuntimeUpdate(
                    result.ErrorCode == SpeechErrorCode.Cancelled
                        ? AssistantVisualState.Idle
                        : AssistantVisualState.Error,
                    "No pude abrir el micrófono",
                    SafeSpeechMessage(result.ErrorCode),
                    MicrophoneActive: false));
                await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Listening,
                $"Escuchando con {_recognitionProviderName}…",
                "Soltá el núcleo al terminar.",
                MicrophoneActive: true));
        }
        catch (OperationCanceledException)
        {
            _orchestrator.SetListening(false);
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _voiceTransitionGate.Release();
        }
    }

    public async Task StopPushToTalkAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_recognition is null)
        {
            return;
        }

        await _voiceTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SpeechRecognitionResult recognition;
        try
        {
            recognition = await _recognition.StopPushToTalkAsync(cancellationToken).ConfigureAwait(false);
            _orchestrator.SetListening(false);
        }
        finally
        {
            _voiceTransitionGate.Release();
        }

        if (!recognition.Succeeded)
        {
            var state = recognition.ErrorCode is SpeechErrorCode.Cancelled or SpeechErrorCode.MicrophoneMuted
                ? AssistantVisualState.Idle
                : AssistantVisualState.Error;
            Publish(new AssistantRuntimeUpdate(
                state,
                "Micrófono apagado",
                SafeSpeechMessage(recognition.ErrorCode),
                MicrophoneActive: false));
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(recognition.Text))
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Idle,
                "No detecté una frase · podés intentar otra vez",
                MicrophoneActive: false));
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var transcript = recognition.Text.Trim();
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Thinking,
            "Entendido · procesando…",
            transcript,
            MicrophoneActive: false));
        await SendAsync(transcript, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelSpeechAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_recognition is not null)
        {
            await _recognition.CancelPushToTalkAsync(cancellationToken).ConfigureAwait(false);
        }

        _speechCancellation?.Cancel();
        _neuralPlayer.Stop();
        await _speechSynthesizer.StopSpeakingAsync(cancellationToken).ConfigureAwait(false);
        _orchestrator.SetListening(false);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            IsMuted ? "Voz silenciada · micrófono apagado" : "Disponible",
            MicrophoneActive: false));
        await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _isWakeWordEnabled = enabled;

        if (_wakeWord is not null)
        {
            if (enabled && !IsMuted)
            {
                var result = await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    _isWakeWordEnabled = false;
                    Publish(new AssistantRuntimeUpdate(
                        AssistantVisualState.Error,
                        "La activación por voz no está disponible",
                        "Podés seguir usando PTT o texto.",
                        MicrophoneActive: false,
                        WakeWordEnabled: false));
                }
            }
            else
            {
                await _wakeWord.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await PersistVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            _isWakeWordEnabled
                ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? "Viernes"}”"
                : "Wake desactivado · PTT disponible",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    public async Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken)
    {
        _isShellVisible = visible;
        if (!visible)
        {
            _wakeHandoffCancellation?.Cancel();
            if (_recognition?.IsMicrophoneActive == true)
            {
                await _recognition.CancelPushToTalkAsync(cancellationToken).ConfigureAwait(false);
            }

            // Ocultar el orbe ya no apaga la escucha: para eso está mute, que sí libera el micrófono.
            if (_listenWhileHidden)
            {
                await ResumeWakeWordAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Idle,
                _listenWhileHidden && _isWakeWordEnabled && !IsMuted
                    ? $"Oculto y atento · decí “{_wakeWord?.Phrases[0] ?? "Viernes"}”"
                    : "Widget oculto · escucha detenida",
                MicrophoneActive: IsAnyMicrophoneActive(),
                WakeWordEnabled: _isWakeWordEnabled));
            return;
        }

        await ResumeWakeWordAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            _isWakeWordEnabled && !IsMuted
                ? $"Atento · decí “{_wakeWord?.Phrases[0] ?? "Viernes"}”"
                : "Disponible · PTT activo",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    public async Task SetListenWhileHiddenAsync(bool enabled, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _listenWhileHidden = enabled;
        await PersistVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (!_isShellVisible)
        {
            if (enabled)
            {
                await ResumeWakeWordAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            enabled
                ? "Voy a seguir atento aunque me oculte"
                : "Al ocultarme voy a dejar de escuchar",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    public async Task ConfirmPendingAsync(CancellationToken cancellationToken)
    {
        PendingConfirmation? confirmation;
        lock (_confirmationGate)
        {
            confirmation = _pendingConfirmation;
        }

        if (confirmation is null)
        {
            return;
        }

        await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryConfirmBudgetOverrideAsync(confirmation, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Thinking,
                "Verificando la acción confirmada…"));
            var result = await _orchestrator.ConfirmToolAsync(
                confirmation.ToolCallId,
                cancellationToken).ConfigureAwait(false);

            DismissPending(publish: false);
            var succeeded = result.Status == ToolExecutionStatus.Succeeded;
            Publish(new AssistantRuntimeUpdate(
                succeeded ? AssistantVisualState.Idle : AssistantVisualState.Attention,
                succeeded ? "Acción completada" : "Acción bloqueada por la política segura",
                result.Message,
                ClearConfirmation: true));
            await SpeakIfEnabledAsync(result.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void DismissPending() => DismissPending(publish: true);

    private void DismissPending(bool publish)
    {
        lock (_confirmationGate)
        {
            _pendingConfirmation = null;
            _budgetOverridePendingInput = null;
        }

        if (publish)
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Idle,
                "Acción cancelada · no se realizó ningún cambio",
                ClearConfirmation: true));
        }
    }

    private async Task SpeakIfEnabledAsync(string text, CancellationToken cancellationToken)
    {
        if (IsMuted || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var spokenText = text.Length <= MaximumSpokenCharacters
            ? text
            : text[..MaximumSpokenCharacters] + "…";
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Speaking,
            "Hablando · podés silenciarme cuando quieras"));

        _speechCancellation?.Dispose();
        _speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _speechCancellation.Token;

        RuntimeTrace.Write("voz.inicio", $"{spokenText.Length} caracteres · neural={_neuralVoice.IsAvailable}");
        var spoke = await TrySpeakNeuralAsync(spokenText, token).ConfigureAwait(false);
        var neuralFailure = spoke ? null : _neuralVoice.LastFailure;
        RuntimeTrace.Write("voz.neural", spoke ? "sonó" : $"falló · {neuralFailure ?? "sin motivo"}");

        if (!spoke && !token.IsCancellationRequested)
        {
            // La voz de Windows queda como red: peor timbre, pero siempre disponible y sin red.
            var result = await _speechSynthesizer.SpeakAsync(spokenText, token).ConfigureAwait(false);
            spoke = result.Succeeded;
            RuntimeTrace.Write("voz.sapi", spoke ? "sonó" : $"falló · {result.ErrorCode} · {result.ErrorMessage}");
            if (!spoke)
            {
                neuralFailure = result.ErrorMessage ?? neuralFailure;
            }
        }

        // Una voz que falla en silencio es indistinguible de una que decidió no hablar. Si algo se
        // rompió, tiene que decirlo: sin eso, diagnosticar es adivinar.
        Publish(new AssistantRuntimeUpdate(
            spoke || token.IsCancellationRequested ? AssistantVisualState.Idle : AssistantVisualState.Error,
            spoke || token.IsCancellationRequested
                ? _conversationActive ? "En conversación · decime «listo» para cortar" : "Disponible"
                : $"Sin voz: {neuralFailure ?? "no se pudo reproducir el audio"}"));
    }

    /// <summary>
    /// Deja de hablar de inmediato. Lo llama el bucle de conversación al detectar que el usuario
    /// arrancó a hablar: poder interrumpirla es lo que separa una conversación de un locutor.
    /// </summary>
    public void BargeIn()
    {
        if (_isDisposed)
        {
            return;
        }

        RuntimeTrace.Write("voz.interrumpida");
        _speechCancellation?.Cancel();
        _neuralPlayer.Stop();
        _ = _speechSynthesizer.StopSpeakingAsync(CancellationToken.None);
    }

    /// <summary>
    /// Habla por oraciones: sintetiza la siguiente mientras suena la actual, así el primer sonido
    /// llega en cuanto está lista la primera frase en vez de esperar la respuesta entera.
    /// </summary>
    private async Task<bool> TrySpeakNeuralAsync(string text, CancellationToken cancellationToken)
    {
        if (!_neuralVoice.IsAvailable)
        {
            return false;
        }

        var chunks = SplitIntoSpokenChunks(text);
        if (chunks.Count == 0)
        {
            return false;
        }

        try
        {
            var pending = _neuralVoice.SynthesizeAsync(chunks[0], cancellationToken);
            for (var index = 0; index < chunks.Count; index++)
            {
                var audio = await pending.ConfigureAwait(false);
                if (audio is null)
                {
                    // El primer tramo define si hay voz neural; a mitad de camino se corta y listo.
                    return index > 0;
                }

                pending = index + 1 < chunks.Count
                    ? _neuralVoice.SynthesizeAsync(chunks[index + 1], cancellationToken)
                    : Task.FromResult<SynthesizedSpeech?>(null);

                if (!await _neuralPlayer
                        .PlayAsync(audio.Pcm, audio.SampleRate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return index > 0;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Corta en oraciones y agrupa las cortas: un tramo de tres palabras gasta una ida y vuelta
    /// entera para casi nada de audio.
    /// </summary>
    internal static IReadOnlyList<string> SplitIntoSpokenChunks(string text)
    {
        const int maximum = 260;

        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var sentence in SplitSentences(text))
        {
            // El primer tramo se corta apenas hay una frase: el silencio que se siente es el que va
            // desde que dejás de hablar hasta el primer sonido, no el que hay entre frases.
            var minimum = chunks.Count == 0 ? 1 : 70;

            if (current.Length > 0 && current.Length + sentence.Length > maximum)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            current.Append(sentence);
            if (current.Length >= minimum)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(chunk => chunk.Length > 0).ToArray();
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '!' or '?' or '…' or '\n'))
            {
                continue;
            }

            // Se corta después del signo y de los espacios que lo siguen, no antes.
            var end = index + 1;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            yield return text[start..end];
            start = end;
            index = end - 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private async Task ApplyMuteAsync(bool isMuted)
    {
        try
        {
            if (_recognition is not null)
            {
                await _recognition.SetMicrophoneMutedAsync(isMuted).ConfigureAwait(false);
            }

            await _speechSynthesizer.SetMicrophoneMutedAsync(isMuted).ConfigureAwait(false);
            if (_wakeWord is not null)
            {
                await _wakeWord.SetMutedAsync(isMuted).ConfigureAwait(false);
            }

            if (isMuted)
            {
                // Mute sigue siendo el corte duro: también cierra la conversación abierta.
                await EndConversationAsync("Voz silenciada · conversación cerrada", CancellationToken.None)
                    .ConfigureAwait(false);
                _wakeHandoffCancellation?.Cancel();
                await _speechSynthesizer.StopSpeakingAsync(CancellationToken.None).ConfigureAwait(false);
                _orchestrator.SetListening(false);
            }
            else
            {
                await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (_isInitialized)
            {
                await PersistVoiceSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Idle,
                isMuted
                    ? "Voz silenciada · micrófono apagado"
                    : _isWakeWordEnabled
                        ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? "Viernes"}”"
                        : "Voz activa · PTT disponible",
                MicrophoneActive: IsAnyMicrophoneActive(),
                WakeWordEnabled: _isWakeWordEnabled));
        }
        catch (ObjectDisposedException)
        {
            // El cierre ganó la carrera y los dispositivos ya fueron liberados.
        }
    }

    /// <summary>
    /// El guard cortó antes de llamar al modelo. Se ofrece autorizar, pero nunca se autoriza solo:
    /// hace falta el mismo gesto explícito que para una acción de PC.
    /// </summary>
    private string OfferBudgetOverride(string input, BudgetGuardResult guard)
    {
        var reasons = string.Join(" ", guard.Reasons);
        var detail = string.IsNullOrWhiteSpace(reasons)
            ? "Alcanzaste un límite local de uso."
            : reasons;

        var confirmation = new PendingConfirmation(
            BudgetOverrideCallId,
            "Seguir gastando por hoy",
            detail);
        lock (_confirmationGate)
        {
            _pendingConfirmation = confirmation;
            _budgetOverridePendingInput = input;
        }

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Attention,
            "Límite local alcanzado · no se llamó al modelo",
            $"{detail} Los comandos locales siguen funcionando.",
            Confirmation: confirmation,
            ClearSteps: true));
        return detail;
    }

    private async Task<bool> TryConfirmBudgetOverrideAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation.ToolCallId, BudgetOverrideCallId, StringComparison.Ordinal))
        {
            return false;
        }

        string? input;
        lock (_confirmationGate)
        {
            input = _budgetOverridePendingInput;
            _budgetOverridePendingInput = null;
            _pendingConfirmation = null;
        }

        // Sólo por hoy y sólo en memoria: mañana, o tras reiniciar, vuelve a preguntar.
        _budgetOverrideDay = DateOnly.FromDateTime(DateTime.Now);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Thinking,
            "Gasto autorizado sólo por hoy · se olvida al reiniciar",
            ClearConfirmation: true));

        if (!string.IsNullOrWhiteSpace(input))
        {
            await ProcessRequestAsync(input, _conversationActive, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Abre una conversación: a partir de acá el micrófono vuelve solo después de cada respuesta.
    /// La llama el wake word y también el toque en el orbe.
    /// </summary>
    public Task StartConversationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_conversationActive || IsMuted || _recognition is null)
        {
            RuntimeTrace.Write(
                "conversacion.rechazada",
                $"activa={_conversationActive} muted={IsMuted} reconocedor={_recognition is not null}");
            return Task.CompletedTask;
        }

        RuntimeTrace.Write("conversacion.abierta");

        _conversationActive = true;
        _conversationCancellation?.Dispose();
        _conversationCancellation = new CancellationTokenSource();

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Listening,
            "En conversación · decime «listo» para cortar",
            "Te escucho.",
            MicrophoneActive: true));

        _ = RunConversationLoopAsync(_conversationCancellation.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Acuse de recibo hablado. Que conteste apenas lo llamás es lo que convierte «se activó» en
    /// «me escuchó»: sin voz, una animación no distingue estar atento de estar colgado.
    /// </summary>
    private async Task GreetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SpeakIfEnabledAsync("Te escucho.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Si la voz falla, la conversación sigue: el saludo no es la conversación.
        }
    }

    public async Task EndConversationAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_conversationActive)
        {
            return;
        }

        RuntimeTrace.Write("conversacion.cerrada", reason);
        _conversationActive = false;
        _conversationCancellation?.Cancel();

        if (_recognition?.IsMicrophoneActive == true)
        {
            await _recognition.CancelPushToTalkAsync(cancellationToken).ConfigureAwait(false);
        }

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            reason,
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));

        await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Escucha, responde y vuelve a escuchar. No se corta por silencio: sólo por una frase de
    /// cierre, por mute o porque el dispositivo falle. Ese es el punto de tener conversación.
    /// </summary>
    private async Task RunConversationLoopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _conversationLoopRunning, 1, 0) != 0)
        {
            return;
        }

        var consecutiveDeviceFailures = 0;
        var consecutiveSilences = 0;

        try
        {
            await PauseWakeWordAsync(cancellationToken).ConfigureAwait(false);

            // SAPI avisa que soltó el micrófono antes de que el driver lo libere de verdad.
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            await GreetAsync(cancellationToken).ConfigureAwait(false);

            while (_conversationActive && !cancellationToken.IsCancellationRequested && !IsMuted)
            {
                if (_recognition is null)
                {
                    break;
                }

                Publish(new AssistantRuntimeUpdate(
                    AssistantVisualState.Listening,
                    "Te escucho · decime «listo» para cortar",
                    MicrophoneActive: true));

                RuntimeTrace.Write("captura.inicio");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // Ritmo de charla, no de dictado: corta 600 ms después de que dejás de hablar y
                // no se queda veinte segundos mirándote si no decís nada. La duración máxima
                // siempre tiene que superar a la ventana inicial o la validación tira.
                var capture = await _recognition
                    .RecognizeSingleUtteranceAsync(
                        new SingleUtteranceRecognitionOptions
                        {
                            InitialSilenceTimeout = TimeSpan.FromSeconds(6),
                            EndSilenceTimeout = TimeSpan.FromMilliseconds(600),
                            MaximumDuration = TimeSpan.FromSeconds(20)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                clock.Stop();
                RuntimeTrace.Write(
                    "captura.fin",
                    $"{clock.ElapsedMilliseconds} ms · ok={capture.Succeeded} · código={capture.ErrorCode} · " +
                    $"texto={(string.IsNullOrWhiteSpace(capture.Text) ? "(vacío)" : $"«{capture.Text}»")}");

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!capture.Succeeded)
                {
                    // El dispositivo puede tardar en quedar libre: se reintenta antes de rendirse,
                    // y sólo se corta si falla dos veces seguidas. Seguir sería fingir que escucha.
                    if (capture.ErrorCode is SpeechErrorCode.DeviceError or SpeechErrorCode.Unavailable)
                    {
                        if (++consecutiveDeviceFailures >= 3)
                        {
                            await EndConversationAsync(
                                $"Corté la conversación: {SafeSpeechMessage(capture.ErrorCode)}",
                                CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    continue;
                }

                consecutiveDeviceFailures = 0;
                var transcript = capture.Text?.Trim();
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    // El silencio no cierra nada: sigue esperando, que es lo que se le pidió. Pero
                    // tampoco se queda mudo: a los tres silencios seguidos pregunta, como haría
                    // cualquiera al que dejaste hablando solo.
                    if (++consecutiveSilences == 3)
                    {
                        await SpeakIfEnabledAsync("¿Seguís ahí?", cancellationToken).ConfigureAwait(false);
                    }

                    Publish(new AssistantRuntimeUpdate(
                        AssistantVisualState.Listening,
                        "Sigo escuchando · decime «listo» para cortar",
                        MicrophoneActive: true));
                    continue;
                }

                consecutiveSilences = 0;

                if (IsClosingPhrase(transcript))
                {
                    await SpeakIfEnabledAsync("Listo. Llamame cuando quieras.", CancellationToken.None)
                        .ConfigureAwait(false);
                    await EndConversationAsync("Conversación cerrada", CancellationToken.None).ConfigureAwait(false);
                    await LearnFromConversationAsync().ConfigureAwait(false);
                    return;
                }

                _conversationTurns.Add(transcript);

                await SendAsync(transcript, spoken: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cerrar la conversación cancela el bucle; no es un error.
        }
        catch (Exception exception)
        {
            // Tragarse el motivo convertía un error de configuración en «se apaga sola».
            RuntimeTrace.Write("conversacion.excepcion", $"{exception.GetType().Name} · {exception.Message}");
            await EndConversationAsync("La conversación se interrumpió", CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _conversationLoopRunning, 0);
            if (!_conversationActive)
            {
                await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sólo cierra si la frase es corta y es esencialmente la despedida: «gracias por todo esto que
    /// hiciste» no debería cortar una conversación.
    /// </summary>
    internal static bool IsClosingPhrase(string text)
    {
        var normalized = new string(text
            .ToLowerInvariant()
            .Where(character => !char.IsPunctuation(character))
            .ToArray())
            .Replace("viernes", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (normalized.Length == 0 || normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
        {
            return false;
        }

        return ClosingPhrases.Any(phrase =>
            normalized.Equals(phrase, StringComparison.Ordinal) ||
            normalized.StartsWith(phrase + " ", StringComparison.Ordinal) ||
            normalized.EndsWith(" " + phrase, StringComparison.Ordinal));
    }

    /// <summary>
    /// Al cerrar una charla, destila a lo sumo dos hechos duraderos sobre el usuario y los guarda
    /// como <em>observaciones temporales</em>: vencen solas y nunca se vuelven permanentes sin que
    /// las apruebes. Es lo que hace que Viernes aprenda con vos sin aprender a tus espaldas.
    /// </summary>
    /// <remarks>
    /// No guarda la conversación: guarda hechos cortos y revisables. La política de contenido del
    /// store rechaza credenciales y cualquier cosa con forma de transcripción.
    /// </remarks>
    private async Task LearnFromConversationAsync()
    {
        List<string> turns;
        lock (_confirmationGate)
        {
            turns = [.. _conversationTurns];
            _conversationTurns.Clear();
        }

        if (turns.Count < 2 || !IsCloudConfigured)
        {
            RuntimeTrace.Write("memoria.omitida", $"turnos={turns.Count} nube={IsCloudConfigured}");
            return;
        }

        try
        {
            var prompt =
                "De lo que dijo el usuario, extraé como máximo DOS hechos duraderos sobre él: " +
                "preferencias, rutinas, nombres de personas cercanas, cómo le gusta trabajar. " +
                "Uno por línea, en tercera persona, menos de 90 caracteres cada uno, sin comillas. " +
                "Ignorá pedidos puntuales, fechas de eventos y cualquier cosa efímera. " +
                "Si no hay nada duradero, respondé exactamente: NADA.\n\n" +
                string.Join("\n", turns.TakeLast(12));

            var distilled = await _orchestrator.ProcessAsync(prompt, CancellationToken.None).ConfigureAwait(false);
            if (distilled.IsLocalMode || string.IsNullOrWhiteSpace(distilled.Text))
            {
                return;
            }

            foreach (var line in distilled.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(2))
            {
                var fact = line.TrimStart('-', '*', '•', ' ').Trim();
                if (fact.Length is < 8 or > 90 || fact.Contains("NADA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // El store rechaza credenciales y contenido con forma de conversación, y la
                // observación vence sola: nunca se vuelve permanente sin que la apruebes.
                var captured = await _memory
                    .ObserveAsync(fact, confidence: 0.6, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                RuntimeTrace.Write("memoria.observada", $"{captured.Status} · {fact}");
            }
        }
        catch (Exception exception)
        {
            RuntimeTrace.Write("memoria.falló", exception.GetType().Name);
        }
    }

    private void ReminderSchedulerOnReminderDue(object? sender, ReminderDueEventArgs eventArgs) =>
        _ = AnnounceReminderAsync(eventArgs);

    private async Task AnnounceReminderAsync(ReminderDueEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        var title = eventArgs.Reminder.Title;
        var when = eventArgs.Reminder.DueAt.ToLocalTime().ToString("HH:mm", ArgentineCulture);
        var detail = eventArgs.IsLate
            ? $"Era para las {when}: {title}"
            : $"Son las {when}: {title}";

        // Un recordatorio interrumpe la presencia mínima, pero no ejecuta ninguna acción por su cuenta.
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Attention,
            eventArgs.IsLate ? "Recordatorio atrasado" : "Recordatorio",
            detail,
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));

        RequestActivation(new ShellActivationRequest(
            ShellActivationReason.Reminder,
            "Recordatorio de Viernes",
            detail));

        try
        {
            await SpeakIfEnabledAsync(detail, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // La voz es un complemento del aviso visual; su falla no debe perder el recordatorio.
        }
    }

    private void RequestActivation(ShellActivationRequest request)
    {
        var handlers = ActivationRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ShellActivationRequest> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, request);
            }
            catch (Exception)
            {
                // Un shell que no puede mostrarse no debe romper el flujo de voz ni de recordatorios.
            }
        }
    }

    private async Task HandleWakeWordDetectedAsync(WakeWordDetectedEventArgs eventArgs)
    {
        // Con una conversación abierta, decir el nombre es parte de la charla, no una activación.
        if (_conversationActive)
        {
            RuntimeTrace.Write("wake.ignorado", "ya hay conversación abierta");
            return;
        }

        if (Interlocked.CompareExchange(ref _wakeHandoffActive, 1, 0) != 0 ||
            _isDisposed || IsMuted || !_isWakeWordEnabled || _recognition is null || _wakeWord is null)
        {
            return;
        }

        RuntimeTrace.Write("wake.detected", $"frase «{eventArgs.Phrase}» confianza {eventArgs.Confidence:0.00}");

        try
        {
            _wakeHandoffCancellation?.Dispose();
            _wakeHandoffCancellation = new CancellationTokenSource();
            _orchestrator.SetListening(true);

            // Llamarlo por su nombre alcanza para que aparezca, aunque estuviera oculto en la bandeja.
            RequestActivation(new ShellActivationRequest(
                ShellActivationReason.WakeWord,
                "Viernes",
                $"Te escuché decir “{eventArgs.Phrase}”."));

            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Listening,
                $"Activada por “{eventArgs.Phrase}” · escuchando…",
                "¿Qué necesitás?",
                MicrophoneActive: true,
                WakeWordEnabled: true));

            // Llamarlo por su nombre abre la conversación directamente. Antes se hacía una captura
            // suelta primero y sólo se abría la conversación si ésa salía bien: una captura fallida
            // dejaba todo cerrado, sin respuesta y sin voz. El bucle es quien captura, y reintenta.
            await StartConversationAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Error,
                "La activación por voz tuvo un problema",
                "Podés seguir usando el núcleo o el campo de texto.",
                MicrophoneActive: IsAnyMicrophoneActive()));
        }
        finally
        {
            _wakeHandoffCancellation?.Dispose();
            _wakeHandoffCancellation = null;
            Interlocked.Exchange(ref _wakeHandoffActive, 0);

            // Con la conversación abierta el micrófono queda en manos del bucle y el wake descansa.
            if (!_conversationActive)
            {
                _orchestrator.SetListening(false);
                await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Detiene el wake sin condicionarlo a su estado. Antes sólo paraba si estaba en
    /// <c>Listening</c>, y justo después de detectar una frase no lo está: quedaba corriendo,
    /// se re-armaba solo y le ganaba el micrófono a la captura. Detener dos veces no cuesta nada;
    /// no detener cuesta la conversación entera.
    /// </summary>
    private async Task PauseWakeWordAsync(CancellationToken cancellationToken)
    {
        if (_wakeWord is null)
        {
            return;
        }

        var result = await _wakeWord.StopAsync(cancellationToken).ConfigureAwait(false);
        RuntimeTrace.Write(
            "wake.pausado",
            $"ok={result.Succeeded} · estado={_wakeWord.State} · micrófono={_wakeWord.IsMicrophoneActive}");
    }

    private async Task ResumeWakeWordAsync(CancellationToken cancellationToken)
    {
        // Durante una conversación el micrófono es del bucle. Reactivar el wake acá se lo robaba:
        // pasaba al abrir la conversación y otra vez después de cada turno, así que la conversación
        // no llegaba a capturar nunca. Es el corte en la raíz, porque los llamadores son varios.
        if (_conversationActive)
        {
            return;
        }

        if (_isDisposed || !_isInitialized || IsMuted || !_isWakeWordEnabled || _wakeWord is null ||
            (!_isShellVisible && !_listenWhileHidden) ||
            Volatile.Read(ref _requestActive) != 0 || _recognition?.IsMicrophoneActive == true)
        {
            return;
        }

        if (_wakeWord.State != WakeWordServiceState.Listening)
        {
            var result = await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
            RuntimeTrace.Write("wake.reanudado", $"ok={result.Succeeded}");
        }
    }

    private async Task PersistVoiceSettingsAsync(CancellationToken cancellationToken)
    {
        _settings = _settings with
        {
            MicrophoneMuted = _isMuted,
            VoiceActivation = _isWakeWordEnabled
                ? VoiceActivationMode.LocalWakeWord
                : VoiceActivationMode.PushToTalk,
            WakeWordPhrases = _wakeWord?.Phrases.ToArray() ?? _settings.WakeWordPhrases,
            ListenWhileHidden = _listenWhileHidden,
            OrbShape = OrbShape.ToString(),
            PreferredRecognitionProvider = _recognition?.Info.Kind ?? _settings.PreferredRecognitionProvider
        };
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
    }

    private SpeechRecognitionProviderSelection CreateRecognitionSelection(ViernesLocalSettings settings)
    {
        var preferWhisper = !string.Equals(
            Environment.GetEnvironmentVariable("VIERNES_STT_PROVIDER"),
            "sapi",
            StringComparison.OrdinalIgnoreCase) &&
            settings.PreferredRecognitionProvider != SpeechRecognitionProviderKind.WindowsSapi;
        var configuredModelPath = Environment.GetEnvironmentVariable("VIERNES_WHISPER_MODEL_PATH");
        var whisperOptions = new WhisperSpeechRecognitionOptions
        {
            ModelPath = !string.IsNullOrWhiteSpace(configuredModelPath)
                ? configuredModelPath.Trim()
                : settings.WhisperModelPath ?? WhisperSpeechRecognitionOptions.GetDefaultModelPath(),
            Language = "es"
        };
        return new SpeechRecognitionProviderSelector().Select(new SpeechRecognitionSelectionOptions
        {
            PreferWhisperLocal = preferWhisper,
            Whisper = whisperOptions,
            Sapi = new SpeechServiceOptions
            {
                RecognitionCulture = settings.RecognitionCulture,
                SynthesisCulture = settings.RecognitionCulture,
                EmitPartialTranscriptions = true
            }
        });
    }

    private static bool ResolveWakeEnabled(VoiceActivationMode configuredMode)
    {
        var environmentValue = Environment.GetEnvironmentVariable("VIERNES_WAKE_ENABLED")?.Trim();
        if (environmentValue is not null)
        {
            if (environmentValue.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                environmentValue.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                environmentValue.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (environmentValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                environmentValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                environmentValue.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return configuredMode == VoiceActivationMode.LocalWakeWord;
    }

    /// <summary>
    /// Windows no tiene reconocedor rioplatense: el wake corre sobre el de español de España, y con
    /// ese acento la confianza que devuelve SAPI queda sistemáticamente por debajo de 0,78. Bajarlo
    /// es lo que hace que reconozca; subirlo, lo que corta falsos positivos. Se ajusta sin recompilar
    /// porque el punto justo depende del micrófono y de la voz.
    /// </summary>
    private static float ResolveWakeConfidence()
    {
        const float fallback = 0.55f;
        var configured = Environment.GetEnvironmentVariable("VIERNES_WAKE_CONFIDENCE");
        return float.TryParse(
            configured,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) && parsed is > 0 and <= 1
            ? parsed
            : fallback;
    }

    private static bool ResolveListenWhileHidden(bool configuredValue) =>
        Environment.GetEnvironmentVariable("VIERNES_LISTEN_WHILE_HIDDEN")?.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" => false,
            "1" or "true" or "on" => true,
            _ => configuredValue
        };

    private static IReadOnlyList<string> ResolveWakePhrases(IReadOnlyList<string> configuredPhrases)
    {
        var environmentValue = Environment.GetEnvironmentVariable("VIERNES_WAKE_PHRASES");
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            return configuredPhrases;
        }

        var phrases = environmentValue
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(phrase => phrase.Length is >= 2 and <= 40 && !phrase.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return phrases.Length > 0 ? phrases : configuredPhrases;
    }

    /// <summary>
    /// Cortar la voz apenas el usuario empieza a hablar. Sin esto hay que esperar a que termine la
    /// frase para poder decir algo, que es exactamente lo que no hace una persona.
    /// </summary>
    private void RecognitionOnSpeechStarted(object? sender, SpeechTranscriptionEventArgs e)
    {
        if (_conversationActive && !string.IsNullOrWhiteSpace(e.Text))
        {
            BargeIn();
        }
    }

    /// <summary>
    /// Reenvía el nivel del micrófono a la interfaz sin pasar por el estado: llega decenas de veces
    /// por segundo y tiene que mover la forma, no reescribir la burbuja.
    /// </summary>
    private void RecognitionOnAudioLevel(object? sender, AudioLevelEventArgs e) =>
        Updated?.Invoke(this, new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            AudioLevel: e.Level));

    private void SubscribeRecognition(ISpeechRecognitionProvider recognition)
    {
        recognition.AudioLevelChanged += RecognitionOnAudioLevel;
        recognition.TranscriptionUpdated += RecognitionOnSpeechStarted;
        recognition.MicrophoneActivityChanged += RecognitionOnMicrophoneActivityChanged;
        recognition.TranscriptionUpdated += RecognitionOnTranscriptionUpdated;
        recognition.ServiceError += RecognitionOnError;
    }

    private void SubscribeWakeWord(IWakeWordService wakeWord)
    {
        wakeWord.MicrophoneActivityChanged += WakeOnMicrophoneActivityChanged;
        wakeWord.WakeWordDetected += WakeOnWakeWordDetected;
        wakeWord.ServiceError += WakeOnError;
    }

    private void RecognitionOnMicrophoneActivityChanged(object? sender, MicrophoneActivityChangedEventArgs e)
    {
        var state = e.IsActive
            ? AssistantVisualState.Listening
            : _lastVisualState == AssistantVisualState.Listening
                ? AssistantVisualState.Idle
                : _lastVisualState;
        Publish(new AssistantRuntimeUpdate(
            state,
            e.IsActive ? $"Escuchando con {_recognitionProviderName}…" : "Micrófono de dictado apagado",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    private void RecognitionOnTranscriptionUpdated(object? sender, SpeechTranscriptionEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Text))
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Listening,
                e.IsFinal ? "Frase capturada" : "Escuchando…",
                e.Text,
                MicrophoneActive: IsAnyMicrophoneActive()));
        }
    }

    private void RecognitionOnError(object? sender, SpeechServiceErrorEventArgs e) =>
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Error,
            "La voz local informó un problema",
            SafeSpeechMessage(e.ErrorCode),
            MicrophoneActive: IsAnyMicrophoneActive()));

    private void WakeOnMicrophoneActivityChanged(object? sender, MicrophoneActivityChangedEventArgs e)
    {
        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            e.IsActive && _isWakeWordEnabled
                ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? "Viernes"}”"
                : _lastVisualState == AssistantVisualState.Idle
                    ? "Micrófono de activación apagado"
                    : CurrentStateLabel(_lastVisualState),
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    private void WakeOnWakeWordDetected(object? sender, WakeWordDetectedEventArgs e) =>
        _ = HandleWakeWordDetectedAsync(e);

    private void WakeOnError(object? sender, SpeechServiceErrorEventArgs e) =>
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            "Wake demo no disponible · PTT sigue activo",
            SafeSpeechMessage(e.ErrorCode),
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: false));

    private void OrchestratorOnProgressChanged(object? sender, TurnProgressEventArgs e) =>
        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            Steps: e.Steps));

    private void OrchestratorOnStateChanged(object? sender, AssistantStateChangedEventArgs e)
    {
        if (e.CurrentState is AssistantState.Thinking or AssistantState.Error)
        {
            Publish(new AssistantRuntimeUpdate(
                e.CurrentState == AssistantState.Thinking
                    ? AssistantVisualState.Thinking
                    : AssistantVisualState.Error,
                e.CurrentState == AssistantState.Thinking ? "Pensando…" : "No pude completar eso"));
        }
    }

    private void Publish(AssistantRuntimeUpdate update)
    {
        _lastVisualState = update.State;
        Updated?.Invoke(this, update);
    }

    private bool IsAnyMicrophoneActive() =>
        (_recognition?.IsMicrophoneActive ?? false) || (_wakeWord?.IsMicrophoneActive ?? false);

    private static string CurrentStateLabel(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Listening => "Escuchando…",
        AssistantVisualState.Thinking => "Pensando…",
        AssistantVisualState.Speaking => "Hablando…",
        AssistantVisualState.Attention => "Esperando confirmación",
        AssistantVisualState.Error => "Atención necesaria",
        _ => "Disponible"
    };

    private static string GetConfirmationTitle(string toolName) => toolName switch
    {
        "pc_action" => "Confirmar acción de PC",
        "agenda_create" => "Confirmar cambio de agenda",
        "reminder_create" => "Confirmar recordatorio",
        _ => "Confirmación necesaria"
    };

    private static string SafeSpeechMessage(SpeechErrorCode errorCode) => errorCode switch
    {
        SpeechErrorCode.MicrophoneMuted => "El micrófono está silenciado.",
        SpeechErrorCode.Unavailable => "Instalá el modelo Whisper o un paquete de voz de Windows; también podés escribir.",
        SpeechErrorCode.TimedOut => "No detecté audio a tiempo.",
        SpeechErrorCode.DeviceError => "Windows no pudo acceder al micrófono.",
        SpeechErrorCode.Cancelled => "La escucha fue cancelada.",
        _ => "La voz local no está disponible en este momento."
    };

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _conversationActive = false;
        _conversationCancellation?.Cancel();
        _conversationCancellation?.Dispose();
        _wakeHandoffCancellation?.Cancel();
        _wakeHandoffCancellation?.Dispose();
        _orchestrator.StateChanged -= OrchestratorOnStateChanged;
        _orchestrator.ProgressChanged -= OrchestratorOnProgressChanged;
        _reminderScheduler.ReminderDue -= ReminderSchedulerOnReminderDue;
        await _reminderScheduler.DisposeAsync().ConfigureAwait(false);

        if (_wakeWord is not null)
        {
            _wakeWord.MicrophoneActivityChanged -= WakeOnMicrophoneActivityChanged;
            _wakeWord.WakeWordDetected -= WakeOnWakeWordDetected;
            _wakeWord.ServiceError -= WakeOnError;
            await _wakeWord.DisposeAsync().ConfigureAwait(false);
        }

        if (_recognition is not null)
        {
            _recognition.AudioLevelChanged -= RecognitionOnAudioLevel;
            _recognition.TranscriptionUpdated -= RecognitionOnSpeechStarted;
            _recognition.MicrophoneActivityChanged -= RecognitionOnMicrophoneActivityChanged;
            _recognition.TranscriptionUpdated -= RecognitionOnTranscriptionUpdated;
            _recognition.ServiceError -= RecognitionOnError;
            await _recognition.DisposeAsync().ConfigureAwait(false);
        }

        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        await _neuralPlayer.DisposeAsync().ConfigureAwait(false);
        await _speechSynthesizer.DisposeAsync().ConfigureAwait(false);
        _voiceTransitionGate.Dispose();
        _httpClient.Dispose();
    }
}
