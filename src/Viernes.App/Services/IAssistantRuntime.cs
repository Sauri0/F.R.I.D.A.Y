using Viernes.App.ViewModels;
using Viernes.Core.Conversation;

namespace Viernes.App.Services;

internal sealed record AssistantRuntimeUpdate(
    AssistantVisualState State,
    string Status,
    string? Message = null,
    bool? MicrophoneActive = null,
    bool? WakeWordEnabled = null,
    PendingConfirmation? Confirmation = null,
    bool ClearConfirmation = false,
    IReadOnlyList<TurnStep>? Steps = null,
    bool ClearSteps = false,
    double? AudioLevel = null,
    IReadOnlyList<BubbleListItem>? Items = null,
    bool ClearItems = false,
    BubbleListKind ListKind = BubbleListKind.Agenda,

    /// <summary>
    /// Volver al reposo del todo, sin dejar nada en pantalla.
    /// </summary>
    /// <remarks>
    /// El cierre normal de un turno muestra la respuesta unos segundos antes de encogerse, que es
    /// lo que se quiere casi siempre. Pero cuando el usuario pidió que pare, dejar la burbuja
    /// abierta contando lo que hizo es lo contrario de lo que pidió: se ve como si siguiera
    /// trabajando. Esta bandera dice «esto no es una respuesta que mostrar, es un apagarse».
    /// </remarks>
    bool Quiet = false,

    /// <summary>
    /// El registro con el que está diciendo esto, si tiene uno.
    /// </summary>
    /// <remarks>
    /// Viaja por el mismo canal que el estado y no por uno propio porque no es una condición
    /// aparte: es la cara con la que se dice <em>este</em> cambio. Sale del mismo
    /// <see cref="Viernes.Core.Voice.VoiceMoment"/> con el que se sintetiza la voz, y si se
    /// calculara dos veces, la cara y el tono se separarían apenas alguien tocara uno de los dos.
    /// <para>Es un gesto: se dispara, dura lo que dice la tabla y se apaga solo. Nadie lo apaga.</para>
    /// </remarks>
    Controls.OrbMood? Mood = null,

    /// <summary>
    /// Lo que se está transcribiendo ahora, palabra por palabra y con qué tan firme es cada una.
    /// </summary>
    /// <remarks>
    /// No es un <c>string</c> y no puede serlo: la burbuja dibuja tres calidades a la vez —lo que se
    /// rescató del búfer al 40 %, lo firme pleno y la palabra que se está formando en itálica al
    /// 60 %— y esa distinción se decide <em>por palabra</em>. Un solo texto no puede cargarla, así
    /// que el reconocedor tendría que mandarla tres veces o la interfaz tendría que adivinarla.
    /// <para>
    /// La arma <see cref="Viernes.Core.Voice.DictationBoard"/>, que a su vez usa
    /// <c>DictationLine.Build</c>. Acá viaja ya resuelta: quien la dibuja no decide nada.
    /// </para>
    /// </remarks>
    IReadOnlyList<Viernes.Core.Voice.DictationWord>? Dictation = null,

    /// <summary>Borrar la línea: arranca otra frase o se cerró la charla.</summary>
    bool ClearDictation = false,

    /// <summary>
    /// Cuánto audio anterior al nombre se rescató del búfer rodante, si se rescató alguno.
    /// </summary>
    /// <remarks>
    /// Viaja para poder escribirlo en el encabezado del bloque recuperado. <b>No son los diez
    /// segundos de la ventana</b>: el recorte llega hasta donde arrancó esa tanda de habla, así que
    /// el número cambia en cada frase y con la tele puesta no se meten diez segundos de tele
    /// adelante del pedido.
    /// </remarks>
    TimeSpan? DictationRecovered = null,

    /// <summary>
    /// Esta publicación es sólo la línea de dictado: no trae estado ni etiqueta propios.
    /// </summary>
    /// <remarks>
    /// <see cref="State"/> y <see cref="Status"/> viajan igual porque el récord los pide, pero son
    /// el eco del último estado publicado y no un cambio. Sin esta marca, del otro lado no hay con
    /// qué distinguirlos: cada hipótesis parcial caía en el manejador completo, que hace
    /// <c>StatusText = update.Status</c>, y pisaba «En conversación · decime «listo» para cortar»
    /// con «Escuchando…» apenas alguien empezaba a hablar. El nivel del micrófono no necesita marca
    /// porque llega en un campo propio; esto no tiene uno, así que la marca es la que hay.
    /// </remarks>
    bool DictationOnly = false);

internal sealed record PendingConfirmation(
    string ToolCallId,
    string Title,
    string Detail);

/// <summary>Por qué Viernes pide aparecer sin que el usuario haya tocado el orbe.</summary>
internal enum ShellActivationReason
{
    /// <summary>Alguien lo llamó por su nombre.</summary>
    WakeWord,

    /// <summary>Un recordatorio local llegó a su hora.</summary>
    Reminder
}

/// <summary>
/// Pedido de presencia: el runtime nunca toca la ventana, sólo avisa. El shell decide si la muestra.
/// </summary>
internal sealed record ShellActivationRequest(
    ShellActivationReason Reason,
    string Title,
    string Detail);

internal interface IAssistantRuntime : IAsyncDisposable
{
    event EventHandler<AssistantRuntimeUpdate>? Updated;

    /// <summary>Se dispara cuando Viernes necesita hacerse visible por su cuenta.</summary>
    event EventHandler<ShellActivationRequest>? ActivationRequested;

    bool IsMuted { get; set; }

    bool IsCloudConfigured { get; }

    bool IsWakeWordEnabled { get; }

    /// <summary>Si la activación por voz sigue viva cuando el orbe está oculto.</summary>
    bool IsListeningWhileHidden { get; }

    /// <summary>Cuerpo elegido para el orbe. Preferencia del usuario, se persiste local.</summary>
    Controls.OrbShape OrbShape { get; }

    Task SetOrbShapeAsync(Controls.OrbShape shape, CancellationToken cancellationToken);

    bool IsWakeWordDemo { get; }

    string RecognitionProviderName { get; }

    /// <summary>Cómo se llama el asistente. Lo eligió quien instaló y sale de las preferencias.</summary>
    string AssistantName { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string> SendAsync(string text, CancellationToken cancellationToken);

    Task StartPushToTalkAsync(CancellationToken cancellationToken);

    Task StopPushToTalkAsync(CancellationToken cancellationToken);

    Task CancelSpeechAsync(CancellationToken cancellationToken);

    Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task SetListenWhileHiddenAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Si hay una conversación abierta, en la que el micrófono vuelve solo tras cada respuesta.</summary>
    bool IsConversationActive { get; }

    Task StartConversationAsync(CancellationToken cancellationToken);

    Task EndConversationAsync(string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Cierra la conversación. Con <paramref name="quiet"/> vuelve al reposo sin dejar nada en
    /// pantalla, que es lo que corresponde cuando el cierre lo pidió el usuario.
    /// </summary>
    Task EndConversationAsync(string reason, bool quiet, CancellationToken cancellationToken);

    Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken);

    /// <summary>Hay autorización de gasto viva para hoy.</summary>
    bool HasSpendAuthorization { get; }

    /// <summary>Corta todo de inmediato, sin pasar por el modelo ni por la conversación.</summary>
    void Panic();

    Task ConfirmPendingAsync(CancellationToken cancellationToken);

    void DismissPending();
}
