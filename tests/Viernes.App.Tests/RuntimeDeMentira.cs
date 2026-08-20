using Viernes.App.Controls;
using Viernes.App.Services;
using Viernes.App.ViewModels;

namespace Viernes.App.Tests;

/// <summary>Lo mínimo para que el modelo de vista exista: sólo el nombre importa acá.</summary>
internal sealed class RuntimeDeMentira(string name) : IAssistantRuntime
{
    // Con cuerpo vacío y no como campo: el modelo de vista se engancha en su constructor, acá
    // nadie los dispara, y un evento declarado y nunca disparado es una advertencia.
    public event EventHandler<AssistantRuntimeUpdate>? Updated
    {
        add { }
        remove { }
    }

    public event EventHandler<ShellActivationRequest>? ActivationRequested
    {
        add { }
        remove { }
    }

    public string AssistantName { get; private set; } = name;

    public bool IsMuted { get; set; }

    public bool IsCloudConfigured => false;

    public bool IsWakeWordEnabled => false;

    public bool IsListeningWhileHidden => false;

    public OrbShape OrbShape => OrbShape.Gota;

    public bool FollowsActiveMonitor => false;

    public bool IsWakeWordDemo => true;

    public string RecognitionProviderName => "de mentira";

    public bool IsConversationActive => false;

    public bool HasSpendAuthorization => false;

    // Las claves las anota y nada más: esta clase existe para probar el renombrado, y guardar
    // credenciales de verdad desde una prueba tocaría el entorno de quien la corre.
    public string? UltimaClaveRouter { get; private set; }

    public string? UltimaClaveGoogle { get; private set; }

    public CredentialsState DescribeCredentials() => new(
        HasOpenRouter: !string.IsNullOrEmpty(this.UltimaClaveRouter),
        HasGoogle: !string.IsNullOrEmpty(this.UltimaClaveGoogle),
        OpenRouterShadowed: false);

    public Task<CredentialsResult> SetCredentialsAsync(
        string? openRouterKey,
        string? googleKey,
        CancellationToken cancellationToken)
    {
        if (openRouterKey is not null) { this.UltimaClaveRouter = openRouterKey; }
        if (googleKey is not null) { this.UltimaClaveGoogle = googleKey; }
        return Task.FromResult(new CredentialsResult());
    }

    public Task<AssistantRenameResult> SetAssistantNameAsync(
        string? nombre,
        CancellationToken cancellationToken)
    {
        this.AssistantName = nombre ?? this.AssistantName;
        return Task.FromResult(new AssistantRenameResult(true, this.AssistantName));
    }

    public Task SetOrbShapeAsync(OrbShape shape, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public double OrbScale { get; private set; } = Viernes.Core.Configuration.OrbScaleRange.Default;

    /// <summary>Anota el tamaño y nada más: acá no hay disco al que escribirlo.</summary>
    public Task SetOrbScaleAsync(double scale, CancellationToken cancellationToken)
    {
        this.VecesQueSeGuardoElTamano++;
        this.OrbScale = Viernes.Core.Configuration.OrbScaleRange.Clamp(scale);
        return Task.CompletedTask;
    }

    /// <summary>Cuántas veces se pidió guardar el tamaño. Un gesto de la barra tiene que ser uno.</summary>
    public int VecesQueSeGuardoElTamano { get; private set; }

    public Task SetFollowActiveMonitorAsync(bool follow, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> SendAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);

    public Task StartPushToTalkAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopPushToTalkAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CancelSpeechAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetWakeWordEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SetListenWhileHiddenAsync(bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StartConversationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EndConversationAsync(string reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EndConversationAsync(string reason, bool quiet, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SetShellVisibilityAsync(bool visible, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void Panic()
    {
    }

    public Task ConfirmPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void DismissPending()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
