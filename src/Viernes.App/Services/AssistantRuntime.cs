using System.Net.Http;
using Viernes.App.Controls;
using Viernes.App.Diagnostics;
using Viernes.App.ViewModels;
using Viernes.Core;
using Viernes.Core.Awareness;
using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Mcp;
using Viernes.Platform.Windows.Awareness;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Scheduling;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Viernes.Core.Usage;
using Viernes.Core.Voice;
using Viernes.Memory.Models;
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

    /// <summary>
    /// No es <c>readonly</c> porque al iniciar se rehace con el nombre que eligió el usuario, que
    /// vive en las preferencias y todavía no está leído cuando corre el constructor.
    /// </summary>
    private ViernesOptions _options;

    /// <summary>Cómo se llama el asistente en esta instalación.</summary>
    private AssistantIdentity _identity = AssistantIdentity.Default;

    /// <summary>Herramientas MCP ya conectadas, a la espera de que se arme el orquestador final.</summary>
    private IReadOnlyList<IAssistantTool>? _mcpTools;

    private readonly HttpClient _httpClient;
    /// <summary>
    /// No es <c>readonly</c> porque se reconstruye una sola vez, al iniciar, si hay servidores MCP.
    /// </summary>
    /// <remarks>
    /// El ejecutor de herramientas es inmutable a propósito —es el único punto donde se decide si
    /// algo se ejecuta, y hacerlo mutable sería abrir la puerta a que se le agreguen capacidades en
    /// caliente—. Como conectar servidores MCP es asincrónico y lento, y el constructor no puede
    /// esperar, la única forma honesta de sumarlas es rehacer el orquestador entero en el arranque,
    /// antes de que exista una conversación. Después de eso vuelve a ser fijo.
    /// </remarks>
    private ConversationOrchestrator _orchestrator;
    private readonly UsageLedger _usageLedger;
    private LocalCommandRouter _localCommands;
    private readonly ISpeechService _speechSynthesizer;
    private readonly OpenRouterSpeechClient _neuralVoice;
    private readonly NeuralSpeechPlayer _neuralPlayer = new();
    private CancellationTokenSource? _speechCancellation;

    /// <summary>
    /// Conversación abierta: mientras dure, el micrófono vuelve a abrirse solo después de cada
    /// respuesta y no hace falta repetir el nombre. Se cierra únicamente cuando el usuario lo dice.
    /// </summary>
    private bool _conversationActive;
    private System.Diagnostics.Stopwatch? _captureClock;
    private int _voiceTraced;
    private int _microphoneTraced;
    private CancellationTokenSource? _conversationCancellation;

    /// <summary>Lo que el freno corta: el turno en curso, venga de la voz o del teclado.</summary>
    private CancellationTokenSource? _turnCancellation;

    /// <summary>Distinto de cero cuando el usuario pidió parar y el turno todavía no terminó.</summary>
    private int _restRequested;

    /// <summary>
    /// Distinto de cero mientras destila lo aprendido de la charla que acaba de cerrar.
    /// </summary>
    /// <remarks>
    /// La destilación corre un turno entero contra el modelo, sobre el mismo orquestador cuyos
    /// eventos alimentan la interfaz. Sin esta bandera, después de despedirse el orbe volvía a
    /// «Pensando…» y mostraba como pasos el prompt interno de la destilación —y ahí se quedaba,
    /// porque el puente hacia la interfaz sólo reenvía Thinking y Error, nunca Idle: puede subir el
    /// orbe pero no bajarlo. En un turno normal eso está bien porque el reposo lo publica el cierre
    /// del pedido; la destilación no pasa por ahí.
    /// <para>
    /// Es trabajo interno y no tiene por qué verse. Que se vea es peor que que no se vea: el usuario
    /// pidió que pare y lo que ve es que arranca a pensar.
    /// </para>
    /// </remarks>
    private int _distilling;

    private int _conversationLoopRunning;

    /// <summary>Lo que dijo el usuario en la charla, para destilar al cerrarla. No se persiste.</summary>
    private readonly List<string> _conversationTurns = [];

    // Las listas de frases de cierre viven en Viernes.Core.Conversation.ClosingPhrase: es lógica de
    // texto pura, y acá adentro no había forma de probarla —el proyecto de pruebas no puede
    // referenciar la aplicación—, así que su test terminaba reimplementando la regla y midiendo su
    // propia expectativa.

    private readonly JsonUserDataStore _dataStore = new();
    private readonly JsonPersonalMemoryStore _memory = new();
    private readonly ReminderScheduler _reminderScheduler;
    private readonly LocalSettingsStore _settingsStore = new();
    private readonly WakeWordRecognitionCoordinator _wakeCoordinator = new();
    private readonly SemaphoreSlim _voiceTransitionGate = new(1, 1);
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly object _confirmationGate = new();

    private readonly WindowsPcActionExecutor _pcActions;

    /// <summary>
    /// Vive tanto como el runtime a propósito: su valor está en el historial que acumula, y uno
    /// nuevo por turno no sabría por dónde anduviste.
    /// </summary>
    private readonly WindowsEnvironmentObserver _environment = new();
    private DesktopSignals? _signals;
    private McpToolProvider? _mcpProvider;
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
        _pcActions = new WindowsPcActionExecutor();
        _orchestrator = BuildOrchestrator(extraTools: null);
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

    private ConversationOrchestrator BuildOrchestrator(IReadOnlyList<IAssistantTool>? extraTools) =>
        ViernesCoreFactory.CreateDefault(
            _httpClient,
            _options,
            _dataStore,
            _usageLedger,
            _pcActions,
            actionMemory: null,
            extraTools,
            _environment,
            personalContext: DescribePersonalMemoryAsync,
            rest: RestAsync);

    /// <summary>
    /// Aparta el micrófono cuando el modelo entendió que se lo están pidiendo.
    /// </summary>
    /// <remarks>
    /// Es la contraparte de <see cref="RestTool"/>: el núcleo entiende la intención y esta capa la
    /// ejecuta, porque el micrófono y la palabra de activación viven acá. Antes lo único que podía
    /// apartarla era una lista de frases comparada contra la transcripción, así que cualquier forma
    /// de pedirlo que no estuviera escrita en el código se ignoraba.
    /// </remarks>
    private async Task RestAsync(RestDepth depth, CancellationToken cancellationToken)
    {
        CancelSpeechSafely();
        _neuralPlayer.Stop();
        await _speechSynthesizer.StopSpeakingAsync(CancellationToken.None).ConfigureAwait(false);

        if (depth == RestDepth.Callar)
        {
            return;
        }

        await EndConversationAsync("Se lo pidió el usuario", quiet: true, CancellationToken.None)
            .ConfigureAwait(false);

        // El turno sigue vivo después de que la herramienta devuelve: el modelo recibe el resultado,
        // contesta, y esa respuesta vuelve a publicar «pensando» y a hablar. Eso es exactamente lo
        // que el usuario ve como que quedó penando con la burbuja abierta después de pedirle que
        // pare. La bandera hace que al terminar el turno se vuelva al reposo y se calle, pase lo que
        // pase con lo que el modelo haya contestado.
        Volatile.Write(ref _restRequested, 1);

        if (depth == RestDepth.Apagar)
        {
            // Soltar el micrófono del todo. Se reactiva a mano desde la bandeja, que es la única
            // forma de que «desactivate» signifique lo que dice.
            IsMuted = true;
        }
    }

    /// <summary>
    /// Arma la línea de contexto con lo que se sabe del usuario.
    /// </summary>
    /// <remarks>
    /// Sólo lo explícito —lo que el usuario pidió recordar—, nunca las observaciones ni las
    /// sugerencias sin aprobar: inyectar una suposición con el mismo peso que un dato dicho a
    /// propósito es cómo un asistente empieza a afirmar cosas que nadie le dijo.
    /// </remarks>
    private async Task<string?> DescribePersonalMemoryAsync(CancellationToken cancellationToken)
    {
        var items = await _memory
            .ListAsync(PersonalMemoryKind.Explicit, cancellationToken)
            .ConfigureAwait(false);

        if (items.Count == 0)
        {
            return null;
        }

        var lines = items
            .OfType<ExplicitMemory>()
            .Take(20)
            .Select(item => $"- {item.Content}");

        return "Lo que sabés del usuario porque te lo pidió él:\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// Levanta los servidores MCP declarados y rehace el orquestador para que sus herramientas
    /// existan desde el primer pedido. Sin servidores declarados no hace absolutamente nada.
    /// </summary>
    private async Task ConnectMcpServersAsync(CancellationToken cancellationToken)
    {
        var servers = await McpToolProvider.LoadAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (servers.Count == 0)
        {
            return;
        }

        var provider = new McpToolProvider();
        var tools = await provider
            .ConnectAsync(servers, _options.ConfirmActions, cancellationToken)
            .ConfigureAwait(false);

        // Un servidor que no levanta se informa y no impide arrancar: quedarse sin asistente porque
        // falta un ejecutable de terceros sería peor que quedarse sin esa capacidad.
        foreach (var failure in provider.Failures)
        {
            RuntimeTrace.Write("mcp.fallo", failure);
        }

        if (tools.Count == 0)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _mcpProvider = provider;
        _mcpTools = tools;

        RuntimeTrace.Write(
            "mcp.listo",
            $"servidores={servers.Count(server => server.Enabled)} · herramientas={tools.Count}");
    }

    /// <summary>
    /// Rehace el orquestador conservando las suscripciones, para que cambie el prompt y no el resto.
    /// </summary>
    /// <remarks>
    /// Se llama una sola vez, al arrancar, cuando ya se sabe el nombre elegido y qué herramientas MCP
    /// levantaron. Antes había dos lugares que rehacían el orquestador a mano y cada uno tenía que
    /// acordarse de desenganchar y volver a enganchar los dos eventos; olvidarse de uno dejaba al
    /// orbe congelado en el último estado que alcanzó a ver.
    /// </remarks>
    private void RebuildOrchestrator(IReadOnlyList<IAssistantTool>? extraTools)
    {
        _orchestrator.StateChanged -= OrchestratorOnStateChanged;
        _orchestrator.ProgressChanged -= OrchestratorOnProgressChanged;
        _orchestrator = BuildOrchestrator(extraTools);
        _orchestrator.StateChanged += OrchestratorOnStateChanged;
        _orchestrator.ProgressChanged += OrchestratorOnProgressChanged;
        _localCommands = new LocalCommandRouter(_orchestrator, _memory);
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

    /// <summary>
    /// Hay autorización de gasto para hoy. Vive en memoria y muere con el proceso, a propósito:
    /// esa fragilidad <em>es</em> la garantía, y persistirla sería quitar la única fricción real.
    /// </summary>
    public bool HasSpendAuthorization => _budgetOverrideDay == DateOnly.FromDateTime(DateTime.Now);

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

    /// <summary>Cómo se llama el asistente en esta instalación.</summary>
    public string AssistantName => _identity.Name;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isInitialized)
        {
            return;
        }

        // Las preferencias primero, porque de ahí sale el nombre del asistente y el nombre entra en
        // la primera línea del prompt del sistema. Si esto se leyera después de armar el orquestador,
        // el asistente se presentaría con el nombre de fábrica durante toda la sesión.
        var loaded = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _settings = loaded.Settings;
        _identity = new AssistantIdentity(_settings.AssistantName);
        _options = ViernesOptions.FromEnvironment(assistantName: _identity.Name);

        // Antes de que exista una conversación: acá el orquestador todavía se puede rehacer sin que
        // nadie pierda historial ni quede a mitad de un turno.
        await ConnectMcpServersAsync(cancellationToken).ConfigureAwait(false);
        RebuildOrchestrator(_mcpTools);

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
            Phrases = ResolveWakePhrases(_settings.EffectiveWakePhrases),
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


        // Proactividad: mira el escritorio de fondo y sólo habla si algo lo amerita de verdad.
        _signals = new DesktopSignals(
            new SalienceGate(),
            _environment.IdleTime,
            _environment.ForegroundTitle,
            OnObservation);

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
            IsCloudConfigured ? AssistantVisualState.Idle : AssistantVisualState.Unconfigured,
            $"{providerStatus} · {wakeStatus}",
            // Primero el hecho, después lo que igual funciona. Sin mayúsculas y sin sonar a falla:
            // que falte la clave no es un error, es una instalación sin terminar.
            IsCloudConfigured
                ? "Lista para ayudarte."
                : "Falta la clave de OpenRouter. Andan recordatorios, agenda y memoria; " +
                  "lo que necesita el modelo queda esperando.",
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

        // El turno corre bajo su propio token, encadenado al de quien llamó. Es lo que le da al freno
        // algo que cortar: los pedidos escritos llegan acá con CancellationToken.None —el comando de
        // la interfaz no tiene otro que dar—, así que sin esto el atajo apagaba la voz mientras el
        // bucle de herramientas seguía tecleando en la ventana de al lado, y en pantalla decía
        // «Corté todo».
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _turnCancellation, turn);
        previous?.Dispose();

        try
        {
            await PauseWakeWordAsync(turn.Token).ConfigureAwait(false);
            CancelSpeechSafely();
            _neuralPlayer.Stop();
            await _speechSynthesizer.StopSpeakingAsync(turn.Token).ConfigureAwait(false);
            return await ProcessRequestAsync(text, spoken, turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            // Cortar a propósito no es un error, y el mensaje tiene que decir lo que pasó de verdad.
            return "Corté lo que estaba haciendo.";
        }
        finally
        {
            Interlocked.CompareExchange(ref _turnCancellation, null, turn);
            Interlocked.Exchange(ref _requestActive, 0);
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);

            // La última palabra del turno tiene que ser el reposo. Si en el medio el usuario pidió
            // parar, el turno igual siguió —el modelo contestó al resultado de la herramienta— y esa
            // respuesta dejó el orbe pensando y la burbuja abierta. Acá se corrige al final, cuando
            // ya no queda nadie que pueda volver a pisarlo.
            if (Interlocked.Exchange(ref _restRequested, 0) == 1)
            {
                CancelSpeechSafely();
                _neuralPlayer.Stop();
                await _speechSynthesizer.StopSpeakingAsync(CancellationToken.None).ConfigureAwait(false);

                Publish(new AssistantRuntimeUpdate(
                    AssistantVisualState.Idle,
                    IsMuted
                        ? "Micrófono apagado"
                        : $"Atento · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”",
                    MicrophoneActive: IsAnyMicrophoneActive(),
                    WakeWordEnabled: _isWakeWordEnabled,
                    ClearSteps: true,
                    ClearItems: true,
                    Quiet: true));
            }
        }
    }

    /// <summary>
    /// Cancela la voz sin poder fallar.
    /// </summary>
    /// <remarks>
    /// <see cref="SpeakCoreAsync"/> hace <c>Dispose()</c> del origen anterior y asigna el nuevo en
    /// dos pasos; un <c>Cancel()</c> que pegue justo en el medio tira <see cref="ObjectDisposedException"/>.
    /// Cuando eso pasaba dentro de <see cref="Panic"/>, la excepción subía hasta el hook del atajo,
    /// que se la tragaba entera: no se paraba el reproductor, no se cancelaba la conversación, no se
    /// silenciaba —y el rastro ya decía que había frenado, porque se escribe antes.
    /// </remarks>
    private void CancelSpeechSafely()
    {
        try
        {
            _speechCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ya estaba cancelado y desechado: no hay nada que cortar.
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

        // Capacidad reducida y error no comparten dibujo: gris dice «menos», rojo dice «falló».
        var state = result.State == AssistantState.Error
            ? AssistantVisualState.Error
            : result.IsLocalMode
                ? AssistantVisualState.Unconfigured
                : AssistantVisualState.Idle;
        Publish(new AssistantRuntimeUpdate(
            state,
            result.IsLocalMode ? "Sigo con lo de acá · no salió nada del equipo" : "Listo",
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
                ClearItems: outcome.Items is null,
                ListKind: outcome.ListKind));
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
                ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
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
                    ? $"Oculto y atento · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
                    : "Widget oculto · escucha detenida",
                MicrophoneActive: IsAnyMicrophoneActive(),
                WakeWordEnabled: _isWakeWordEnabled));
            return;
        }

        await ResumeWakeWordAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            _isWakeWordEnabled && !IsMuted
                ? $"Atento · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
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

    /// <summary>
    /// Una sola voz por vez, siempre.
    /// </summary>
    /// <remarks>
    /// Sin esta cola, dos respuestas que se generan casi juntas —la respuesta del turno y la pregunta
    /// de confirmación, por ejemplo— se pisaban: en el registro quedaban dos <c>voz.inicio</c> sin
    /// que ninguna hubiera terminado, y una de ellas seguía sonando <em>durante</em> la captura
    /// siguiente. Con el micrófono abierto y Viernes hablando, el resultado es que se escucha a sí
    /// misma y reacciona sola. Serializar acá es lo que vuelve imposible ese solapamiento.
    /// </remarks>
    private async Task SpeakIfEnabledAsync(string text, CancellationToken cancellationToken)
    {
        if (IsMuted || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SpeakCoreAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _speechGate.Release();
        }
    }

    // Acá vivían los acuses: unos «mhm», «dale», «a ver» grabados al arrancar que sonaban cuando la
    // respuesta tardaba más de medio segundo. La idea era tapar el silencio; el efecto era el
    // contrario. Una persona que piensa hace un silencio distinto cada vez, y esto hacía siempre el
    // mismo de una lista de cuatro. A la tercera vez ya no se oye una duda: se oye un aparato
    // reproduciendo un archivo, y eso vuelve mecánico todo lo que viene después.
    //
    // El silencio no era el problema: el problema es lo que dura, y eso se arregla haciendo que
    // conteste antes —streaming y voz por oración—, no poniéndole una alfombra encima.

    /// <summary>
    /// Espera a que no quede voz sonando ni encolada. Es una espera, no una reserva: sólo toma la
    /// cola para comprobar que está libre y la suelta enseguida.
    /// </summary>
    private async Task WaitUntilQuietAsync(CancellationToken cancellationToken)
    {
        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _speechGate.Release();
    }

    private async Task SpeakCoreAsync(string text, CancellationToken cancellationToken)
    {
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
    /// Algo que Viernes notó y que el filtro consideró digno de contarte.
    /// </summary>
    /// <remarks>
    /// No interrumpe una conversación en curso ni habla con el micrófono silenciado: si ya te tiene
    /// atención, el aviso puede esperar al final; y si te silenciaste, es porque no querés que hable.
    /// Cuando el orbe está oculto entra por el globo de la bandeja, que es la forma de avisar que no
    /// te tapa lo que estás haciendo.
    /// </remarks>
    private void OnObservation(Observation observation)
    {
        if (_isDisposed || IsMuted || _conversationActive)
        {
            return;
        }

        RuntimeTrace.Write("proactivo", $"{observation.Key} · {observation.Message}");

        RequestActivation(new ShellActivationRequest(
            ShellActivationReason.Reminder,
            "Viernes",
            observation.Message));

        _ = SpeakIfEnabledAsync(observation.Message, CancellationToken.None);
    }

    /// <summary>
    /// Freno de emergencia: corta todo lo que Viernes esté haciendo, ya.
    /// </summary>
    /// <remarks>
    /// No pasa por el modelo, ni por la política, ni por la conversación. Desde que mueve el cursor
    /// y escribe con el teclado, pedirle que pare <em>hablando</em> dejó de ser suficiente: si está
    /// tecleando en otra ventana, tu voz compite con su propia acción, y si algo salió mal lo último
    /// que querés es negociar con el sistema que se descontroló. Silencia además el micrófono, que
    /// es el corte duro: preferible pasarse de frenar que quedarse corto.
    /// </remarks>
    public void Panic()
    {
        if (_isDisposed)
        {
            return;
        }

        // Primero se corta, después se cuenta. Al revés, cualquier excepción en el corte dejaba un
        // rastro que decía «frenó» sobre un sistema que seguía andando.
        CancelSpeechSafely();
        _neuralPlayer.Stop();

        // El turno es lo único que puede estar tecleando en otra ventana o corriendo un comando.
        // Es lo que el atajo existe para cortar, y lo que hasta ahora no cortaba.
        try
        {
            _turnCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _conversationCancellation?.Cancel();
            _wakeHandoffCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _conversationActive = false;
        RuntimeTrace.Write("panico", "corte de emergencia por atajo global");

        _ = Task.Run(async () =>
        {
            try
            {
                await _speechSynthesizer.StopSpeakingAsync(CancellationToken.None).ConfigureAwait(false);
                if (_recognition?.IsMicrophoneActive == true)
                {
                    await _recognition.CancelPushToTalkAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Frenar nunca puede fallar de forma ruidosa.
            }
        });

        IsMuted = true;
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Idle,
            "Frenado en seco · micrófono silenciado",
            $"Corté todo con {PanicSwitch.Shortcut}. Reactivame desde la bandeja.",
            MicrophoneActive: false,
            WakeWordEnabled: false));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelación de verdad: alguien la interrumpió a propósito. No hay nada que repetir por
            // la voz de respaldo, porque el silencio es lo que se pidió.
            return true;
        }
        catch (OperationCanceledException)
        {
            // El plazo del HttpClient vence como OperationCanceledException, y el cliente de voz sólo
            // atrapa errores de red. Devolver true acá era lo que hacía que quedarse sin internet
            // sonara exactamente igual que hablar: sin audio, sin voz de respaldo, y con un rastro
            // que decía «sonó». Ahora se trata como lo que es —un fallo— y cae a SAPI.
            RuntimeTrace.Write("voz.neural.timeout", "venció el plazo; paso a la voz del sistema");
            return false;
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
                        ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
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

    public Task EndConversationAsync(string reason, CancellationToken cancellationToken) =>
        EndConversationAsync(reason, quiet: false, cancellationToken);

    /// <summary>
    /// Cierra la conversación. Con <paramref name="quiet"/> vuelve al reposo sin dejar nada en
    /// pantalla, que es lo que corresponde cuando el cierre lo pidió el usuario.
    /// </summary>
    public async Task EndConversationAsync(string reason, bool quiet, CancellationToken cancellationToken)
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
            WakeWordEnabled: _isWakeWordEnabled,
            ClearSteps: true,
            ClearItems: true,
            Quiet: quiet));

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

                // No abrir el micrófono mientras quede una sola palabra por decir.
                //
                // La cola de voz impide que dos respuestas suenen juntas, pero no impedía que la
                // captura arrancara encima de una que todavía estaba sonando: en el registro se veía
                // «voz.inicio» y «captura.inicio» en el mismo milisegundo, y la voz terminando dos
                // segundos después con el micrófono ya abierto. Ahí Viernes se oye a sí misma,
                // transcribe su propia respuesta y contesta sola — eso es el «me escucha de la nada».
                // Esperar la cola acá lo vuelve imposible, sin importar quién haya encolado la voz.
                await WaitUntilQuietAsync(cancellationToken).ConfigureAwait(false);

                Publish(new AssistantRuntimeUpdate(
                    AssistantVisualState.Listening,
                    "Te escucho · decime «listo» para cortar",
                    MicrophoneActive: true));

                RuntimeTrace.Write("captura.inicio");
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // Sin estas marcas, «tarda en reconocer que hablé» y «tarda en contestar» dejan la
                // misma huella en el trace: un único número al final. Partirlo en abrir el
                // micrófono, oír voz y cortar es lo que distingue un problema del otro.
                _captureClock = clock;
                Interlocked.Exchange(ref _voiceTraced, 0);
                Interlocked.Exchange(ref _microphoneTraced, 0);
                // Ritmo de charla, no de dictado: corta 600 ms después de que dejás de hablar y
                // no se queda veinte segundos mirándote si no decís nada. La duración máxima
                // siempre tiene que superar a la ventana inicial o la validación tira.
                var capture = await _recognition
                    .RecognizeSingleUtteranceAsync(
                        // La ventana es larga a propósito: dentro de una conversación el corte lo
                        // decide que hayas hablado y terminado, no un cronómetro. Si vence sin voz,
                        // el bucle vuelve a abrir enseguida, así que la escucha no se interrumpe.
                        // El techo importa más de lo que parece: cada segundo capturado es un segundo
                        // que Whisper después tiene que transcribir. Con 30 s de tope, una captura que
                        // no cortaba bien se volvía medio minuto de audio y casi otro tanto de espera.
                        // Quince alcanzan de sobra para una frase, y el bucle reabre al instante.
                        // Los dos silencios miden cosas distintas y por eso no valen lo mismo.
                        // EndSilence es «¿terminaste de hablar?»: a 600 ms cualquier pausa para
                        // buscar la palabra se leía como punto final y contestaba sobre media idea.
                        // Segundo y medio deja pensar sin que la respuesta se sienta demorada; cada
                        // milisegundo de acá se paga en la espera de todos los turnos. InitialSilence
                        // es otra cosa —«no dijiste nada»— y ahí no hay nada que esperar: vence y el
                        // bucle reabre enseguida.
                        new SingleUtteranceRecognitionOptions
                        {
                            InitialSilenceTimeout = TimeSpan.FromSeconds(10),
                            EndSilenceTimeout = TimeSpan.FromMilliseconds(1000),
                            // Doce y no veinte: el techo no es cuánto te dejo hablar, es cuánto
                            // audio le doy después a Whisper. Una captura de veinte segundos costó
                            // veintinueve transcribiéndola. Una frase de conversación no llega ni
                            // cerca, y si llega, el bucle reabre y seguís.
                            MaximumDuration = TimeSpan.FromSeconds(12)
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
                    // El silencio no cierra de entrada: sigue esperando, que es lo que se le pidió.
                    // Pero pregunta una sola vez, y si tampoco hay respuesta se despide en vez de
                    // quedarse preguntando para siempre con el micrófono abierto.
                    consecutiveSilences++;
                    if (consecutiveSilences == 3)
                    {
                        await SpeakIfEnabledAsync("¿Seguís ahí?", cancellationToken).ConfigureAwait(false);
                    }
                    else if (consecutiveSilences >= 6)
                    {
                        await SpeakIfEnabledAsync("Cualquier cosa me avisás.", CancellationToken.None)
                            .ConfigureAwait(false);
                        await EndConversationAsync("Cerré por silencio", quiet: true, CancellationToken.None)
                            .ConfigureAwait(false);
                        await LearnFromConversationAsync().ConfigureAwait(false);
                        return;
                    }

                    Publish(new AssistantRuntimeUpdate(
                        AssistantVisualState.Listening,
                        "Sigo escuchando · decime «listo» para cortar",
                        MicrophoneActive: true));
                    continue;
                }

                consecutiveSilences = 0;

                // Confirmar por voz. Sin esto, cualquier acción que pida permiso queda muerta en una
                // conversación hablada: el botón vive en la burbuja, y la burbuja está oculta.
                if (HasPendingConfirmation)
                {
                    if (IsAffirmative(transcript))
                    {
                        await ConfirmPendingAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (IsNegative(transcript))
                    {
                        DismissPending();
                        await SpeakIfEnabledAsync("Listo, no lo hago.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if (IsClosingPhrase(transcript))
                {
                    await SpeakIfEnabledAsync("Cualquier cosa me avisás.", CancellationToken.None)
                        .ConfigureAwait(false);

                    // En modo silencioso: se despide y se encoge. Antes cerraba dejando la despedida
                    // en la burbuja siete segundos, y esos siete segundos con el desplegable abierto
                    // después de pedirle que pare se leen como que no hizo caso.
                    await EndConversationAsync("Conversación cerrada", quiet: true, CancellationToken.None)
                        .ConfigureAwait(false);
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

    private bool HasPendingConfirmation
    {
        get
        {
            lock (_confirmationGate)
            {
                return _pendingConfirmation is not null;
            }
        }
    }

    private static readonly string[] Affirmatives =
        ["si", "sí", "dale", "hacelo", "hazlo", "confirmo", "confirmá", "confirma", "obvio", "por favor", "ok", "okey", "correcto"];

    private static readonly string[] Negatives =
        ["no", "cancelá", "cancela", "cancelalo", "mejor no", "dejalo", "olvidalo", "nada"];

    internal static bool IsAffirmative(string text) => MatchesShortPhrase(text, Affirmatives);

    internal static bool IsNegative(string text) => MatchesShortPhrase(text, Negatives);

    /// <summary>
    /// Una confirmación es corta por naturaleza. Exigir brevedad evita que «no me acuerdo si dale»
    /// dentro de una frase larga dispare una acción que el usuario no pidió.
    /// </summary>
    private static bool MatchesShortPhrase(string text, string[] phrases)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0 || normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
        {
            return false;
        }

        return phrases.Any(phrase =>
            normalized.Equals(phrase, StringComparison.Ordinal) ||
            normalized.StartsWith(phrase + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Minúsculas, sin puntuación y <b>sin acentos</b>.
    /// </summary>
    /// <remarks>
    /// Lo de los acentos no es cosmético: el usuario dijo «dejá de oír» y la frase no cerró nada
    /// porque la lista tenía «oir» y el transcriptor escribió «oír». Whisper acentúa según le
    /// parece —«recordame» o «recórdame», «deja» o «dejá»— así que comparar con acentos es comparar
    /// contra una moneda al aire. Plegarlos elimina la clase entera de fallo.
    /// </remarks>
    private static string Normalize(string text) => ClosingPhrase.Normalize(text);

    /// <summary>
    /// Sólo cierra si la frase es corta y es esencialmente la despedida: «gracias por todo esto que
    /// hiciste» no debería cortar una conversación.
    /// </summary>
    internal static bool IsClosingPhrase(string text) => ClosingPhrase.IsClosing(text);

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

        // Un turno alcanza. Exigir dos daba por sentado que las charlas son largas, y no lo son: en
        // el registro real casi todas cerraron con cero o un turno, así que la destilación no corrió
        // nunca y el archivo de memoria personal jamás llegó a existir. Una charla de un solo pedido
        // también dice algo sobre cómo trabaja el usuario.
        if (turns.Count < 1 || !IsCloudConfigured)
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

            ConversationTurnResult distilled;
            Volatile.Write(ref _distilling, 1);
            try
            {
                distilled = await _orchestrator.ProcessAsync(prompt, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                // Se baja pase lo que pase. Si quedara arriba, el turno siguiente del usuario
                // tampoco mostraría que está pensando: el remedio sería peor que la enfermedad.
                Volatile.Write(ref _distilling, 0);
            }

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
            $"Recordatorio de {_identity.Name}",
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
            // Las frases NO se vuelven a escribir. Las que tiene el servicio de activación pueden
            // venir derivadas del nombre o de VIERNES_WAKE_PHRASES, y persistir cualquiera de las dos
            // las congelaría: renombrar el asistente dejaría «Hola Viernes» escrito en el archivo y
            // seguiría despertando con el nombre viejo. Sólo se guardan si alguien las eligió a mano.
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
        // Medido en este equipo, las detecciones reales de «Viernes» dieron entre 0,62 y 0,68. Con
        // el umbral en 0,55 sobraba margen por debajo, y ahí entraban frases que no eran el nombre:
        // se abría una conversación sin que nadie la hubiera llamado. Sesenta corta ese margen sin
        // tocar el piso de lo que sí se detectó.
        const float fallback = 0.60f;
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
    private void RecognitionOnAudioLevel(object? sender, AudioLevelEventArgs e)
    {
        // Una sola línea por captura: esto corre decenas de veces por segundo y trazar cada llamada
        // convertiría el registro en ruido y el diagnóstico en imposible.
        if (e.IsVoice && Interlocked.Exchange(ref _voiceTraced, 1) == 0)
        {
            RuntimeTrace.Write("voz.detectada", $"a los {_captureClock?.ElapsedMilliseconds ?? -1} ms");
        }

        Updated?.Invoke(this, new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            AudioLevel: e.Level));
    }

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
        if (e.IsActive && Interlocked.Exchange(ref _microphoneTraced, 1) == 0)
        {
            RuntimeTrace.Write("mic.abierto", $"a los {_captureClock?.ElapsedMilliseconds ?? -1} ms");
        }

        // Dentro de una conversación, que el micrófono se cierre significa «terminaste de hablar»,
        // no «no pasa nada». Caer al reposo un instante antes de que empiece a pensar producía el
        // parpadeo al estado de reposo entre escuchar y pensar: la forma decía que se había ido.
        if (!e.IsActive && _conversationActive)
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Thinking,
                "Pensando…",
                MicrophoneActive: IsAnyMicrophoneActive(),
                WakeWordEnabled: _isWakeWordEnabled));
            return;
        }

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
                ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
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

    private void OrchestratorOnProgressChanged(object? sender, TurnProgressEventArgs e)
    {
        // La destilación no se muestra: es trabajo interno sobre el mismo orquestador, y sus pasos
        // son el prompt que se le da al modelo para resumir la charla.
        if (Volatile.Read(ref _distilling) != 0)
        {
            return;
        }

        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            Steps: e.Steps));
    }

    private void OrchestratorOnStateChanged(object? sender, AssistantStateChangedEventArgs e)
    {
        if (Volatile.Read(ref _distilling) != 0)
        {
            return;
        }

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

        _signals?.Dispose();
        _environment.Dispose();

        // Los servidores MCP son procesos hijos: si no se cierran, quedan vivos después de salir.
        if (_mcpProvider is not null)
        {
            await _mcpProvider.DisposeAsync().ConfigureAwait(false);
            _mcpProvider = null;
        }

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
