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
using Viernes.Core.Missions;
using Viernes.Core.Models;
using Viernes.Core.Persistence;
using Viernes.Core.Projects;
using Viernes.Core.Scheduling;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Viernes.Core.Usage;
using Viernes.Core.Voice;
using Viernes.Memory.Brain;
using Viernes.Memory.Chats;
using Viernes.Memory.Models;
using Viernes.Memory.Persistence;
using Viernes.Platform.Windows.Actions;
using Viernes.Platform.Windows.Processes;
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
/// <remarks>
/// Es parcial: el camino en vivo —la sesión dúplex con Gemini— vive en
/// <c>AssistantRuntime.Live.cs</c>. No es una división por tamaño sino por camino: son dos formas
/// distintas de tener una conversación y la única frontera entre las dos está en
/// <see cref="StartConversationAsync"/>. Tenerlas mezcladas en el mismo archivo haría que cada
/// arreglo del camino de siempre tuviera que releerse preguntándose a cuál de los dos afecta.
/// </remarks>
internal sealed partial class AssistantRuntime : IAssistantRuntime
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

    /// <summary>Si el último renombrado dejó algo sin hacer. Ver <see cref="SetAssistantNameAsync"/>.</summary>
    private bool _renameLeftSomethingUndone;

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

    /// <summary>
    /// La voz del sistema. Nula hasta que se leyeron las preferencias.
    /// </summary>
    /// <remarks>
    /// Se construye en <see cref="InitializeAsync"/> y no en el constructor porque la voz elegida
    /// vive en <c>settings.json</c> y ahí todavía no está leído. Construirla antes es lo que hacía
    /// que <c>PreferredVoiceName</c> no llegara nunca al sintetizador: quedaba escrita en el archivo
    /// y no cambiaba absolutamente nada. Hay una sola forma de fijar la voz —las opciones con las
    /// que se construye— y ahora esas opciones se arman cuando ya se sabe cuál es.
    /// </remarks>
    private ISpeechService? _speechSynthesizer;
    // No es readonly porque cambiar la clave de OpenRouter obliga a rehacerlo: el cliente toma las
    // opciones al construirse y ViernesOptions es inmutable, así que reemplazar _options no le llega.
    private OpenRouterSpeechClient _neuralVoice;

    /// <summary>
    /// La voz de Google. Es la principal desde que el usuario eligió Aoede escuchando las catorce.
    /// </summary>
    /// <remarks>
    /// Convive con la de OpenRouter en vez de reemplazarla de cuajo: si falta la clave de Google o
    /// el modelo está saturado, se cae a la anterior y recién después a la del sistema. Quedarse
    /// mudo por un proveedor es exactamente el fallo que costó una tarde encontrar.
    /// </remarks>
    private readonly GeminiSpeechClient _googleVoice;
    private readonly NeuralSpeechPlayer _neuralPlayer = new();
    private CancellationTokenSource? _speechCancellation;

    /// <summary>
    /// Conversación abierta: mientras dure, el micrófono vuelve a abrirse solo después de cada
    /// respuesta y no hace falta repetir el nombre. Se cierra únicamente cuando el usuario lo dice.
    /// </summary>
    private bool _conversationActive;

    /// <summary>
    /// Lo que se lleva transcripto de la tanda de habla abierta.
    /// </summary>
    /// <remarks>
    /// Uno solo para los tres caminos por los que entra voz —las hipótesis de SAPI, los fragmentos
    /// de la sesión en vivo y el WAV que entrega el oído continuo—: son tres formas de traer el
    /// mismo texto y sin un acumulador común cada una armaría la línea a su manera, que es
    /// exactamente cómo la burbuja termina dibujándose distinta según por dónde entró la voz.
    /// </remarks>
    private readonly DictationBoard _dictation = new();
    private System.Diagnostics.Stopwatch? _captureClock;
    private int _voiceTraced;
    private int _microphoneTraced;
    private CancellationTokenSource? _conversationCancellation;

    /// <summary>Lo que el freno corta: el turno en curso, venga de la voz o del teclado.</summary>
    private CancellationTokenSource? _turnCancellation;

    /// <summary>Distinto de cero cuando el usuario pidió parar y el turno todavía no terminó.</summary>
    private int _restRequested;

    /// <summary>
    /// Distinto de cero mientras corre una acción confirmada a mano.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfirmPendingAsync"/> ejecuta herramientas sin pasar por <see cref="SendAsync"/>,
    /// así que <c>_requestActive</c> vale cero ahí. Sin esta segunda bandera, el descanso pedido
    /// desde una acción confirmada no sabía si tenía un turno que lo consumiera.
    /// </remarks>
    private int _confirmActive;

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

    /// <summary>Distinto de cero mientras el vigía está intentando recuperar el micrófono.</summary>
    private int _watchdogRunning;

    private int _conversationLoopRunning;

    /// <summary>Lo que dijo el usuario en la charla, para destilar al cerrarla. No se persiste.</summary>
    private readonly List<string> _conversationTurns = [];

    /// <summary>
    /// La charla abierta, tal como va quedando escrita.
    /// </summary>
    /// <remarks>
    /// Nulo entre conversaciones. Lo tocan el bucle de conversación, el hilo que lee del socket y el
    /// del cierre, así que se lee y se escribe con <see cref="Volatile"/>: lo que no puede pasar es
    /// que un turno tardío escriba en una charla que ya se cerró, y por eso el campo se pone en nulo
    /// antes de cerrarla y no después.
    /// </remarks>
    private ChatArchive? _chat;

    // Las listas de frases de cierre viven en Viernes.Core.Conversation.ClosingPhrase: es lógica de
    // texto pura, y acá adentro no había forma de probarla —el proyecto de pruebas no puede
    // referenciar la aplicación—, así que su test terminaba reimplementando la regla y midiendo su
    // propia expectativa.

    private readonly JsonUserDataStore _dataStore = new();
    /// <summary>
    /// Las reglas que le enseñó el usuario, los objetivos abiertos y los permisos aprendidos.
    /// </summary>
    /// <remarks>
    /// <b>Se arman acá y se le pasan a la fábrica, por el mismo motivo que las misiones:</b> el
    /// camino escrito los lee en cada turno a través del orquestador, y el hablado arma su
    /// instrucción por su cuenta. Si la fábrica los creara sola, el anfitrión no tendría con qué
    /// armarla y hablando seguiría sin saber qué le enseñaron.
    /// <para>
    /// Que hablando no los tuviera dejó de ser defendible cuando pasó a declarar todas las
    /// herramientas: <b>una regla que el usuario enseñó a propósito para frenar algo no puede valer
    /// sólo cuando escribe</b>. El motivo que estaba escrito —«allá hay treinta herramientas y acá
    /// tres»— se cayó con ellas.
    /// </para>
    /// </remarks>
    private readonly Viernes.Core.Learning.RuleBook _ruleBook = new();

    private readonly Viernes.Core.Goals.GoalBook _goalBook = new();

    private readonly Viernes.Core.Autonomy.AutonomyPolicy _autonomy = new();

    private readonly JsonPersonalMemoryStore _memory = new();

    /// <summary>
    /// Lo que sabe, en archivos de texto.
    /// </summary>
    /// <remarks>
    /// Convive con <see cref="_memory"/> y no lo reemplaza <em>todavía</em>. Aquél sigue siendo el
    /// dueño de lo que el usuario pidió recordar a propósito —lo usan la herramienta de memoria, el
    /// conector MCP y la pantalla de revisión—; el cerebro es lo que ella destila sola de las
    /// charlas.
    /// <para>
    /// Los dos miran la misma charla y hacen cosas distintas con ella, y por eso conviven: aquél
    /// deja <em>sugerencias</em> para que el usuario apruebe o descarte en la pantalla de memoria;
    /// éste aprende solo y se corrige solo. Si algún día el cerebro empieza a proponer, sobra uno.
    /// </para>
    /// </remarks>
    private readonly Brain _brain = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "cerebro"));

    /// <summary>
    /// Las misiones. Es la misma instancia que ve la herramienta <c>mision</c>.
    /// </summary>
    /// <remarks>
    /// Compartirla no es una optimización: el libro cachea en memoria lo que leyó del disco, así que
    /// dos instancias serían dos verdades. El orbe diría «te espero» sobre una pregunta que la
    /// herramienta ya dio por contestada.
    /// <para>
    /// Acá decía que el caché no se invalida nunca y que un <c>misiones.json</c> editado por fuera
    /// pedía reiniciar. <b>Ya no.</b> El libro relee cuando el archivo cambió —compara fecha y
    /// tamaño— y su compuerta es estática por ruta, así que dos instancias sobre el mismo archivo se
    /// excluyen de verdad en vez de pisarse. El conector, que es justamente un editor de afuera,
    /// funciona con el orbe abierto.
    /// </para>
    /// <para>
    /// Compartir la instancia sigue valiendo la pena igual, y ahora por su razón real: evita una
    /// relectura del disco por barrido del vigía, que corre cada cinco segundos.
    /// </para>
    /// </remarks>
    private readonly MissionBook _missionBook = new();

    /// <summary>Lo que está haciendo Claude Code. Sólo lee archivos; nunca escribe en la sesión.</summary>
    private readonly ClaudeSessionWatcher _projectWatcher = new();

    private readonly ReminderScheduler _reminderScheduler;
    private readonly LocalSettingsStore _settingsStore = new();
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

    /// <summary>
    /// El que decide qué es voz, uno solo para todo el asistente.
    /// </summary>
    /// <remarks>
    /// Cargar el modelo entrenado cuesta, y estaba pasando dos veces: una al armar el oído y otra
    /// <b>por conversación</b>, adentro del micrófono de la sesión en vivo — o sea, justo después de
    /// abrir el websocket y antes de empezar a subir audio. Ahí se perdían las primeras palabras de
    /// la primera frase de cada charla. Se arma una vez, en el arranque, y se presta: los dos nunca
    /// capturan a la vez porque el micrófono es uno solo.
    /// </remarks>
    private IVoiceActivityDetector? _voiceDetector;

    /// <summary>El oído continuo, si es el que está escuchando. Nulo con el wake de siempre.</summary>
    private ContinuousWakeListener? _continuousWake;

    /// <summary>Distinto de cero mientras se está armando la frase que empezó con el nombre.</summary>
    private int _awaitingUtterance;

    /// <summary>Distinto de cero mientras se transcribe el WAV que entregó el oído continuo.</summary>
    /// <remarks>
    /// El proveedor levanta <c>TranscriptionUpdated</c> con <c>isFinal</c> por <b>cada tramo</b>, y
    /// ese evento es el que alimenta la burbuja. Para el WAV del oído eso es justo lo que no se
    /// quiere: la frase entera —incluido lo que se dijo <em>antes</em> del nombre, que no se le dijo
    /// a ella— salía como firme a opacidad plena, y recién después
    /// <see cref="HandleWakeUtteranceAsync"/> la partía y bajaba el tramo recuperado al 40 %. Se
    /// veía el parpadeo. Acá la línea la arma el único que sabe partirla; mientras tanto el camino
    /// de siempre no publica nada.
    /// </remarks>
    private int _splittingUtterance;
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

    /// <summary>Cada cuánto se vuelven a mirar las condiciones que encienden los estados de fondo.</summary>
    /// <remarks>
    /// Los estados de fondo no los publica nadie: no hay un evento cuando una misión queda esperando
    /// respuesta ni cuando Claude Code termina su turno. Se miran, y cinco segundos es el tramo en el
    /// que enterarse sigue siendo enterarse a tiempo sin que el proceso se haga notar.
    /// </remarks>
    private static readonly TimeSpan AmbientPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cada cuánto se releen las sesiones de Claude Code.
    /// </summary>
    /// <remarks>
    /// Mucho más espaciado que el resto porque cuesta lo que cuesta: recorre el árbol de sesiones y
    /// lee la cola de cada archivo. Un proyecto que quedó esperando va a seguir esperando dentro de
    /// tres cuartos de minuto; leerlo cada cinco segundos sería pagar disco todo el día por eso.
    /// </remarks>
    private static readonly TimeSpan ProjectPeriod = TimeSpan.FromSeconds(45);

    private System.Threading.Timer? _ambientTimer;

    /// <summary>
    /// Distinto de cero mientras el vigía está mirando.
    /// </summary>
    /// <remarks>
    /// El barrido de sesiones lee archivos y puede tardar más que el intervalo. Sin esto, un disco
    /// lento apila barridos hasta que hay diez leyendo el mismo árbol a la vez.
    /// </remarks>
    private int _ambientRunning;

    private DateTimeOffset _projectsReadAt;

    /// <summary>
    /// Cuánto se queda el orbe en «un proyecto te espera» antes de bajar al estado de siempre.
    /// </summary>
    /// <remarks>
    /// Es un aviso, no una condición, y esa es toda la diferencia. Una sesión de Claude Code que
    /// espera puede quedarse esperando toda la tarde, y sin este vencimiento el orbe quedaba violeta
    /// toda la tarde: un aviso que no se va deja de ser un aviso y pasa a ser el fondo de pantalla.
    /// Peor todavía, tapaba «guardia», que es donde se ve que está atenta al nombre — la única
    /// información que el usuario mira todo el tiempo.
    /// <para>
    /// Vuelve a anunciarse cuando cambia <em>qué</em> proyecto espera, no cuando pasa el tiempo: si
    /// arranca a esperar otro, eso es noticia nueva. Y no se pierde nada al bajar, porque el
    /// desplegable de proyectos sigue estando: lo que se libera es el orbe, no el dato.
    /// </para>
    /// <para>
    /// Los 45 s no salen del boceto. Los desplegables informativos de la referencia viven 7 s, pero
    /// ésos aparecen adelante de la cara; un estado en un orbe de 108 px en un rincón necesita
    /// sobrevivir a que mires para otro lado.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ProjectNoticeLife = TimeSpan.FromSeconds(45);

    /// <summary>Qué proyectos esperaban la última vez, para saber si lo que hay ahora es noticia.</summary>
    private string _projectWaitingSignature = string.Empty;

    /// <summary>Hasta cuándo el aviso de proyecto se queda con el orbe.</summary>
    private DateTimeOffset _projectNoticeUntil;
    private volatile bool _missionWaiting;
    private volatile bool _missionRunning;
    private volatile bool _projectWaiting;

    /// <summary>
    /// El último veredicto sobre el oído: distinto de cero si la última vez que se supo algo, el
    /// micrófono no entregaba señal.
    /// </summary>
    /// <remarks>
    /// Sorda no es error y no es mute: el dispositivo está —o debería estar— tomando y no entra
    /// nada. Se enciende cuando Windows niega el micrófono o cuando una captura falla por el
    /// dispositivo.
    /// <para>
    /// <b>Es memoria, no estado: nadie la lea directamente.</b> Se lee por <see cref="IsDeaf"/>, que
    /// la contrasta contra el oído real antes de creerle. Ahí está explicado por qué.
    /// </para>
    /// </remarks>
    private int _deaf;

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
        _reminderScheduler.AgendaItemDue += ReminderSchedulerOnAgendaItemDue;
        _neuralVoice = new OpenRouterSpeechClient(
            _httpClient,
            _options,
            SpeechSynthesisOptions.FromEnvironment());
        _googleVoice = new GeminiSpeechClient(
            _httpClient,
            () => LocalCredentials.Get("GOOGLE_API_KEY"));

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
            // El libro va explícito para que la herramienta y el orbe lean el mismo: la fábrica
            // creaba uno propio, y con dos instancias cacheando aparte «te espero» podía quedar
            // encendido sobre una pregunta que la herramienta ya había contestado.
            missions: _missionBook,
            // Los tres van explícitos por lo mismo que el libro de misiones: la fábrica crearía los
            // suyos, y con dos instancias cacheando aparte una regla enseñada por la herramienta no
            // la vería quien arma la instrucción hablada.
            rules: _ruleBook,
            goals: _goalBook,
            autonomy: _autonomy,
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
        await SilenceVoiceAsync(CancellationToken.None).ConfigureAwait(false);

        // El corte tiene que verse en el mismo cuadro en que se pide, o no se lee como obediencia.
        // Sólo cuando estaba hablando o pensando: si estaba quieta no interrumpió nada, y encender
        // «me callo» sobre el reposo sería inventar un gesto. Se apaga sola cuando el turno cierra.
        if (_lastVisualState is AssistantVisualState.Speaking or AssistantVisualState.Thinking)
        {
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.Interrupted,
                "Me callo",
                MicrophoneActive: IsAnyMicrophoneActive()));
        }

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

        // Y si nadie va a consumirla, se consume acá mismo.
        //
        // La bandera la levantaba esta función y la bajaba el finally de SendAsync, dando por hecho
        // que descansar siempre pasa por un turno. No siempre: una acción confirmada a mano ejecuta
        // herramientas por fuera de SendAsync, y por ese camino la bandera quedaba viva hasta el
        // final del siguiente turno cualquiera —uno que el usuario había pedido de verdad— y ahí
        // publicaba un reposo espurio que le borraba la respuesta de la pantalla.
        if (Volatile.Read(ref _requestActive) == 0 && Volatile.Read(ref _confirmActive) == 0)
        {
            await ConsumeRestRequestAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Baja la bandera de descanso y, si estaba levantada, deja el orbe callado y en reposo.
    /// </summary>
    /// <remarks>
    /// La última palabra del turno tiene que ser el reposo. Si en el medio el usuario pidió parar,
    /// el turno igual siguió —el modelo contestó al resultado de la herramienta— y esa respuesta
    /// dejó el orbe pensando y la burbuja abierta. Acá se corrige al final, cuando ya no queda nadie
    /// que pueda volver a pisarlo.
    /// </remarks>
    private async Task ConsumeRestRequestAsync()
    {
        if (Interlocked.Exchange(ref _restRequested, 0) != 1)
        {
            return;
        }

        CancelSpeechSafely();
        _neuralPlayer.Stop();
        await SilenceVoiceAsync(CancellationToken.None).ConfigureAwait(false);

        Publish(new AssistantRuntimeUpdate(
            Resting(),
            IsMuted
                ? "Micrófono apagado"
                : $"Atento · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled,
            ClearSteps: true,
            ClearItems: true,
            Quiet: true));
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

        // El corte que estaba acá dejaba el cerebro INALCANZABLE. Era de cuando lo único que se le
        // contaba al modelo eran los recuerdos explícitos: sin ninguno, no había nada que decir y se
        // volvía. Cuando se sumó el cerebro debajo, ese return siguió estando, así que un usuario
        // sin un solo recuerdo guardado a propósito —el caso normal— no recibía NADA de lo que ella
        // hubiera aprendido sola. Todo el camino de aprender existía y no cambiaba una coma.
        var lines = items
            .OfType<ExplicitMemory>()
            .Take(20)
            .Select(item => $"- {item.Content}");

        var explicito = items.Count == 0
            ? null
            : "Lo que sabés del usuario porque te lo pidió él:\n" + string.Join('\n', lines);

        return Juntar(explicito, DescribirCerebro());
    }

    /// <summary>
    /// Lo que aprendió solo, para el turno que viene.
    /// </summary>
    /// <remarks>
    /// <b>Sin esto, aprender no cambia nada.</b> Un cerebro que se escribe y no se lee es un diario
    /// íntimo: queda lindo en la carpeta y la asistente se comporta exactamente igual que el primer
    /// día. Esta función es la única razón por la que destilar sirve.
    /// <para>
    /// Va entero mientras entre, y cuando deja de entrar pasa a ser un índice: los títulos con su
    /// alcance, sin los cuerpos. Degradar así y no cortar por la mitad importa — con veinte notas
    /// conviene tenerlas enteras, y con doscientas conviene saber que existen todas antes que
    /// conocer bien las primeras treinta y ninguna de las otras.
    /// </para>
    /// <para>
    /// Lo reemplazado no entra. Es evidencia para entender después por qué se equivocó, no algo
    /// según lo cual actuar.
    /// </para>
    /// </remarks>
    private string? DescribirCerebro()
    {
        List<BrainNote> notas;
        try
        {
            notas = [.. _brain.All().Where(nota => nota.Status == BrainStatus.Vigente)];
        }
        catch (Exception excepcion) when (excepcion is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (notas.Count == 0)
        {
            return null;
        }

        var enteras = notas.Sum(nota => nota.Title.Length + nota.Body.Length + 20) <= PresupuestoDelCerebro;

        var renglones = notas.Select(nota => enteras
            ? $"- {nota.Title} ({nota.Scope}, {nota.Confidence.ToString().ToLowerInvariant()}): {nota.Body}"
            : $"- {nota.Title} ({nota.Scope})");

        return "Lo que fuiste aprendiendo de él y de esta computadora:\n" + string.Join('\n', renglones);
    }

    /// <summary>
    /// Cuánto del cerebro entra en cada turno.
    /// </summary>
    /// <remarks>
    /// Se paga en cada pedido, así que no puede crecer sin techo. Cuatro mil caracteres es del orden
    /// de mil palabras: alcanza para varias decenas de notas enteras y sigue siendo chico al lado de
    /// la instrucción de sistema y las herramientas.
    /// </remarks>
    private const int PresupuestoDelCerebro = 4000;

    /// <summary>Pega los pedazos que haya, salteando los vacíos.</summary>
    private static string? Juntar(params string?[] partes)
    {
        var vivos = partes.Where(parte => !string.IsNullOrWhiteSpace(parte)).ToList();
        return vivos.Count == 0 ? null : string.Join("\n\n", vivos);
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

        // Los servidores MCP son ejecutables aparte y sobreviven a Viernes si Viernes no llega a
        // cerrarlos: un cierre forzado, un cuelgue, apagar la máquina. En la computadora del usuario
        // se contaron SETENTA Y SEIS procesos huérfanos del servidor de Spotify, uno por arranque,
        // algunos de veintidós horas. Esto los ata a la vida de este proceso a nivel del sistema, así
        // que se mueren con él aunque nadie corra ningún cierre. Ver ChildProcessJob.
        //
        // Se vuelve a llamar al reconectar porque una reconexión levanta un proceso nuevo, y un
        // proceso nuevo sin adoptar es un huérfano nuevo.
        // Sólo los ejecutables de los servidores, y no toda la descendencia: cuando el usuario le
        // pide que abra Spotify, esa aplicación también nace descendiente de Viernes, y atarla
        // significaría cerrársela de golpe al cerrar el asistente.
        var ejecutables = servers.Where(server => server.Enabled).Select(server => server.Command).ToList();

        provider.ConnectionChanged += (_, evento) =>
        {
            if (evento.State is McpConnectionState.Conectado or McpConnectionState.Recuperado)
            {
                ChildProcessJob.Adopt(ejecutables);
            }
        };

        var atados = ChildProcessJob.Adopt(ejecutables);

        _mcpProvider = provider;
        _mcpTools = tools;

        RuntimeTrace.Write(
            "mcp.listo",
            $"servidores={servers.Count(server => server.Enabled)} · herramientas={tools.Count} · " +
            $"atados={atados}");

        // Cero atados con servidores levantados es la falla silenciosa de todo esto: los procesos
        // quedan huérfanos igual que antes y no hay ningún otro síntoma hasta que alguien cuenta los
        // procesos meses después. Queda dicho con nombre y apellido.
        if (atados == 0 && ejecutables.Count > 0)
        {
            RuntimeTrace.Write(
                "mcp.sin.atar",
                $"no reconocí el proceso de {string.Join(", ", ejecutables.Select(System.IO.Path.GetFileName))} · " +
                "van a quedar huérfanos si Viernes no cierra bien");
        }
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

    /// <inheritdoc />
    public bool FollowsActiveMonitor { get; private set; }

    /// <inheritdoc />
    public async Task SetFollowActiveMonitorAsync(bool follow, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (FollowsActiveMonitor == follow)
        {
            return;
        }

        FollowsActiveMonitor = follow;
        await PersistVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            follow ? "Te sigo entre pantallas" : "Me quedo donde me dejes"));
    }

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

    /// <inheritdoc />
    public double OrbScale { get; private set; } = OrbScaleRange.Default;

    /// <inheritdoc />
    public async Task SetOrbScaleAsync(double scale, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var wanted = OrbScaleRange.Clamp(scale);

        // Medio punto porcentual: por debajo de eso no hay píxel que cambie y sí habría un archivo
        // escrito. La barra vuelve a pasar por acá al soltarla aunque no la hayan movido.
        if (Math.Abs(OrbScale - wanted) < 0.005)
        {
            return;
        }

        OrbScale = wanted;
        await PersistVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            $"Ahora mido {wanted * 100:0} %"));
    }

    public bool IsWakeWordDemo => _wakeWord?.IsDemoOnly ?? true;

    public string RecognitionProviderName => _recognitionProviderName;

    /// <summary>Cómo se llama el asistente en esta instalación.</summary>
    public string AssistantName => _identity.Name;

    /// <summary>
    /// Le cambia el nombre y, con él, la palabra que lo despierta, sin reiniciar.
    /// </summary>
    /// <remarks>
    /// El nombre toca cuatro cosas y sólo una es decoración. El prompt del sistema lo dice en la
    /// primera línea; las frases de activación se derivan de él; la bandeja y el título lo muestran.
    /// Guardar la preferencia y esperar al próximo arranque sería mentirle al usuario, que acaba de
    /// elegir cómo llamarlo y va a probar a llamarlo así en el segundo siguiente.
    /// <para>
    /// Lo único que no se puede hacer en el lugar es el oído: <see cref="ContinuousWakeListener"/>
    /// arma la gramática de SAPI cuando se lo construye. Por eso se lo cierra y se abre otro; el
    /// detector de voz —lo caro— se conserva.
    /// </para>
    /// </remarks>
    /// <summary>Saca una credencial del entorno del usuario y de este proceso.</summary>
    /// <remarks>No lanza: no poder tocar el entorno no puede tumbar un borrado del archivo.</remarks>
    private static void OlvidarDelEntorno(string nombre)
    {
        try
        {
            Environment.SetEnvironmentVariable(nombre, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(nombre, null);
        }
        catch (Exception excepcion) when (excepcion is System.Security.SecurityException
            or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Qué claves hay puestas. <b>Nunca devuelve los valores.</b></summary>
    public CredentialsState DescribeCredentials() => new(
        HasOpenRouter: LocalCredentials.Has(ViernesOptions.ApiKeyEnvironmentVariable),
        HasGoogle: LocalCredentials.Has("GOOGLE_API_KEY"),
        OpenRouterShadowed: LocalCredentials.IsShadowed(ViernesOptions.ApiKeyEnvironmentVariable));

    /// <summary>
    /// Guarda las claves y hace que surtan efecto sin reiniciar, cuando se puede.
    /// </summary>
    /// <remarks>
    /// <b>Cada clave va a un lugar distinto y no es un descuido.</b> La de OpenRouter va a las
    /// variables de entorno de la cuenta de Windows y no a ningún archivo, que es como estuvo
    /// siempre; la de Google va al archivo de claves, que es donde el usuario pidió que estuviera.
    /// Lo único que cambió con esta ventana es por dónde entran.
    /// <para>
    /// La de Google surte efecto <b>siempre</b>, en el acto: el cliente de voz la pide con una
    /// función en cada uso en vez de haberla guardado al arrancar, así que alcanza con releer el
    /// archivo.
    /// </para>
    /// <para>
    /// La de OpenRouter pide rehacer el orquestador, porque los clientes toman las opciones al
    /// construirse. Y rehacer el orquestador <b>sólo es seguro si no hay una conversación abierta</b>
    /// —está dicho en <c>InitializeAsync</c>, donde se hace por única vez «antes de que exista una
    /// conversación»—. Con una charla en curso, la clave queda guardada y se avisa que corre desde
    /// la próxima, en vez de rehacer el orquestador abajo de un turno o mentir que ya está.
    /// </para>
    /// <para>
    /// Ningún valor de clave entra acá en la bitácora, ni en un mensaje, ni en una excepción. Lo que
    /// se anota es cuál cambió.
    /// </para>
    /// </remarks>
    public async Task<CredentialsResult> SetCredentialsAsync(
        string? openRouterKey,
        string? googleKey,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var pendientes = new List<string>(2);
        var cambiadas = new List<string>(2);

        if (googleKey is not null)
        {
            var problema = LocalCredentials.Set("GOOGLE_API_KEY", googleKey);
            if (problema is not null)
            {
                return new CredentialsResult(Problem: problema);
            }

            // Borrar tiene que borrar, y la clave puede estar en DOS lugares: el archivo y el
            // entorno. Sacándola sólo del archivo, el respaldo del entorno la resucita y el botón
            // «Borrar» no borra nada —comprobado en esta máquina, que tiene GOOGLE_API_KEY en el
            // entorno—. Guardar una nueva no toca el entorno: para eso está el aviso de sombra.
            if (googleKey.Length == 0)
            {
                OlvidarDelEntorno("GOOGLE_API_KEY");
            }

            cambiadas.Add("google");
        }

        if (openRouterKey is not null)
        {
            try
            {
                var limpio = openRouterKey.Trim();
                var valor = limpio.Length == 0 ? null : limpio;

                // A la cuenta de Windows, para que sobreviva al reinicio; y al proceso, para que
                // ViernesOptions.FromEnvironment la vea sin cerrar sesión.
                Environment.SetEnvironmentVariable(
                    ViernesOptions.ApiKeyEnvironmentVariable, valor, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(ViernesOptions.ApiKeyEnvironmentVariable, valor);

                // Y si es un borrado, también del archivo: el archivo le gana al entorno, así que
                // dejarla ahí sería borrar la que no se usa y conservar la que sí.
                if (valor is null)
                {
                    LocalCredentials.Set(ViernesOptions.ApiKeyEnvironmentVariable, null);
                }

                cambiadas.Add("openrouter");
            }
            catch (Exception excepcion) when (excepcion is System.Security.SecurityException
                or UnauthorizedAccessException)
            {
                // El tipo de la excepción, no su mensaje: un mensaje puede arrastrar el valor.
                return new CredentialsResult(
                    Problem: $"No se pudo guardar la clave de OpenRouter ({excepcion.GetType().Name}).");
            }
        }

        if (cambiadas.Count == 0)
        {
            return new CredentialsResult();
        }

        RuntimeTrace.Write("claves.cambiadas", string.Join(" · ", cambiadas));

        if (openRouterKey is not null)
        {
            if (_conversationActive || _liveSession is not null)
            {
                pendientes.Add(
                    "la clave de OpenRouter quedó guardada, pero la charla que está abierta sigue " +
                    "con la anterior: la nueva entra en la próxima");
            }
            else
            {
                // Sin candado, y hay que decir por qué: acá no existe un candado de turno —el
                // orquestador se rehace una sola vez al arrancar, antes de que haya conversación—.
                // Lo que protege esto es la guarda de arriba: no hay charla abierta ni sesión en
                // vivo. Queda una ventana chica: que la palabra de despertar dispare justo entre la
                // comprobación y el reemplazo. Si pasa, ese turno usa el cliente anterior y el
                // siguiente ya usa el nuevo. Es el peor caso y es aceptable; inventar un candado
                // para esto sería agregar un punto de bloqueo nuevo en el camino de la voz.
                _options = ViernesOptions.FromEnvironment(assistantName: _identity.Name);
                _neuralVoice = new OpenRouterSpeechClient(
                    _httpClient,
                    _options,
                    SpeechSynthesisOptions.FromEnvironment());
                RebuildOrchestrator(_mcpTools);
            }
        }

        if (LocalCredentials.IsShadowed(ViernesOptions.ApiKeyEnvironmentVariable))
        {
            pendientes.Add(
                "el archivo de claves tiene otra clave de OpenRouter y ésa es la que se usa");
        }

        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            IsCloudConfigured ? "Claves guardadas." : "Claves guardadas · todavía no puedo pensar sin la de OpenRouter",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));

        return new CredentialsResult(
            Warning: pendientes.Count == 0 ? null : string.Join("; ", pendientes) + ".");
    }

    public async Task<AssistantRenameResult> SetAssistantNameAsync(
        string? name,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!AssistantIdentity.TryValidate(name, out var problem))
        {
            return new AssistantRenameResult(false, _identity.Name, problem);
        }

        var identity = new AssistantIdentity(name);

        // La salida rápida sólo vale si el renombrado ANTERIOR terminó entero.
        //
        // Acá alcanzaba con que el nombre coincidiera, y _identity se escribía antes de saber si
        // SaveAsync había andado. Con eso, un renombrado que fallaba a medias —el disco lleno, el
        // archivo tomado— dejaba _identity ya cambiado, y el reintento con el mismo nombre entraba
        // por esta puerta y contestaba «listo» sin hacer absolutamente nada. Reintentar es
        // exactamente lo que hace quien acaba de leer que algo quedó pendiente.
        if (string.Equals(identity.Name, _identity.Name, StringComparison.Ordinal) && !_renameLeftSomethingUndone)
        {
            return new AssistantRenameResult(true, identity.Name);
        }

        var previousName = _identity.Name;
        _identity = identity;
        _settings = _settings with { AssistantName = identity.Name };
        var saved = await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);

        // Las opciones llevan el nombre adentro y de ahí lo saca el prompt de fábrica. Se rehacen
        // antes de renombrar el orquestador para que las dos cosas digan lo mismo.
        _options = ViernesOptions.FromEnvironment(assistantName: identity.Name);
        var promptRenamed = await _orchestrator.TryRenameAsync(identity.Name, cancellationToken)
            .ConfigureAwait(false);

        var wakeRestarted = await RestartWakeListenerAsync(cancellationToken).ConfigureAwait(false);
        var warning = DescribeRenameLeftovers(saved.Succeeded, promptRenamed, wakeRestarted);

        // Se anota que quedó algo a medias para que reintentar con el mismo nombre vuelva a
        // intentarlo en vez de contestar que sí. Ver la guarda de arriba.
        _renameLeftSomethingUndone = warning is not null;

        RuntimeTrace.Write(
            "nombre.cambiado",
            $"{previousName} → {identity.Name} · guardado={saved.Succeeded} · prompt={promptRenamed} · " +
            $"oido={wakeRestarted}");

        Publish(new AssistantRuntimeUpdate(
            _lastVisualState,
            _isWakeWordEnabled && !IsMuted
                ? $"Ahora me llamo {identity.Name} · decí “{_wakeWord?.Phrases[0] ?? identity.WakePhrases[0]}”"
                : $"Ahora me llamo {identity.Name}",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));

        return new AssistantRenameResult(true, identity.Name, Problem: null, warning);
    }

    /// <summary>
    /// Junta en una sola frase lo que quedó sin surtir efecto, o <c>null</c> si no quedó nada.
    /// </summary>
    private string? DescribeRenameLeftovers(bool saved, bool promptRenamed, bool wakeRestarted) =>
        DescribeRenameLeftovers(
            saved,
            promptRenamed,
            wakeRestarted,
            liveSessionOpen: _liveSession is not null,
            handPickedPhrases: HasHandPickedWakePhrases());

    /// <summary>
    /// Qué quedó sin hacer al renombrar, en palabras que se le puedan mostrar a quien renombró.
    /// </summary>
    /// <remarks>
    /// Estática y sin estado a propósito: es lo único del renombrado que se puede probar sin
    /// micrófono, sin disco y sin modelo, y es además lo que el usuario lee. La versión de instancia
    /// de arriba sólo junta las cinco condiciones.
    /// <para>
    /// Devuelve <c>null</c> cuando no quedó nada pendiente, y de eso depende algo más que el
    /// mensaje: <see cref="SetAssistantNameAsync"/> usa ese <c>null</c> para saber si un reintento
    /// con el mismo nombre tiene que volver a intentarlo.
    /// </para>
    /// </remarks>
    internal static string? DescribeRenameLeftovers(
        bool saved,
        bool promptRenamed,
        bool wakeRestarted,
        bool liveSessionOpen,
        bool handPickedPhrases)
    {
        var pending = new List<string>(5);

        if (!saved)
        {
            // El nombre ya rige en esta sesión, pero el archivo no se escribió: al reiniciar vuelve
            // el anterior, y eso es lo que hay que decirle, no el error de disco.
            pending.Add("no se pudo guardar la preferencia, así que al reiniciar vuelve el nombre anterior");
        }

        if (!promptRenamed)
        {
            pending.Add("el prompt del sistema lo escribió alguien a mano y sigue con el nombre viejo");
        }

        if (!wakeRestarted)
        {
            pending.Add("el oído no volvió a arrancar: te va a escuchar con el nombre nuevo cuando reinicies");
        }

        if (liveSessionOpen)
        {
            // La instrucción de la sesión hablada se manda al abrirla y no se puede reescribir con la
            // sesión abierta. La próxima ya sale con el nombre nuevo —se arma cada vez—, ésta no.
            pending.Add("la charla en voz que está abierta sigue con el nombre anterior hasta que se corte");
        }

        if (handPickedPhrases)
        {
            // Frases escritas a mano ganan sobre el nombre a propósito —así está en
            // ViernesLocalSettings—, pero entonces renombrar no cambia con qué se lo despierta y eso
            // hay que decirlo, o el usuario prueba el nombre nuevo y no pasa nada.
            pending.Add("las frases de activación están puestas a mano, así que se lo sigue llamando igual");
        }

        return pending.Count == 0 ? null : string.Join("; ", pending) + ".";
    }

    private bool HasHandPickedWakePhrases() =>
        _settings.WakeWordPhrases is { Count: > 0 } ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VIERNES_WAKE_PHRASES"));

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
        FollowsActiveMonitor = _settings.FollowActiveMonitor;
        OrbScale = OrbScaleRange.Clamp(_settings.OrbScale);

        // Recién acá, con el archivo leído, se sabe qué voz eligió el usuario. Es el único lugar
        // donde se fija: las opciones con las que se construye el sintetizador.
        // Este SpeechService sólo sintetiza: el que reconoce es _recognition, que sale de
        // CreateRecognitionSelection. Los parciales de la transcripción se prenden allá, y prenderlos
        // acá no haría nada — costó un rato entenderlo, porque el nombre de la bandera no dice de
        // cuál de los dos oficios habla y acá era el único lugar donde aparecía apagada.
        _speechSynthesizer = new SpeechService(new SpeechServiceOptions
        {
            RecognitionCulture = "es-AR",
            SynthesisCulture = "es-AR",
            PreferredVoiceName = _settings.PreferredVoiceName,
            EmitPartialTranscriptions = false
        });

        var selection = CreateRecognitionSelection(_settings);
        _recognition = selection.Provider;
        _recognitionProviderName = selection.Provider.Info.DisplayName;
        _recognitionFallbackReason = selection.UsedFallback ? selection.FallbackReason : null;
        SubscribeRecognition(_recognition);

        // El modelo de Whisper se carga acá y no en la primera frase. El oído continuo entrega el WAV
        // entero y transcribe con la persona ya callada: sin esto, la primera vez que alguien dice el
        // nombre después de arrancar paga la carga del modelo como demora pura. Va en segundo plano
        // porque no hay nada del arranque que dependa de que termine.
        if (_recognition is WhisperSpeechRecognitionProvider precarga)
        {
            _ = Task.Run(async () =>
            {
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                var listo = await precarga.WarmUpAsync().ConfigureAwait(false);
                reloj.Stop();
                RuntimeTrace.Write("whisper.precargado", $"ok={listo} en {reloj.ElapsedMilliseconds} ms");
            });
        }

        _wakeWord = BuildWakeListener();
        SubscribeWakeWord(_wakeWord);

        await _recognition.SetMicrophoneMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);
        await _speechSynthesizer.SetMicrophoneMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);
        await _wakeWord.SetMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);

        var wakeStarted = false;
        if (_isWakeWordEnabled && !_isMuted)
        {
            // Con try, y no por prolijidad. Lo que sigue de acá para abajo es la mitad del arranque
            // —el vigía del escritorio, los recordatorios, la marca de inicializado— y una excepción
            // que suba desde el oído se la lleva entera y en silencio: el asistente queda abierto,
            // dibujado y sin nada andando. Ya pasó con una NotSupportedException del caño de audio y
            // no dejó una sola línea en la bitácora. Que no arranque el oído es quedarse sin wake;
            // que no arranque el resto es quedarse sin asistente.
            try
            {
                var wakeResult = await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
                wakeStarted = wakeResult.Succeeded;
                if (!wakeStarted)
                {
                    RuntimeTrace.Write("wake.no.arranco", $"{wakeResult.ErrorCode} · {wakeResult.ErrorMessage}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RuntimeTrace.Write("wake.excepcion", $"{exception.GetType().Name} · {exception.Message}");
            }

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

        // Por dónde va a ir la primera charla, dicho antes de que alguien hable. Sale del mismo
        // router que la decide de verdad, así que no puede desincronizarse de lo que después pasa;
        // y nunca lleva la clave adentro, sólo si la hay.
        RuntimeTrace.Write("voz.camino.inicial", DescribeVoiceRoute().ToString());
        var providerStatus = selection.Availability.IsAvailable
            ? $"{_recognitionProviderName} listo"
            : "entrada de voz no disponible";
        if (!string.IsNullOrWhiteSpace(_recognitionFallbackReason))
        {
            providerStatus += " · respaldo SAPI";
        }

        // El vigía de fondo. Arranca enseguida porque una pregunta sin contestar de anteayer tiene
        // que verse desde el primer cuadro, no dentro de cinco segundos.
        _ambientTimer = new System.Threading.Timer(
            _ => _ = RefreshAmbientAsync(),
            null,
            TimeSpan.Zero,
            AmbientPeriod);

        var wakeStatus = _isMuted
            ? "voz silenciada"
            : wakeStarted
                ? $"wake demo activo · decí “{_wakeWord.Phrases[0]}”"
                : "PTT disponible";
        Publish(new AssistantRuntimeUpdate(
            Resting(),
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
        // Lo escrito entra por acá sin pasar por AddConversationTurn, que es del camino hablado: sin
        // esto, la mitad de las charlas quedaría con las respuestas y sin las preguntas.
        if (!spoken)
        {
            Charla()?.Note(ChatVoice.Persona, text);
        }

        var respuesta = await SendCoreAsync(text, spoken, cancellationToken).ConfigureAwait(false);
        NoteAssistantTurn(respuesta);
        return respuesta;
    }

    /// <summary>
    /// El envío de siempre. Lo de afuera es sólo dejar la charla escrita.
    /// </summary>
    /// <remarks>
    /// Se partió en dos porque acá adentro hay ocho salidas distintas —ocupada, cortada, presupuesto,
    /// herramienta local, la respuesta del modelo— y anotar la respuesta en cada una es garantizar
    /// que la próxima que se agregue se olvide. Una sola envoltura anota todas.
    /// </remarks>
    private async Task<string> SendCoreAsync(string text, bool spoken, CancellationToken cancellationToken)
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
            // Escribir cierra la charla hablada, y no es una limitación técnica: es la misma regla
            // que ya vale en la interfaz —escribir es la prueba de que este turno se lee, no se
            // escucha— y además lo único seguro. Con la sesión en vivo abierta, la voz de siempre
            // saldría por los parlantes con su micrófono escuchando: se oiría a sí misma, el
            // servidor lo tomaría como que alguien le habló encima, y se contestaría sola.
            if (IsLiveConversation)
            {
                // Sin keepChat, esto partía la charla al medio y perdía la respuesta. La pregunta ya
                // se anotó arriba, en SendAsync; si acá se cierra la charla, cuando vuelva la
                // respuesta el campo ya es nulo y la respuesta se descarta sin dejar rastro. El .md
                // terminaba con una pregunta sin contestar y la destilación salía a aprender de esa
                // charla trunca.
                //
                // Y no es sólo evitar el defecto: seguir por escrito es LA MISMA conversación
                // siguiendo en otro medio. Partirla en dos archivos sería mentir sobre lo que pasó.
                await EndConversationAsync(
                    "Seguimos por escrito",
                    quiet: true,
                    keepChat: true,
                    CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await PauseWakeWordAsync(turn.Token).ConfigureAwait(false);
            CancelSpeechSafely();
            _neuralPlayer.Stop();
            await SilenceVoiceAsync(turn.Token).ConfigureAwait(false);
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
            await ConsumeRestRequestAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Calla la voz del sistema. Antes de que existan las preferencias no hay ninguna que callar.
    /// </summary>
    private Task SilenceVoiceAsync(CancellationToken cancellationToken) =>
        _speechSynthesizer?.StopSpeakingAsync(cancellationToken) ?? Task.CompletedTask;

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

            // Pedir permiso no es «revisá esto»: se inclina hacia el usuario porque quiere hacer
            // algo y no puede sin que le digan que sí. «Revisar» queda para lo que ya pasó.
            Publish(new AssistantRuntimeUpdate(
                AssistantVisualState.AskingPermission,
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
                : Resting();
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
                AssistantVisualState.AskingPermission,
                "Esperando tu decisión · no se realizó la acción",
                outcome.Text,
                Confirmation: confirmation));
        }
        else
        {
            var failed = outcome.ToolResult?.Status is ToolExecutionStatus.Failed or ToolExecutionStatus.Denied;
            Publish(new AssistantRuntimeUpdate(
                failed ? AssistantVisualState.Error : Resting(),
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
                Resting(),
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
        await SilenceVoiceAsync(cancellationToken).ConfigureAwait(false);
            _orchestrator.SetListening(true);
            var result = await _recognition.StartPushToTalkAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _orchestrator.SetListening(false);
                Publish(new AssistantRuntimeUpdate(
                    result.ErrorCode == SpeechErrorCode.Cancelled
                        ? Resting()
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
                ? Resting()
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
                Resting(),
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
            MicrophoneActive: false,
            Dictation: _dictation.Settle(transcript),
            DictationRecovered: _dictation.RecoveredSpan));
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

        // Sin proteger, esta cancelación tira ObjectDisposedException si pega en la ventana en que
        // SpeakCoreAsync desecha el origen anterior y asigna el nuevo. Y como silenciar se llama
        // fire-and-forget desde la interfaz, esa excepción se perdía y las dos líneas de abajo nunca
        // corrían: apretabas silenciar y la voz seguía hablando.
        CancelSpeechSafely();
        _neuralPlayer.Stop();

        // Silenciar es silenciar todo, venga la voz de donde venga. Sin esto, apretar silenciar
        // durante una charla en vivo callaba la voz de siempre —que no estaba sonando— y dejaba
        // hablando a la única que sí.
        SilenceLive();

        // Y silenciar CIERRA la charla en vivo, no la calla nada más. Con sólo callarla, la sesión
        // quedaba abierta y el micrófono seguía capturando y subiendo a la nube mientras esta misma
        // función publicaba «micrófono apagado». Que la pantalla diga una cosa y el micrófono haga
        // otra es lo peor que puede hacer un asistente que vive escuchando, y no es un detalle de
        // implementación: es lo único que el usuario tiene para confiar en el botón.
        await StopLiveAsync("silenciada").ConfigureAwait(false);

        await SilenceVoiceAsync(cancellationToken).ConfigureAwait(false);
        _orchestrator.SetListening(false);
        Publish(new AssistantRuntimeUpdate(
            Resting(),
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
            Resting(),
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
                Resting(),
                _listenWhileHidden && _isWakeWordEnabled && !IsMuted
                    ? $"Oculto y atento · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
                    : "Widget oculto · escucha detenida",
                MicrophoneActive: IsAnyMicrophoneActive(),
                WakeWordEnabled: _isWakeWordEnabled));
            return;
        }

        await ResumeWakeWordAsync(cancellationToken).ConfigureAwait(false);
        Publish(new AssistantRuntimeUpdate(
            Resting(),
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

        // Una acción confirmada puede ser «descansar»: acá adentro se ejecutan herramientas sin que
        // haya un turno de SendAsync que después limpie lo que dejen pedido.
        Interlocked.Exchange(ref _confirmActive, 1);
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
                succeeded ? Resting() : AssistantVisualState.Attention,
                succeeded ? "Acción completada" : "Acción bloqueada por la política segura",
                result.Message,
                ClearConfirmation: true));
            await SpeakIfEnabledAsync(result.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _confirmActive, 0);
            await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            await ConsumeRestRequestAsync().ConfigureAwait(false);
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
                Resting(),
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

        // El registro se decide una sola vez y con el texto entero, y de acá salen las dos cosas: el
        // timbre con el que se sintetiza y la cara con la que el orbe lo acompaña. Calcularlo dos
        // veces sería garantizar que se separen la primera vez que alguien toque uno de los dos.
        var moment = VoiceRegister.Guess(spokenText);

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Speaking,
            "Hablando · podés silenciarme cuando quieras",
            Mood: OrbMoods.FromVoice(moment)));

        _speechCancellation?.Dispose();
        _speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _speechCancellation.Token;

        RuntimeTrace.Write("voz.inicio", $"{spokenText.Length} caracteres · neural={_neuralVoice.IsAvailable}");
        var spoke = await TrySpeakNeuralAsync(spokenText, moment, token).ConfigureAwait(false);
        var neuralFailure = spoke ? null : _neuralVoice.LastFailure;
        RuntimeTrace.Write("voz.neural", spoke ? "sonó" : $"falló · {neuralFailure ?? "sin motivo"}");

        if (!spoke && !token.IsCancellationRequested && _speechSynthesizer is { } synthesizer)
        {
            // La voz de Windows queda como red: peor timbre, pero siempre disponible y sin red.
            var result = await synthesizer.SpeakAsync(spokenText, token).ConfigureAwait(false);
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
            spoke || token.IsCancellationRequested ? Resting() : AssistantVisualState.Error,
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

        // La clave alcanza para saber qué la disparó; el texto es una respuesta del asistente y no
        // va a la bitácora. Ver la línea de memoria.observada, que tenía el mismo problema.
        RuntimeTrace.Write("proactivo", observation.Key);

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

        // Y la voz de la sesión en vivo, que sale por otro dispositivo y no la para ninguna de las
        // dos líneas de arriba. Es sincrónico a propósito: acá cada milisegundo se oye.
        SilenceLive();

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

        // El freno cierra la charla sin pasar por EndConversationAsync, así que vacía él los turnos:
        // si no, lo dicho antes del pánico reaparecía en la destilación de la charla siguiente. Y
        // cierra también la charla escrita, por lo mismo.
        _ = TakeConversationTurns();
        CerrarCharla();
        RuntimeTrace.Write("panico", "corte de emergencia por atajo global");

        _ = Task.Run(async () =>
        {
            try
            {
                await StopLiveAsync("freno de emergencia").ConfigureAwait(false);
                await SilenceVoiceAsync(CancellationToken.None).ConfigureAwait(false);
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
            Resting(),
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
        _ = SilenceVoiceAsync(CancellationToken.None);
    }

    /// <summary>
    /// Habla por oraciones: sintetiza la siguiente mientras suena la actual, así el primer sonido
    /// llega en cuanto está lista la primera frase en vez de esperar la respuesta entera.
    /// </summary>
    private async Task<bool> TrySpeakNeuralAsync(
        string text,
        VoiceMoment moment,
        CancellationToken cancellationToken)
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

        // El registro llega decidido de afuera, con el texto entero y una sola vez: partirlo por
        // oraciones y juzgar cada tramo por separado haría que una misma respuesta cambiara de humor
        // en el medio, que es peor que no variar nunca. Y viene de afuera para que sea el mismo con
        // el que el orbe pone la cara.
        try
        {
            var pending = SpeakChunkAsync(chunks[0], moment, cancellationToken);
            for (var index = 0; index < chunks.Count; index++)
            {
                var audio = await pending.ConfigureAwait(false);
                if (audio is null)
                {
                    // El primer tramo define si hay voz neural; a mitad de camino se corta y listo.
                    return index > 0;
                }

                pending = index + 1 < chunks.Count
                    ? SpeakChunkAsync(chunks[index + 1], moment, cancellationToken)
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
    /// Sintetiza un tramo con la voz de Google, y cae a la de OpenRouter si no se puede.
    /// </summary>
    /// <remarks>
    /// El orden importa: Google es la elegida —Aoede, y con registro que cambia según el momento—
    /// pero su modelo devuelve 503 por demanda cada tanto. Caer al proveedor anterior en vez de
    /// quedarse muda convierte un pico de demanda ajeno en una frase que suena un poco distinta,
    /// que es infinitamente mejor que silencio.
    /// </remarks>
    private async Task<SynthesizedSpeech?> SpeakChunkAsync(
        string chunk,
        VoiceMoment moment,
        CancellationToken cancellationToken)
    {
        if (_googleVoice.IsConfigured)
        {
            var google = await _googleVoice
                .SynthesizeAsync(chunk, moment, cancellationToken)
                .ConfigureAwait(false);

            if (google is not null)
            {
                return google;
            }

            RuntimeTrace.Write("voz.google.fallo", _googleVoice.LastFailure ?? "sin detalle");
        }

        return await _neuralVoice.SynthesizeAsync(chunk, cancellationToken).ConfigureAwait(false);
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

            if (_speechSynthesizer is not null)
            {
                await _speechSynthesizer.SetMicrophoneMutedAsync(isMuted).ConfigureAwait(false);
            }

            if (_wakeWord is not null)
            {
                await _wakeWord.SetMutedAsync(isMuted).ConfigureAwait(false);
            }

            if (isMuted)
            {
                // Silenciarse a propósito no es sordera: el micrófono está apagado porque se lo
                // pidieron, no porque no entregue audio.
                Volatile.Write(ref _deaf, 0);

                // Mute sigue siendo el corte duro: también cierra la conversación abierta.
                await EndConversationAsync("Voz silenciada · conversación cerrada", CancellationToken.None)
                    .ConfigureAwait(false);
                _wakeHandoffCancellation?.Cancel();
                await SilenceVoiceAsync(CancellationToken.None).ConfigureAwait(false);
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
                Resting(),
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

        // Autorizar gasto es autorización, no revisión: la única forma de seguir es que digas que sí.
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.AskingPermission,
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
    public Task StartConversationAsync(CancellationToken cancellationToken) =>
        StartConversationAsync(opening: null, cancellationToken);

    /// <summary>
    /// Abre la conversación con una frase ya dicha.
    /// </summary>
    /// <param name="opening">
    /// Lo que la persona dijo antes de que hubiera conversación. Lo trae el oído continuo, que ya
    /// grabó la frase entera —incluido lo anterior al nombre— mientras el resto todavía no existía.
    /// Con <c>null</c> se abre como siempre: saluda y espera.
    /// </param>
    /// <remarks>
    /// Que la primera frase entre por acá es lo que evita el turno perdido. Sin esto, el oído
    /// entiende «Viernes creame una carpeta», abre la conversación, y la conversación pregunta «¿qué
    /// necesitás?» sobre un pedido que ya se hizo.
    /// <para>
    /// Devuelve si la abrió, y eso no es adorno: las tres salidas de abajo son silenciosas, y quien
    /// llama suele haber cerrado el oído antes para dejarle el micrófono. Sin la respuesta, el que
    /// llama la daba por abierta y no reabría nada.
    /// </para>
    /// </remarks>
    private Task<bool> StartConversationAsync(string? opening, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_conversationActive || IsMuted || _recognition is null)
        {
            RuntimeTrace.Write(
                "conversacion.rechazada",
                $"activa={_conversationActive} muted={IsMuted} reconocedor={_recognition is not null}");
            return Task.FromResult(false);
        }

        RuntimeTrace.Write("conversacion.abierta", opening is null ? "sin frase" : "con la frase ya dicha");

        // Cada vez que sale de reposo se abre una charla, y queda escrita mientras pasa. Antes no
        // quedaba nada: los turnos vivían en una lista y se tiraban al cerrar.
        AbrirCharla();

        _conversationActive = true;
        _conversationCancellation?.Dispose();
        _conversationCancellation = new CancellationTokenSource();

        Publish(new AssistantRuntimeUpdate(
            opening is null ? AssistantVisualState.Listening : AssistantVisualState.Thinking,
            opening is null
                ? "En conversación · decime «listo» para cortar"
                : "Entendido · procesando…",
            opening is null ? "Te escucho." : null,
            MicrophoneActive: opening is null));

        _ = RunChosenConversationAsync(_conversationCancellation.Token, opening);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Elige por cuál de los dos caminos va esta charla y arranca el que corresponda.
    /// </summary>
    /// <remarks>
    /// <b>Es la única frontera entre los dos caminos.</b> El de siempre —reconocer acá, pensar en la
    /// nube, sintetizar acá— y el de la sesión en vivo no se mezclan en ningún otro lado: si el
    /// nuevo no se puede abrir, no dejó nada abierto y se sigue con el de siempre como si nunca se
    /// hubiera intentado. El motivo de la elección queda escrito en la bitácora, siempre, incluso
    /// cuando la elección es la de siempre.
    /// </remarks>
    private async Task RunChosenConversationAsync(CancellationToken cancellationToken, string? opening)
    {
        try
        {
            if (await TryStartLiveConversationAsync(cancellationToken).ConfigureAwait(false))
            {
                // La frase que ya se dijo entra por el mismo canal, escrita: el audio de esa frase lo
                // grabó el oído continuo y la sesión en vivo no lo escuchó nunca. Mandarla como texto
                // es lo único que la sesión puede hacer con audio que no pasó por ella.
                if (!string.IsNullOrWhiteSpace(opening) && _liveSession is not null)
                {
                    await _liveSession.SendTextAsync(opening, cancellationToken).ConfigureAwait(false);
                }

                // A partir de acá manda el servidor: no hay bucle que correr de este lado. La charla
                // la cierra el usuario, el mute, el freno o una caída de la sesión.
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            // Que el camino nuevo se rompa de una forma que no se previó no puede dejar mudo al
            // asistente: se anota y se sigue por el de siempre, que es lo que ya funcionaba.
            RuntimeTrace.Write("vivo.arranque.excepcion", $"{exception.GetType().Name} · {exception.Message}");
        }

        await RunConversationLoopAsync(cancellationToken, opening).ConfigureAwait(false);
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
    public Task EndConversationAsync(string reason, bool quiet, CancellationToken cancellationToken) =>
        EndConversationAsync(reason, quiet, keepChat: false, cancellationToken);

    /// <summary>
    /// Cierra la conversación abierta.
    /// </summary>
    /// <param name="reason">Por qué se cierra. Va a la bitácora.</param>
    /// <param name="quiet">Si se cierra sin decir nada.</param>
    /// <param name="keepChat">
    /// Si la charla escrita sigue abierta. Sólo lo pide el paso de hablar a escribir, que no es un
    /// final sino la misma conversación cambiando de medio: cerrarla ahí perdería la respuesta que
    /// todavía no llegó, y dejaría dos archivos donde hubo una sola charla.
    /// </param>
    /// <param name="cancellationToken">Para cortar la espera.</param>
    public async Task EndConversationAsync(
        string reason,
        bool quiet,
        bool keepChat,
        CancellationToken cancellationToken)
    {
        // Cerrar vacía los turnos, sin excepción.
        //
        // Sólo los limpiaba LearnFromConversationAsync, que corre en dos de los muchos caminos de
        // cierre. Por los demás —la herramienta «descansar», mute, un fallo del dispositivo, una
        // excepción del bucle— los turnos quedaban en la lista y se arrastraban a la destilación de
        // la charla siguiente: Viernes «aprendía» de una conversación mezclada con otra que ya había
        // terminado. Los caminos que sí destilan se llevan los turnos antes de llamar acá.
        var abandoned = TakeConversationTurns();
        if (abandoned.Count > 0)
        {
            RuntimeTrace.Write("conversacion.turnos.descartados", $"{abandoned.Count} · {reason}");
        }

        // Se cierra por TODOS los caminos de cierre y no sólo por los que destilan, que es el mismo
        // error que este método vino a arreglar con los turnos: los caminos raros —un fallo del
        // dispositivo, mute, la herramienta «descansar»— son justamente los que hay que poder releer.
        if (!keepChat)
        {
            CerrarCharla();
        }

        if (!_conversationActive)
        {
            // Aunque la charla ya se haya dado por cerrada, el camino nuevo puede seguir con el
            // micrófono y el parlante tomados: cerrarlo acá también es lo que impide que un cierre
            // por un camino raro deje la sesión en vivo escuchando de fondo.
            await StopLiveAsync(reason).ConfigureAwait(false);
            return;
        }

        RuntimeTrace.Write("conversacion.cerrada", reason);
        _conversationActive = false;
        await StopLiveAsync(reason).ConfigureAwait(false);
        _conversationCancellation?.Cancel();

        if (_recognition?.IsMicrophoneActive == true)
        {
            await _recognition.CancelPushToTalkAsync(cancellationToken).ConfigureAwait(false);
        }

        _dictation.Clear();
        Publish(new AssistantRuntimeUpdate(
            Resting(),
            reason,
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled,
            ClearSteps: true,
            ClearItems: true,
            ClearDictation: true,
            Quiet: quiet));

        await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Escucha, responde y vuelve a escuchar. No se corta por silencio: sólo por una frase de
    /// cierre, por mute o porque el dispositivo falle. Ese es el punto de tener conversación.
    /// </summary>
    private async Task RunConversationLoopAsync(
        CancellationToken cancellationToken,
        string? opening = null)
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

            if (string.IsNullOrWhiteSpace(opening))
            {
                await GreetAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Con el pedido ya hecho, saludar es hacerla esperar para decirle algo que no
                // preguntó. Se contesta y listo.
                AddConversationTurn(opening);
                await SendAsync(opening, spoken: true, cancellationToken).ConfigureAwait(false);
            }

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
                        // El dispositivo no entrega: eso es sorda, y hay que decirlo mientras se
                        // reintenta. Seguir dibujando «te escucho» es prometer una escucha que no hay.
                        Volatile.Write(ref _deaf, 1);
                        Publish(new AssistantRuntimeUpdate(
                            AssistantVisualState.Deaf,
                            "No te oigo · reintentando",
                            MicrophoneActive: false));

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

                // La captura salió: el dispositivo entrega. Aunque no haya venido texto, oye.
                Volatile.Write(ref _deaf, 0);

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

                        // Los turnos se retiran antes de cerrar: el cierre los descarta, y acá se
                        // quieren para destilar.
                        var silenced = TakeConversationTurns();
                        await EndConversationAsync("Cerré por silencio", quiet: true, CancellationToken.None)
                            .ConfigureAwait(false);
                        await LearnFromConversationAsync(silenced).ConfigureAwait(false);
                        return;
                    }

                    Publish(new AssistantRuntimeUpdate(
                        AssistantVisualState.Listening,
                        "Sigo escuchando · decime «listo» para cortar",
                        MicrophoneActive: true));
                    continue;
                }

                consecutiveSilences = 0;

                // La frase quedó cerrada: lo que estaba en itálica pasa a firme y ahí se queda
                // mientras piensa. Sin esto, la última palabra sigue temblando durante todo el turno.
                PublishDictation(_dictation.Settle(transcript));

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

                    // Los turnos se retiran antes de cerrar, porque el cierre los descarta.
                    var closed = TakeConversationTurns();

                    // En modo silencioso: se despide y se encoge. Antes cerraba dejando la despedida
                    // en la burbuja siete segundos, y esos siete segundos con el desplegable abierto
                    // después de pedirle que pare se leen como que no hizo caso.
                    await EndConversationAsync("Conversación cerrada", quiet: true, CancellationToken.None)
                        .ConfigureAwait(false);
                    await LearnFromConversationAsync(closed).ConfigureAwait(false);
                    return;
                }

                AddConversationTurn(transcript);

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
    /// Se lleva lo que dijo el usuario en la charla y deja la lista vacía, en un solo paso.
    /// </summary>
    /// <remarks>
    /// Leer y vaciar tienen que ser la misma operación: si fueran dos, cualquier cierre que hiciera
    /// una y se olvidara de la otra volvería a dejar turnos viejos para la charla siguiente, que es
    /// justamente el error que esto viene a cerrar.
    /// </remarks>
    /// <summary>
    /// Anota lo que dijo el usuario, bajo el mismo candado con el que se vacía.
    /// </summary>
    /// <remarks>
    /// Agregar estaba fuera del candado y vaciar adentro: el bucle de conversación corre en otra
    /// tarea que la del cierre, y una lista sin sincronizar que se recorre mientras se le agrega
    /// puede tirar en cualquiera de las dos puntas.
    /// </remarks>
    private void AddConversationTurn(string transcript)
    {
        lock (_confirmationGate)
        {
            _conversationTurns.Add(transcript);
        }

        Charla()?.Note(ChatVoice.Persona, transcript);
    }

    /// <summary>Anota en la charla escrita lo que contestó ella.</summary>
    /// <remarks>
    /// Va aparte de <see cref="AddConversationTurn"/> porque son dos cosas distintas que hasta ahora
    /// se confundían en una: aquella lista es <em>lo que pidió la persona</em>, y existe para
    /// destilar. Lo que ella contesta nunca se guardaba en ningún lado, así que ni siquiera había
    /// media charla que releer.
    /// </remarks>
    private void NoteAssistantTurn(string? reply) =>
        Volatile.Read(ref _chat)?.Note(ChatVoice.Ella, reply);

    /// <summary>
    /// La charla escrita abierta, abriéndola si hacía falta.
    /// </summary>
    /// <remarks>
    /// Se abre sola porque «sale de reposo» tiene más puertas de las que uno se acuerda: la palabra
    /// de activación, tocar el orbe, el panel de escribir, un recordatorio que la despierta. Buscar
    /// cada puerta y abrir la charla en todas es garantizar que la próxima que se agregue no la
    /// abra, y ahí lo que se pierde es una charla entera sin que nada avise.
    /// </remarks>
    private ChatArchive? Charla()
    {
        if (Volatile.Read(ref _chat) is { } abierta)
        {
            return abierta;
        }

        AbrirCharla();
        return Volatile.Read(ref _chat);
    }

    /// <summary>Abre la charla escrita, o no hace nada si algo falla.</summary>
    /// <remarks>
    /// No poder escribir la charla no puede impedir tenerla: un disco lleno, una carpeta sin
    /// permiso, un antivirus. Se pierde el archivo y se anota por qué.
    /// </remarks>
    private void AbrirCharla()
    {
        // Cerrar la anterior primero. Pisar el campo la dejaba abierta para siempre: sin renglón de
        // cierre, sin destilar, y con su hilo y su cola vivos hasta que muriera el proceso. Pasa de
        // verdad — alcanza con que un camino de cierre no haya pasado por CerrarCharla.
        CerrarCharla();

        try
        {
            Volatile.Write(ref _chat, ChatArchive.Open(CarpetaDeCharlas, RutaDeVoz));
        }
        catch (Exception excepcion) when (excepcion is System.IO.IOException or UnauthorizedAccessException)
        {
            RuntimeTrace.Write("charla.no.se.pudo.abrir", excepcion.GetType().Name);
        }
    }

    /// <summary>Cierra la charla escrita, si había una.</summary>
    /// <remarks>
    /// El campo se pone en nulo <em>antes</em> de cerrar: un turno que llegue tarde —el hilo del
    /// socket no se detiene en el mismo instante que el cierre— tiene que caer en el vacío y no en
    /// una charla a medio cerrar.
    /// </remarks>
    private void CerrarCharla()
    {
        var charla = Interlocked.Exchange(ref _chat, null);
        if (charla is null)
        {
            return;
        }

        charla.Note(ChatVoice.Nota, "— se cerró la charla —");
        charla.Close();

        if (charla.Turns <= 1)
        {
            return;
        }

        RuntimeTrace.Write("charla.escrita", $"turnos={charla.Turns}");

        // Destilar es lento —es un pedido al modelo— y cerrar la charla no puede esperarlo: quien
        // cierra puede ser el hilo de la interfaz, el del socket o el del apagado. Sale por una
        // tarea con su propio try, que es lo que este archivo ya hace en todos los demás cierres.
        var archivo = charla.Path;
        _ = Task.Run(() => DestilarCharlaAsync(archivo));
    }

    /// <summary>
    /// Lee la charla que acaba de terminar y guarda en el cerebro lo que valga la pena.
    /// </summary>
    /// <remarks>
    /// <b>Es lo que convierte el archivo de charlas en aprendizaje.</b> Sin esto quedan un montón de
    /// transcripciones y una carpeta vacía al lado.
    /// <para>
    /// Se le manda la charla <em>entera, con los dos lados</em>, y no sólo lo que dijo la persona. La
    /// destilación de antes miraba únicamente los pedidos, así que no podía enterarse de nada de lo
    /// que pasó al intentarlos: qué falló, qué había que hacer primero, qué corrigió el usuario
    /// después de una respuesta. Eso es justamente lo reutilizable.
    /// </para>
    /// <para>
    /// Y se le manda lo que ya sabe. Sin eso vuelve a aprender lo mismo cada vez y el cerebro se
    /// llena de la misma nota escrita de veinte formas; con eso puede decir «esto reemplaza a
    /// aquello», que es como se corrige en vez de acumular.
    /// </para>
    /// <para>
    /// Todo lo que sale de acá es lo que dijo un modelo sobre una charla, así que entra con confianza
    /// media como mucho salvo que la persona lo haya dicho derecho. Subirla porque una herramienta no
    /// dio error es exactamente lo que la skill del usuario prohíbe, y con razón.
    /// </para>
    /// </remarks>
    private async Task DestilarCharlaAsync(string chatPath)
    {
        if (_isDisposed || !IsCloudConfigured)
        {
            return;
        }

        try
        {
            string charla;
            try
            {
                charla = await System.IO.File.ReadAllTextAsync(chatPath).ConfigureAwait(false);
            }
            catch (Exception excepcion) when (excepcion is System.IO.IOException or UnauthorizedAccessException)
            {
                RuntimeTrace.Write("cerebro.no.se.pudo.leer", excepcion.GetType().Name);
                return;
            }

            if (charla.Length > 12_000)
            {
                // Lo último es lo que se destila mejor: ahí están las correcciones y el resultado.
                charla = "…\n" + charla[^12_000..];
            }

            var yaSabe = DescribirCerebro() ?? "Todavía no sabés nada de él.";

            var pedido = $$"""
                Acabás de terminar esta conversación. Extraé lo que te sirva para la próxima vez.

                {{yaSabe}}

                Reglas:
                - Como mucho TRES notas. Ninguna es una respuesta válida y es la más común.
                - Nada efímero: un pedido puntual, una fecha, algo que ya hiciste. Sólo lo que
                  seguiría siendo cierto dentro de un mes.
                - No inventes: si algo no está dicho ni pasó en la charla, no va.
                - Si algo contradice lo que ya sabías, poné el título exacto de la nota vieja en
                  "reemplaza" en vez de escribir una nota nueva parecida.
                - No generalices una preferencia a otros contextos sin evidencia de esos contextos.
                - confianza "alta" sólo si él lo dijo derecho. Que algo no haya fallado no es
                  evidencia de nada.

                Contestá SÓLO un arreglo JSON, sin explicaciones y sin ```:
                [{"tipo":"preferencia|aplicacion|procedimiento|correccion|capacidades",
                   "titulo":"una línea corta",
                   "alcance":"cuándo vale",
                   "confianza":"baja|media|alta",
                   "cuerpo":"una o dos frases",
                   "reemplaza":"título exacto de la nota vieja, o vacío"}]

                La conversación:
                {{charla}}
                """;

            ConversationTurnResult salida;
            Volatile.Write(ref _distilling, 1);
            try
            {
                salida = await _orchestrator.ProcessAsync(pedido, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _distilling, 0);
            }

            if (salida.IsLocalMode || string.IsNullOrWhiteSpace(salida.Text))
            {
                return;
            }

            var evidencia = new[] { "charlas/" + System.IO.Path.GetFileName(chatPath) };
            var guardadas = _brain.Learn(salida.Text, evidencia);

            // Sin lo que aprendió: la bitácora se pega en reportes. Cuántas alcanza para saber si
            // esto está corriendo, que es lo único que no se puede ver de otra forma.
            RuntimeTrace.Write("cerebro.destilado", $"notas={guardadas}");
        }
        catch (Exception excepcion)
        {
            RuntimeTrace.Write("cerebro.excepcion", excepcion.GetType().Name);
        }
    }


    /// <summary>Dónde viven las charlas.</summary>
    /// <remarks>
    /// Adentro de «cerebro» y no sueltas en la carpeta de datos: lo que viene después —lo que ella
    /// destile y organice— vive al lado, y la idea es que el usuario pueda abrir una sola carpeta y
    /// tener ahí todo lo que ella sabe.
    /// </remarks>
    private static string CarpetaDeCharlas => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "cerebro",
        "charlas");

    /// <summary>
    /// Por dónde va a ir la charla, para la cabecera del archivo.
    /// </summary>
    /// <remarks>
    /// Se pregunta por la ruta que <em>tomaría</em> una charla abierta ahora, y no por
    /// <c>IsLiveConversation</c>. Esto corre al abrir, antes de que la sesión hablada exista, así
    /// que aquella bandera todavía está en falso: la cabecera decía «escribiendo» en TODAS las
    /// charlas, también en las habladas, y la palabra «hablando» no se escribía nunca.
    /// <para>
    /// <see cref="DescribeVoiceRoute"/> existe justamente para esto: contesta por dónde iría sin
    /// abrir nada.
    /// </para>
    /// </remarks>
    private string RutaDeVoz
    {
        get
        {
            if (IsLiveConversation)
            {
                return "hablando";
            }

            try
            {
                return DescribeVoiceRoute().IsLive ? "hablando" : "escribiendo";
            }
            catch (Exception excepcion) when (excepcion is not OperationCanceledException)
            {
                // Armar la sesión para preguntarle puede fallar; no poder etiquetar la cabecera no
                // puede costar la charla entera.
                return "escribiendo";
            }
        }
    }

    /// <summary>
    /// Reemplaza el último turno anotado por la frase entera, porque era el mismo pedido.
    /// </summary>
    /// <remarks>
    /// Sólo lo usa la sesión hablada, y por una razón que el camino escrito no tiene: allá un turno
    /// lo cierra el Enter y no hay ambigüedad, y acá lo cierra el detector de voz del servidor apenas
    /// junta el silencio configurado. Una pausa para respirar, o una interrupción, parten en dos algo
    /// que se dijo de corrido; anotarlo como dos turnos hace que la destilación de la charla lea dos
    /// pedidos donde hubo uno, cada uno con media oración.
    /// <para>
    /// Se anota en cuanto llega el primer tramo y se corrige después, en vez de esperar a que el
    /// pedido cierre: si la charla se cierra con la frase a medio decir, lo dicho ya está anotado.
    /// </para>
    /// </remarks>
    private void AmendLastConversationTurn(string transcript, string fragment)
    {
        // A la charla escrita va el TRAMO, no la frase entera. La lista de turnos se corrige porque
        // lo que se le manda al modelo tiene que ser el pedido completo; el archivo se agrega, así
        // que escribir la frase entera otra vez duplicaría la primera mitad. Que en el archivo
        // queden dos renglones es fiel: la persona dijo dos cosas seguidas, aunque fueran una sola.
        Charla()?.Note(ChatVoice.Persona, fragment);

        lock (_confirmationGate)
        {
            if (_conversationTurns.Count == 0)
            {
                _conversationTurns.Add(transcript);
                return;
            }

            _conversationTurns[^1] = transcript;
        }
    }

    private List<string> TakeConversationTurns()
    {
        lock (_confirmationGate)
        {
            var turns = new List<string>(_conversationTurns);
            _conversationTurns.Clear();
            return turns;
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
    /// <para>
    /// Recibe los turnos en vez de leerlos: el que cierra la charla es quien se los lleva, con
    /// <see cref="TakeConversationTurns"/>, y así el vaciado no depende de que esta función se haya
    /// llamado.
    /// </para>
    /// </remarks>
    private async Task LearnFromConversationAsync(IReadOnlyList<string> turns)
    {
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
                // El hecho NO se escribe, sólo su veredicto y su largo. Es lo que la bitácora
                // promete de sí misma —«nunca el contenido de las respuestas»— y acá se estaba
                // escribiendo lo más personal que maneja el asistente: algo que aprendió sobre quien
                // lo usa, en texto plano, en un archivo que se comparte para diagnosticar. El
                // veredicto y el largo alcanzan para saber si la observación entró y con qué tamaño,
                // que es para lo que sirve esta línea.
                RuntimeTrace.Write("memoria.observada", $"{captured.Status} · {fact.Length} caracteres");
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

    private void ReminderSchedulerOnAgendaItemDue(object? sender, AgendaItemDueEventArgs eventArgs) =>
        _ = AnnounceAgendaItemAsync(eventArgs);

    /// <summary>
    /// Anuncia un evento de agenda que acaba de empezar.
    /// </summary>
    /// <remarks>
    /// Camino idéntico al de un recordatorio —orbe al frente, globo de bandeja y voz— pero con otras
    /// palabras: un recordatorio es algo que tenés que hacer y un evento es algo que ya empezó, y
    /// leerlos con la misma frase obliga a adivinar de cuál de las dos listas salió.
    /// </remarks>
    private async Task AnnounceAgendaItemAsync(AgendaItemDueEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        var title = eventArgs.Item.Title;
        var when = eventArgs.Item.StartsAt.ToLocalTime().ToString("HH:mm", ArgentineCulture);
        var detail = eventArgs.IsLate
            ? $"Empezaba a las {when}: {title}"
            : $"Son las {when}, empieza: {title}";

        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Attention,
            eventArgs.IsLate ? "Agenda · ya había empezado" : "Agenda",
            detail,
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));

        RequestActivation(new ShellActivationRequest(
            ShellActivationReason.Reminder,
            $"Agenda de {_identity.Name}",
            detail));

        try
        {
            await SpeakIfEnabledAsync(detail, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // La voz es un complemento del aviso visual; su falla no debe perder el evento.
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

        // Parar el oído tira la frase que se estaba armando —el ensamblador hace Reset y suelta el
        // pre-roll sin emitir nada—, así que la espera abierta por el nombre no se va a cerrar nunca
        // sola. Bajar el pestillo acá, en el único punto por donde pasan todos, es lo que impide el
        // peor modo de falla que tuvo este proyecto: decís «Viernes», empezás a hablar, y antes de
        // terminar escribís en el panel o silenciás. La frase se pierde, el pestillo queda arriba, y
        // a partir de ahí la app sigue escuchando, sigue reconociendo el nombre, y no vuelve a abrir
        // una conversación NUNCA MÁS hasta reiniciar. Sin un renglón en la bitácora.
        //
        // Y el orquestador también: quedaba creyendo que escuchaba.
        if (Interlocked.Exchange(ref _awaitingUtterance, 0) != 0)
        {
            _orchestrator.SetListening(false);
            RuntimeTrace.Write("wake.espera.cortada", "se paró el oído antes de que cerrara la frase");
        }

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

            if (!result.Succeeded)
            {
                StartListeningWatchdog();
                return;
            }

            // El oído volvió: se apaga la sordera y el fondo pasa a guardia solo.
            Volatile.Write(ref _deaf, 0);
        }
    }

    /// <summary>
    /// Vuelve a intentar recuperar el oído después de que falle el dispositivo, y mientras tanto lo dice.
    /// </summary>
    /// <remarks>
    /// Antes se intentaba una sola vez. Si Windows devolvía un error de dispositivo —otra aplicación
    /// tomó el micrófono, un virtual como Sonar o NVIDIA Broadcast cambió el predeterminado, se
    /// desenchufó un auricular— quedaba escrito <c>wake.reanudado ok=False</c> en el registro y el
    /// asistente se quedaba sordo hasta que alguien lo reiniciara. Sin decir nada: desde afuera es
    /// indistinguible de estar apagado, que es exactamente lo que terminó preguntando el usuario.
    /// <para>
    /// Reintenta con espera creciente porque casi todas estas fallas son pasajeras: la otra
    /// aplicación suelta el micrófono, el dispositivo vuelve. Y lo anuncia, que es la mitad que más
    /// importa: un asistente sordo que parece atento es peor que uno que avisa que no oye.
    /// </para>
    /// </remarks>
    private void StartListeningWatchdog()
    {
        if (Interlocked.CompareExchange(ref _watchdogRunning, 1, 0) != 0)
        {
            return;
        }

        // Sorda, no error: el equipo anda y el asistente también, lo que no llega es el audio.
        // Pintarlo de rojo manda a buscar una falla donde lo que hay es una capacidad caída.
        Volatile.Write(ref _deaf, 1);
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Deaf,
            "Sin micrófono · reintentando",
            "Windows no me deja acceder al micrófono. Sigo intentando.",
            MicrophoneActive: false,
            WakeWordEnabled: _isWakeWordEnabled));

        _ = Task.Run(async () =>
        {
            TimeSpan[] esperas =
            [
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(45)
            ];

            try
            {
                for (var intento = 0; !_isDisposed; intento++)
                {
                    await Task.Delay(esperas[Math.Min(intento, esperas.Length - 1)]).ConfigureAwait(false);

                    if (_isDisposed || IsMuted || !_isWakeWordEnabled || _wakeWord is null)
                    {
                        // Se dejó de intentar por decisión de alguien, no por falta de audio: la
                        // sordera se apaga o queda encendida para siempre sobre un oído apagado.
                        Volatile.Write(ref _deaf, 0);
                        return;
                    }

                    if (_wakeWord.State == WakeWordServiceState.Listening)
                    {
                        Volatile.Write(ref _deaf, 0);
                        return;
                    }

                    var result = await _wakeWord.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    RuntimeTrace.Write("wake.reintento", $"intento={intento + 1} ok={result.Succeeded}");

                    if (result.Succeeded)
                    {
                        Volatile.Write(ref _deaf, 0);
                        Publish(new AssistantRuntimeUpdate(
                            Resting(),
                            $"Atento · decí \u201C{_wakeWord.Phrases[0]}\u201D",
                            "Ya te escucho de nuevo.",
                            MicrophoneActive: IsAnyMicrophoneActive(),
                            WakeWordEnabled: _isWakeWordEnabled));
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RuntimeTrace.Write("wake.reintento.excepcion", exception.GetType().Name);
            }
            finally
            {
                Interlocked.Exchange(ref _watchdogRunning, 0);
            }
        });
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
            OrbScale = OrbScale,
            FollowActiveMonitor = FollowsActiveMonitor,
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
                PreferredVoiceName = settings.PreferredVoiceName,

                // Las hipótesis de SAPI son la única fuente de transcripción palabra por palabra del
                // camino de siempre: Whisper no entrega nada hasta que el micrófono se cerró, porque
                // transcribe el WAV entero de una vez. Con este respaldo la burbuja se llena
                // mientras se habla; con Whisper aparece completa al final. Es una diferencia real
                // entre los dos reconocedores y no hay cómo emparejarla desde acá.
                EmitPartialTranscriptions = true
            }
        });
    }

    /// <summary>
    /// Arma el oído: el continuo por defecto, y el de siempre si alguien lo pide.
    /// </summary>
    /// <remarks>
    /// El continuo abre el micrófono una sola vez y reparte el audio a tres lugares: la ventana
    /// rodante de diez segundos, el reconocedor de nombre y el detector de voz. La diferencia que se
    /// nota es que <em>lo dicho antes del nombre ya está grabado</em>: «Viernes, creame una carpeta»
    /// sale de un tirón, sin la coreografía de soltar el dispositivo y esperar 220 ms a que Windows
    /// lo libere antes de empezar a grabar.
    /// <para>
    /// Estaba escrito, medido y calibrado, y no lo llamaba nadie. Se enciende por defecto con la
    /// caída puesta: si falta el modelo entrenado, adentro se usa la heurística, y si el oído no
    /// arranca —lo dice <see cref="ContinuousWakeListener.StartAsync"/>— el que llama se queda sin
    /// wake exactamente igual que antes. <c>VIERNES_WAKE_LISTENER=sapi</c> vuelve al de siempre sin
    /// recompilar.
    /// </para>
    /// </remarks>
    private IWakeWordService BuildWakeListener()
    {
        var phrases = ResolveWakePhrases(_settings.EffectiveWakePhrases);
        var confidence = ResolveWakeConfidence();

        if (string.Equals(
            Environment.GetEnvironmentVariable("VIERNES_WAKE_LISTENER")?.Trim(),
            "sapi",
            StringComparison.OrdinalIgnoreCase))
        {
            RuntimeTrace.Write("oido.continuo", "apagado por VIERNES_WAKE_LISTENER=sapi");
            return new SapiWakeWordService(new WakeWordServiceOptions
            {
                Phrases = phrases,
                RecognitionCulture = _settings.RecognitionCulture,
                MinimumConfidence = confidence
            });
        }

        var options = new ContinuousWakeListenerOptions
        {
            Phrases = phrases,
            RecognitionCulture = _settings.RecognitionCulture,
            MinimumConfidence = confidence
        };

        // El detector se arma acá y no adentro del oído para poder prestárselo a la sesión en vivo.
        // Es la carga que se estaba pagando de nuevo en cada conversación. Y si ya está armado se
        // reusa: rehacer el oído para cambiarle el nombre no tiene por qué volver a cargar el modelo
        // entrenado —que no depende del nombre— ni dejar el anterior colgado prestado a la sesión
        // en vivo.
        if (_voiceDetector is null)
        {
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            _voiceDetector = ContinuousWakeListener.CreateDetector(options, out var faltante);
            reloj.Stop();
            RuntimeTrace.Write(
                "vad.cargado",
                $"{_voiceDetector.Info.Name} en {reloj.ElapsedMilliseconds} ms" +
                (faltante is null ? string.Empty : $" · {faltante}"));
        }

        var listener = new ContinuousWakeListener(options, _voiceDetector);
        _continuousWake = listener;
        return listener;
    }

    /// <summary>
    /// Cierra el oído y abre otro con las frases que correspondan ahora. Dice si quedó escuchando.
    /// </summary>
    /// <remarks>
    /// Las frases se le fijan al oído cuando se lo construye —adentro arma con ellas la gramática de
    /// SAPI, una sola vez—, así que cambiarle el nombre al asistente no se resuelve avisándole:
    /// hay que cerrarlo y abrir otro. Es la única parte del renombrado que no se puede hacer en el
    /// lugar.
    /// <para>
    /// Vuelve a arrancar sólo si estaba escuchando. Si estaba parado —silenciado, o con el micrófono
    /// prestado a una conversación en curso— el que lo despierte después va a encontrar el oído
    /// nuevo, que ya tiene el nombre nuevo.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Rehace el oído con las frases del nombre nuevo. Contesta si quedó escuchando como estaba.
    /// </summary>
    /// <remarks>
    /// Todo el cuerpo va adentro del <c>try</c>, y no sólo el arranque. Acá afuera quedaban
    /// <c>DisposeAsync</c>, <c>BuildWakeListener</c>, <c>SubscribeWakeWord</c> y
    /// <c>SetMutedAsync</c>: si cualquiera de ésos tiraba —el micrófono lo tomó otro, el motor de
    /// reconocimiento no se pudo crear— la excepción se escapaba hacia
    /// <see cref="SetAssistantNameAsync"/>, que no la atrapa, y el renombrado moría después de haber
    /// destruido el oído viejo. O sea: la peor salida posible, sin oído y sin aviso.
    /// <para>
    /// Cuando algo falla se deja <c>_wakeWord</c> en lo que haya quedado y se contesta <c>false</c>,
    /// que es lo que hace que el usuario vea «el oído no volvió» en vez de un renombrado silencioso a
    /// medias.
    /// </para>
    /// </remarks>
    private async Task<bool> RestartWakeListenerAsync(CancellationToken cancellationToken)
    {
        var previous = _wakeWord;
        var wasListening = previous?.State == WakeWordServiceState.Listening;

        try
        {
            if (previous is not null)
            {
                UnsubscribeWakeWord(previous);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _wakeWord = BuildWakeListener();
            SubscribeWakeWord(_wakeWord);
            await _wakeWord.SetMutedAsync(_isMuted, cancellationToken).ConfigureAwait(false);

            if (!wasListening || _isMuted || !_isWakeWordEnabled)
            {
                return true;
            }

            var result = await _wakeWord.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                RuntimeTrace.Write(
                    "oido.rearmado.fallo",
                    $"{result.ErrorCode} · {result.ErrorMessage}");
            }

            return result.Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // El micrófono lo puede haber tomado otro en el medio. Que no vuelva el oído es quedarse
            // sin wake hasta reiniciar, no quedarse sin asistente: se informa y sigue.
            RuntimeTrace.Write("oido.rearmado.excepcion", $"{exception.GetType().Name} · {exception.Message}");
            return false;
        }
    }

    private void UnsubscribeWakeWord(IWakeWordService wakeWord)
    {
        wakeWord.MicrophoneActivityChanged -= WakeOnMicrophoneActivityChanged;
        wakeWord.WakeWordDetected -= WakeOnWakeWordDetected;
        wakeWord.ServiceError -= WakeOnError;

        if (wakeWord is ContinuousWakeListener continuous)
        {
            continuous.UtteranceCaptured -= WakeOnUtteranceCaptured;
            continuous.AudioLevelChanged -= WakeOnAudioLevel;
        }
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
        if (e.IsVoice)
        {
            // El detector vio voz: entra audio, y sorda deja de valer en este mismo cuadro. No es la
            // contracara exacta de cómo se enciende —esto sólo corre mientras el dictado captura, y
            // la sordera se enciende también fuera de ahí—; lo que garantiza que no quede pegada es
            // IsDeaf(), que la contrasta contra el wake.
            Volatile.Write(ref _deaf, 0);
        }

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

        if (wakeWord is ContinuousWakeListener continuous)
        {
            continuous.UtteranceCaptured += WakeOnUtteranceCaptured;
            continuous.AudioLevelChanged += WakeOnAudioLevel;
        }
    }

    private void RecognitionOnMicrophoneActivityChanged(object? sender, MicrophoneActivityChangedEventArgs e)
    {
        if (e.IsActive)
        {
            // Windows entregó el dispositivo: lo que enciende la sordera es justamente que no lo
            // entregue, así que acá deja de valer sin esperar a que alguien hable.
            Volatile.Write(ref _deaf, 0);

            // Micrófono abierto es frase nueva. Lo que quedaba de la anterior —incluido lo que se
            // había rescatado del búfer— pertenece a un pedido que ya se contestó.
            ClearDictation();
        }

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
                ? Resting()
                : _lastVisualState;
        Publish(new AssistantRuntimeUpdate(
            state,
            e.IsActive ? $"Escuchando con {_recognitionProviderName}…" : "Micrófono de dictado apagado",
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    /// <summary>
    /// Llegó texto del reconocedor: firme o todavía formándose.
    /// </summary>
    /// <remarks>
    /// La diferencia entre una hipótesis y un tramo cerrado es justo la que la burbuja dibuja: la
    /// última palabra en itálica mientras puede cambiar, y plena cuando ya no. Antes las dos salían
    /// por el mismo campo de texto y del otro lado no había con qué distinguirlas.
    /// </remarks>
    private void RecognitionOnTranscriptionUpdated(object? sender, SpeechTranscriptionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text))
        {
            return;
        }

        // El WAV del oído continuo pasa por el mismo proveedor y levanta este mismo evento, un tramo
        // por vez. Esa frase no se dibuja acá: la parte HandleWakeUtteranceAsync entre lo anterior al
        // nombre y el pedido, y son dos calidades distintas en la burbuja.
        if (Volatile.Read(ref _splittingUtterance) != 0)
        {
            return;
        }

        PublishDictation(e.IsFinal ? _dictation.Confirm(e.Text) : _dictation.Hear(e.Text));
    }

    /// <summary>
    /// Un problema del oído. Que el dispositivo no esté no es lo mismo que que algo haya fallado.
    /// </summary>
    /// <remarks>
    /// Sorda dice «no te oigo» y error dice «algo se rompió». Un micrófono que Windows no entrega
    /// —lo tomó otra aplicación, cambió el predeterminado, se desenchufó— es lo primero, y pintarlo
    /// de rojo manda al usuario a buscar una falla que no existe.
    /// </remarks>
    private void RecognitionOnError(object? sender, SpeechServiceErrorEventArgs e)
    {
        var deaf = e.ErrorCode is SpeechErrorCode.DeviceError or SpeechErrorCode.Unavailable;
        if (deaf)
        {
            // El aviso sale igual aunque el wake siga escuchando: el usuario acaba de apretar para
            // hablar y no lo oímos, y eso hay que decirlo cuando pasa. Lo que no puede pasar es que
            // quede dicho para siempre —el fallo era del dictado, no del oído—, y de eso se ocupa
            // IsDeaf(): en el próximo barrido el fondo vuelve a guardia solo.
            Volatile.Write(ref _deaf, 1);
        }

        Publish(new AssistantRuntimeUpdate(
            deaf ? AssistantVisualState.Deaf : AssistantVisualState.Error,
            deaf ? "No te oigo · el micrófono no entrega audio" : "La voz local informó un problema",
            SafeSpeechMessage(e.ErrorCode),
            MicrophoneActive: IsAnyMicrophoneActive()));
    }

    private void WakeOnMicrophoneActivityChanged(object? sender, MicrophoneActivityChangedEventArgs e)
    {
        // Armar o soltar el oído es exactamente lo que separa guardia de reposo, así que si el orbe
        // está quieto se recalcula el fondo acá mismo en vez de esperar al vigía.
        var state = _lastVisualState.IsResting() ? Resting() : _lastVisualState;
        Publish(new AssistantRuntimeUpdate(
            state,
            e.IsActive && _isWakeWordEnabled
                ? $"Wake demo activo · decí “{_wakeWord?.Phrases[0] ?? _identity.WakePhrases[0]}”"
                : state == AssistantVisualState.Idle
                    ? "Micrófono de activación apagado"
                    : CurrentStateLabel(state),
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    /// <summary>
    /// Sonó el nombre.
    /// </summary>
    /// <remarks>
    /// Con el oído continuo esto <b>no</b> abre la conversación: el micrófono ya está abierto y la
    /// persona probablemente siga hablando —«Viernes, creame una carpeta» es una sola frase—. Abrir
    /// acá le sacaría el dispositivo al oído en el medio de la frase y perdería justamente lo que
    /// esta clase existe para no perder. Se avisa que la oyó y se espera la frase entera.
    /// <para>
    /// Con el wake de siempre no hay frase que esperar —sólo detecta— y sigue el camino de antes.
    /// </para>
    /// </remarks>
    private void WakeOnWakeWordDetected(object? sender, WakeWordDetectedEventArgs e)
    {
        if (_continuousWake is not null && _recognition is WhisperSpeechRecognitionProvider)
        {
            NoteWakeHeard(e);
            return;
        }

        _ = HandleWakeWordDetectedAsync(e);
    }

    /// <summary>
    /// El nivel del oído continuo, que llega todo el día.
    /// </summary>
    /// <remarks>
    /// Con el orbe escondido no hay nada que mover: publicarlo igual es despertar el dispatcher
    /// treinta veces por segundo para no dibujar nada. Durante el dictado no hace falta esta guarda
    /// —ahí la captura dura lo que dura una frase— y por eso el camino es otro.
    /// </remarks>
    private void WakeOnAudioLevel(object? sender, AudioLevelEventArgs e)
    {
        if (!_isShellVisible)
        {
            return;
        }

        RecognitionOnAudioLevel(sender, e);
    }

    /// <summary>Se oyó el nombre y el oído sigue grabando: acá sólo se dice que la escuchó.</summary>
    private void NoteWakeHeard(WakeWordDetectedEventArgs eventArgs)
    {
        if (_isDisposed || IsMuted || !_isWakeWordEnabled)
        {
            return;
        }

        if (_conversationActive)
        {
            // Con una conversación abierta, decir el nombre es parte de la charla.
            RuntimeTrace.Write("wake.ignorado", "ya hay conversación abierta");
            return;
        }

        if (Interlocked.CompareExchange(ref _awaitingUtterance, 1, 0) != 0)
        {
            return;
        }

        RuntimeTrace.Write(
            "wake.detected",
            $"frase «{eventArgs.Phrase}» confianza {eventArgs.Confidence:0.00} · esperando la frase");

        RequestActivation(new ShellActivationRequest(
            ShellActivationReason.WakeWord,
            _identity.Name,
            $"Te escuché decir “{eventArgs.Phrase}”."));

        _orchestrator.SetListening(true);
        _dictation.Clear();
        Publish(new AssistantRuntimeUpdate(
            AssistantVisualState.Listening,
            "Te escucho · seguí",
            MicrophoneActive: true,
            WakeWordEnabled: true,
            ClearDictation: true));
    }

    /// <summary>
    /// Llegó la frase entera. Se atiende en otro hilo, y eso no es opcional.
    /// </summary>
    /// <remarks>
    /// Esto lo dispara la devolución de llamada del dispositivo de captura, y lo primero que hace el
    /// trabajo es <b>cerrar ese mismo dispositivo</b> para dejárselo a la conversación. Sin el
    /// <c>Task.Run</c>, el cuerpo arranca inline sobre el hilo del driver —el semáforo del oído está
    /// libre, así que el <c>await</c> no cede nada— y <c>StopRecording</c> se queda esperando a que
    /// termine la devolución de llamada que lo está llamando. Es el mismo abrazo mortal que ya está
    /// comentado en el micrófono de la sesión en vivo.
    /// </remarks>
    private void WakeOnUtteranceCaptured(object? sender, WakeUtteranceEventArgs e) =>
        _ = Task.Run(() => HandleWakeUtteranceAsync(e));

    /// <summary>Si lo que se dijo no es más que el nombre con el que la llamaron.</summary>
    /// <remarks>
    /// Se compara contra la frase que <em>efectivamente</em> se oyó y no contra una lista fija: si
    /// alguien le puso otro nombre al asistente, o dijo «hola Viernes» en vez de «Viernes», la que
    /// corresponde es la que disparó.
    /// </remarks>
    private static bool IsJustTheName(string spoken, string phrase)
    {
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return true;
        }

        var nombre = phrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return spoken
            .Split(
                [' ', ',', '.', ';', ':', '¿', '?', '¡', '!', '\u2026'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => nombre.Contains(word.ToLowerInvariant()));
    }

    /// <summary>
    /// Llegó la frase entera: lo de antes del nombre y lo de después, pegado.
    /// </summary>
    /// <remarks>
    /// No es <c>async void</c> aunque sea lo que uno escribiría para un manejador de eventos: una
    /// excepción adentro de un <c>async void</c> no tiene a dónde ir y ya se llevó puesto el proceso
    /// una vez en este repositorio. Sale por una tarea con su propio <c>try</c>.
    /// <para>
    /// El WAV es de quien recibe el evento, así que lo cierra este método pase lo que pase.
    /// </para>
    /// </remarks>
    private async Task HandleWakeUtteranceAsync(WakeUtteranceEventArgs eventArgs)
    {
        var abrio = false;
        try
        {
            if (Interlocked.Exchange(ref _awaitingUtterance, 0) == 0 ||
                _isDisposed || IsMuted || _conversationActive ||
                _recognition is not WhisperSpeechRecognitionProvider whisper)
            {
                return;
            }

            // El oído suelta el micrófono antes de que lo tome la conversación: es de uno solo, y
            // dos capturas sobre el mismo dispositivo es la falla que ya costó una tarde.
            await PauseWakeWordAsync(CancellationToken.None).ConfigureAwait(false);

            var reloj = System.Diagnostics.Stopwatch.StartNew();
            WaveTranscription transcripcion;
            Volatile.Write(ref _splittingUtterance, 1);
            try
            {
                transcripcion = await whisper
                    .TranscribeWaveWithSegmentsAsync(eventArgs.Wave, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _splittingUtterance, 0);
            }

            reloj.Stop();

            // La frase NO se escribe en la traza: es un archivo de texto plano que queda en el disco
            // y se pega en un reporte cuando algo falla. Lo que se dijo en voz alta adentro de la
            // casa no tiene por qué terminar ahí.
            RuntimeTrace.Write(
                "oido.frase",
                $"{reloj.ElapsedMilliseconds} ms · antes {eventArgs.PreRollDuration.TotalMilliseconds:0} ms · " +
                $"después {eventArgs.TailDuration.TotalMilliseconds:0} ms · corte={eventArgs.StopReason} · " +
                $"ok={transcripcion.Result.Succeeded} · tramos={transcripcion.Segments.Count}");

            var texto = transcripcion.Result.Text?.Trim();
            if (!transcripcion.Result.Succeeded || string.IsNullOrWhiteSpace(texto))
            {
                Publish(new AssistantRuntimeUpdate(
                    Resting(),
                    "No te entendí · llamame de nuevo",
                    MicrophoneActive: IsAnyMicrophoneActive(),
                    WakeWordEnabled: _isWakeWordEnabled,
                    ClearDictation: true));
                _dictation.Clear();
                return;
            }

            // Lo anterior al nombre se dibuja distinto: se manda igual —es contexto del pedido— pero
            // no se lo dijeron a ella, y mostrarlo idéntico al pedido se siente como espiar.
            var partes = UtteranceTranscript.Split(
                transcripcion.Segments,
                eventArgs.PreRollDuration,
                eventArgs.Phrase);
            _dictation.Clear();
            _dictation.Recover(partes.Recovered, eventArgs.PreRollDuration);
            var palabras = _dictation.Settle(partes.Spoken);

            // Llamarla y callarse es pedirle que atienda, no hacerle un pedido. Mandarle «Viernes»
            // al modelo como si fuera una consulta le hace contestar cualquier cosa; lo que
            // corresponde ahí es el «te escucho» de siempre. Se distingue por el corte: el oído dejó
            // de grabar porque nadie habló después del nombre, no porque alguien terminara de hablar.
            var soloLlamado = eventArgs.StopReason == UtteranceStopReason.InitialSilence &&
                string.IsNullOrEmpty(partes.Recovered) &&
                IsJustTheName(partes.Spoken, eventArgs.Phrase);

            Publish(new AssistantRuntimeUpdate(
                soloLlamado ? AssistantVisualState.Listening : AssistantVisualState.Thinking,
                soloLlamado ? "Te escucho · decime «listo» para cortar" : "Entendido · procesando…",
                soloLlamado ? null : partes.Full,
                MicrophoneActive: soloLlamado,
                Dictation: palabras,
                DictationRecovered: _dictation.RecoveredSpan));

            // La apertura se marca con lo que contestó StartConversationAsync y NO antes de llamarla.
            // Tiene una salida temprana silenciosa —silenciada, sin reconocedor, ya había una
            // conversación— y dándola por abierta de antemano el finally no entraba en la rama que
            // reabre el oído: el micrófono del wake quedaba cerrado por un PauseWakeWordAsync que
            // nadie deshacía, y el orquestador creyendo que seguía escuchando.
            // Al modelo va la versión marcada y no la plana: lo que se rescató de la ventana rodante
            // se le entrega como contexto, no como pedido. Ver SplitUtterance.ForModel.
            abrio = await StartConversationAsync(
                soloLlamado ? null : partes.ForModel,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RuntimeTrace.Write("oido.frase.excepcion", $"{exception.GetType().Name} · {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _awaitingUtterance, 0);
            eventArgs.Wave.Dispose();

            // Si no se abrió conversación, el oído tiene que volver a escuchar o el asistente se
            // queda sordo hasta que alguien apriete algo.
            if (!abrio && !_conversationActive)
            {
                _orchestrator.SetListening(false);
                await ResumeWakeWordAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private void WakeOnError(object? sender, SpeechServiceErrorEventArgs e) =>
        Publish(new AssistantRuntimeUpdate(
            Resting(),
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

    /// <summary>
    /// Manda la línea de dictado sin tocar el estado del orbe.
    /// </summary>
    /// <remarks>
    /// Va por <c>Updated</c> y no por <see cref="Publish"/> por la misma razón que el nivel del
    /// micrófono: esto llega varias veces por segundo mientras alguien habla y no es un cambio de
    /// estado, es el mismo estado con más texto. Y porque publicarlo como estado tenía un efecto
    /// concreto y feo: Whisper transcribe <em>después</em> de cerrar el micrófono, así que su tramo
    /// firme llegaba con el orbe ya en «pensando» y lo devolvía a «te escucho» en el medio del turno.
    /// <para>
    /// <b>No alcanza con no llamar a <see cref="Publish"/>.</b> Eso deja quieto el estado de este
    /// lado, pero del otro la interfaz igual hacía <c>StatusText = update.Status</c> con la etiqueta
    /// genérica que viaja acá, y cada palabra parcial pisaba la línea de estado. Por eso va
    /// <c>DictationOnly</c>: es la mitad de la promesa que vive del otro lado, y las dos hacen
    /// falta. El nivel del micrófono no la necesita porque llega en un campo propio.
    /// </para>
    /// </remarks>
    private void PublishDictation(IReadOnlyList<DictationWord> words) =>
        Updated?.Invoke(this, new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            MicrophoneActive: IsAnyMicrophoneActive(),
            Dictation: words,
            DictationRecovered: _dictation.RecoveredSpan,
            DictationOnly: true));

    /// <summary>Empieza otra frase: se borra la línea, también lo que se había rescatado.</summary>
    /// <remarks>
    /// Borrar la línea tampoco es un cambio de estado: el micrófono que se abre para otra frase no
    /// cambia lo que el orbe está diciendo. Va con la misma marca que <see cref="PublishDictation"/>
    /// y por lo mismo — sin ella, abrir el micrófono en el medio de una conversación reescribía
    /// «En conversación · decime «listo» para cortar» con la etiqueta genérica.
    /// </remarks>
    private void ClearDictation()
    {
        _dictation.Clear();
        Updated?.Invoke(this, new AssistantRuntimeUpdate(
            _lastVisualState,
            CurrentStateLabel(_lastVisualState),
            ClearDictation: true,
            DictationOnly: true));
    }

    /// <summary>
    /// Si ahora mismo el oído no entrega audio.
    /// </summary>
    /// <remarks>
    /// No alcanza con leer <see cref="_deaf"/>. La bandera se enciende desde un evento —el error del
    /// proveedor de reconocimiento, una captura que falla por el dispositivo— y todos sus apagadores
    /// viven en el camino del dictado: el nivel de audio, la actividad del micrófono, la captura que
    /// sale bien. En reposo ese camino no corre. Bastaba con que otra aplicación se quedara con el
    /// micrófono un segundo durante un push-to-talk para que la bandera quedara encendida sobre un
    /// wake word que nunca se cayó: el orbe decía «no te oigo» mientras el oído escuchaba
    /// perfectamente, y no quedaba nadie que pudiera bajarla.
    /// <para>
    /// Por eso acá se recalcula antes de creerle a la memoria: <b>si el wake está escuchando, no hay
    /// sordera</b> —Windows no sostiene una captura continua sobre un dispositivo que no entrega—, y
    /// el veredicto viejo se borra en el acto. Es el único estado de fondo que se enciende por un
    /// evento en vez de por una condición mirable, así que es el único que puede quedarse pegado, y
    /// esto es exactamente lo que se lo impide. Quien vuelva a leer la bandera sola porque «es un
    /// int y esto sobra», trae el bug de vuelta.
    /// </para>
    /// <para>
    /// Con el wake apagado —push-to-talk puro— no hay nada que mirar y manda la memoria. Ahí la
    /// sordera se apaga sola en el próximo push, apenas el micrófono vuelva a entregar.
    /// </para>
    /// </remarks>
    private bool IsDeaf()
    {
        if (!IsMuted && _isWakeWordEnabled && _wakeWord?.State == WakeWordServiceState.Listening)
        {
            Volatile.Write(ref _deaf, 0);
            return false;
        }

        return Volatile.Read(ref _deaf) != 0;
    }

    /// <summary>
    /// Qué muestra el orbe cuando no está haciendo nada.
    /// </summary>
    /// <remarks>
    /// «Nada» no es un solo estado: puede haber una pregunta sin contestar, un proyecto frenado, una
    /// misión avanzando de fondo o un micrófono que no oye. Ninguna de esas cosas es una actividad
    /// —no hay turno en curso— y todas tienen que verse, así que reposo es el último de la lista y no
    /// el único. Se recalcula entero en cada publicación, así que un estado de fondo se apaga solo en
    /// cuanto su condición deja de valer.
    /// <para>
    /// Todos menos sorda salen de mirar algo que se puede volver a mirar —el libro de misiones, el
    /// vigía de proyectos, el estado del wake—. Sorda es la excepción: se enciende por un evento, y
    /// por eso <see cref="IsDeaf"/> existe. Es el único que hay que acordarse de bajar, y ahí está
    /// escrito quién lo baja.
    /// </para>
    /// <para>
    /// El orden es el de <c>PRI</c> del boceto: sorda 5, esperándote y proyecto y sin clave 3,
    /// trabajando sin vos y guardia 1, reposo 0.
    /// </para>
    /// </remarks>
    private AssistantVisualState Resting()
    {
        // Sorda gana: dibujar cualquier otra cosa mientras no entra audio es prometer que escucha.
        if (IsDeaf())
        {
            return AssistantVisualState.Deaf;
        }

        // Una pregunta sin contestar es lo único de acá que se destraba ahora mismo.
        if (_missionWaiting)
        {
            return AssistantVisualState.WaitingForYou;
        }

        // Y acá el aviso vence. Que un proyecto esté esperando es cierto todo el tiempo que dure,
        // pero ocupar el orbe con eso todo ese tiempo tapa «guardia» —lo único que el usuario mira
        // siempre— con algo que además le debe otro, no ella. El dato sigue en el desplegable de
        // proyectos; lo que se libera es el orbe.
        if (_projectWaiting && DateTimeOffset.Now < _projectNoticeUntil)
        {
            return AssistantVisualState.ProjectWaiting;
        }

        if (!IsCloudConfigured)
        {
            return AssistantVisualState.Unconfigured;
        }

        // Y acá termina. Guardia —el micrófono armado esperando el nombre— NO es un estado que se
        // dibuje: es el reposo.
        //
        // Durante un tiempo sí lo era, y se sentía mal por una razón que sólo se ve usándola: el
        // micrófono está armado casi siempre, así que «guardia» no era un estado, era el fondo de
        // pantalla —y con su cartel puesto—. El orbe quieto tiene que ser celeste y no decir nada:
        // sólo ella. Que esté atenta al nombre se demuestra reaccionando cuando la nombran, no
        // anunciándolo todo el día.
        //
        // El perfil de guardia se conserva entero en la tabla: lo usa el banco de render, y sigue
        // disponible si alguna vez hace falta distinguir armado de no armado.
        if (_missionRunning)
        {
            return AssistantVisualState.Background;
        }

        return AssistantVisualState.Idle;
    }

    /// <summary>
    /// Mira lo que enciende los estados de fondo y, si el orbe está quieto, lo pone al día.
    /// </summary>
    /// <remarks>
    /// Sólo pisa estados de reposo. Si hay un turno pensando, una respuesta sonando o una
    /// confirmación esperando, eso es lo que está pasando y el fondo puede esperar a que termine.
    /// <para>
    /// Cada cinco segundos no relee el disco: <c>ListAsync</c> devuelve el caché del libro, que se
    /// llenó una vez y no se invalida. Barato es barato de verdad —es memoria del proceso— pero eso
    /// también quiere decir que ve los cambios de la herramienta <c>mision</c> y no vería el archivo
    /// tocado por fuera. Lo que sí se paga cada tanto es el vigía de proyectos, y por eso va con su
    /// propio período.
    /// </para>
    /// </remarks>
    private async Task RefreshAmbientAsync()
    {
        if (_isDisposed || !_isInitialized ||
            Interlocked.CompareExchange(ref _ambientRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var missions = await _missionBook
                .ListAsync(onlyOpen: true, CancellationToken.None)
                .ConfigureAwait(false);

            // Dispose no espera al barrido en vuelo: Timer.Dispose() no lo hace, y este arranca
            // fire-and-forget. Un barrido que ya había pasado la guarda de entrada seguía después
            // del await y publicaba sobre un dispatcher apagado. Revalidar acá es lo único que
            // separa un apagado limpio de una excepción que nadie ve.
            if (_isDisposed)
            {
                return;
            }

            _missionWaiting = missions.Any(mission =>
                mission.State == MissionState.Esperando && mission.Question is not null);
            _missionRunning = missions.Any(mission => mission.State == MissionState.EnCurso);

            var now = DateTimeOffset.Now;
            if (now - _projectsReadAt >= ProjectPeriod)
            {
                _projectsReadAt = now;

                // El propio Viernes queda afuera por la misma razón que en la herramienta: mirarse
                // trabajando produce un lazo y no es lo que el usuario quiere seguir.
                var esperando = _projectWatcher
                    .Recent(now, maximum: 8, excludeProjectContaining: "Viernes")
                    .Where(session => session.Activity == SessionActivity.Esperando)
                    .Select(session => session.Project)
                    .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // La firma es QUIÉNES esperan, no cuántos: si el mismo proyecto sigue esperando, no
                // hay noticia nueva que anunciar. Si arranca otro, sí.
                var firma = string.Join('', esperando);
                if (firma != _projectWaitingSignature)
                {
                    _projectWaitingSignature = firma;
                    _projectNoticeUntil = firma.Length == 0 ? default : now + ProjectNoticeLife;
                }

                _projectWaiting = firma.Length > 0;
            }

            PublishResting();
        }
        catch (Exception exception)
        {
            // Mirar de fondo no puede tumbar el asistente ni hacerse notar por fallar.
            //
            // Acá NO va el filtro «when (exception is not OperationCanceledException)» que estaba
            // antes. Dejar pasar las canceladas tiene sentido cuando alguien espera la tarea y va a
            // ver la cancelación; este barrido sale con `_ = RefreshAmbientAsync()` y no lo espera
            // nadie, así que lo único que conseguía el filtro era convertir en excepción no
            // observada justo la que más aparece: TaskCanceledException, la que tira el dispatcher
            // ya apagado, que además DERIVA de OperationCanceledException y por eso se colaba.
            RuntimeTrace.Write("fondo.excepcion", exception.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _ambientRunning, 0);
        }
    }

    /// <summary>Publica el estado de fondo que corresponde ahora, si hay lugar para publicarlo.</summary>
    private void PublishResting()
    {
        if (Volatile.Read(ref _requestActive) != 0 ||
            Volatile.Read(ref _confirmActive) != 0 ||
            Volatile.Read(ref _distilling) != 0 ||
            _conversationActive ||
            HasPendingConfirmation ||
            !_lastVisualState.IsResting())
        {
            return;
        }

        var resting = Resting();
        if (resting == _lastVisualState)
        {
            return;
        }

        Publish(new AssistantRuntimeUpdate(
            resting,
            CurrentStateLabel(resting),
            MicrophoneActive: IsAnyMicrophoneActive(),
            WakeWordEnabled: _isWakeWordEnabled));
    }

    private bool IsAnyMicrophoneActive() =>
        (_recognition?.IsMicrophoneActive ?? false) || (_wakeWord?.IsMicrophoneActive ?? false);

    private static string CurrentStateLabel(AssistantVisualState state) => state switch
    {
        AssistantVisualState.Listening => "Escuchando…",
        AssistantVisualState.Thinking => "Pensando…",
        AssistantVisualState.Speaking => "Hablando…",
        AssistantVisualState.Attention => "Esperando confirmación",
        AssistantVisualState.AskingPermission => "Pidiendo permiso",
        AssistantVisualState.Error => "Atención necesaria",
        AssistantVisualState.Watching => "Atento",
        AssistantVisualState.Background => "Trabajando sin vos",
        AssistantVisualState.WaitingForYou => "Te pregunté algo y quedó sin contestar",
        AssistantVisualState.ProjectWaiting => "Un proyecto te está esperando",
        AssistantVisualState.Interrupted => "Me callo",
        AssistantVisualState.Deaf => "No te oigo",
        // Decía «Falta la clave», que es un diagnóstico y no una instrucción. Es el primer estado que
        // ve alguien que recién instaló, y quedarse en el diagnóstico lo deja adivinando dónde va esa
        // clave —no está en ningún archivo de la aplicación, vive en las variables de entorno de su
        // cuenta de Windows, que es justamente el lugar donde nadie mira—. El instalador ya sabe
        // ponerla, así que lo único que hace falta es nombrarlo.
        AssistantVisualState.Unconfigured => "Falta la clave de OpenRouter · corré el instalador de nuevo para ponerla",
        AssistantVisualState.Offline => "Sin red",
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

        // Antes de soltar nada: una charla que quedó abierta al apagar el programa tiene que quedar
        // cerrada en disco, no a medio escribir.
        CerrarCharla();

        await DisposeLiveAsync().ConfigureAwait(false);
        _ambientTimer?.Dispose();
        _ambientTimer = null;
        _conversationCancellation?.Cancel();
        _conversationCancellation?.Dispose();
        _wakeHandoffCancellation?.Cancel();
        _wakeHandoffCancellation?.Dispose();
        _orchestrator.StateChanged -= OrchestratorOnStateChanged;
        _orchestrator.ProgressChanged -= OrchestratorOnProgressChanged;
        _reminderScheduler.ReminderDue -= ReminderSchedulerOnReminderDue;
        _reminderScheduler.AgendaItemDue -= ReminderSchedulerOnAgendaItemDue;
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
            UnsubscribeWakeWord(_wakeWord);
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

        // Después del oído y de la sesión en vivo, que son los dos que lo usaban prestado. Al revés
        // se desecharía el modelo debajo de una captura que todavía está en vuelo.
        _voiceDetector?.Dispose();
        _voiceDetector = null;
        _continuousWake = null;

        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        await _neuralPlayer.DisposeAsync().ConfigureAwait(false);
        if (_speechSynthesizer is not null)
        {
            await _speechSynthesizer.DisposeAsync().ConfigureAwait(false);
        }

        _voiceTransitionGate.Dispose();
        _httpClient.Dispose();
    }
}
