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
    bool Quiet = false);

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

    Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken);

    /// <summary>Hay autorización de gasto viva para hoy.</summary>
    bool HasSpendAuthorization { get; }

    /// <summary>Corta todo de inmediato, sin pasar por el modelo ni por la conversación.</summary>
    void Panic();

    Task ConfirmPendingAsync(CancellationToken cancellationToken);

    void DismissPending();
}
