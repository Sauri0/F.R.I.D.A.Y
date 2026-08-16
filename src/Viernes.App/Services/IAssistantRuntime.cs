using Viernes.App.ViewModels;

namespace Viernes.App.Services;

internal sealed record AssistantRuntimeUpdate(
    AssistantVisualState State,
    string Status,
    string? Message = null,
    bool? MicrophoneActive = null,
    bool? WakeWordEnabled = null,
    PendingConfirmation? Confirmation = null,
    bool ClearConfirmation = false);

internal sealed record PendingConfirmation(
    string ToolCallId,
    string Title,
    string Detail);

internal interface IAssistantRuntime : IAsyncDisposable
{
    event EventHandler<AssistantRuntimeUpdate>? Updated;

    bool IsMuted { get; set; }

    bool IsCloudConfigured { get; }

    bool IsWakeWordEnabled { get; }

    bool IsWakeWordDemo { get; }

    string RecognitionProviderName { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task<string> SendAsync(string text, CancellationToken cancellationToken);

    Task StartPushToTalkAsync(CancellationToken cancellationToken);

    Task StopPushToTalkAsync(CancellationToken cancellationToken);

    Task CancelSpeechAsync(CancellationToken cancellationToken);

    Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken);

    Task ConfirmPendingAsync(CancellationToken cancellationToken);

    void DismissPending();
}
