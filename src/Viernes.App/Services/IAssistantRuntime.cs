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
    IReadOnlyList<BubbleListItem>? Items = null,
    bool ClearItems = false);

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

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string> SendAsync(string text, CancellationToken cancellationToken);

    Task StartPushToTalkAsync(CancellationToken cancellationToken);

    Task StopPushToTalkAsync(CancellationToken cancellationToken);

    Task CancelSpeechAsync(CancellationToken cancellationToken);

    Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task SetListenWhileHiddenAsync(bool enabled, CancellationToken cancellationToken);

    Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken);

    Task ConfirmPendingAsync(CancellationToken cancellationToken);

    void DismissPending();
}
