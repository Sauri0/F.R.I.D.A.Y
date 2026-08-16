namespace Viernes.Core.Voice;

/// <summary>
/// Contract reserved for an entirely local wake-word implementation. Audio must not leave the
/// device; implementations that need a cloud service must not implement this interface.
/// </summary>
public interface ILocalWakeWordDetector
{
    bool IsAvailable { get; }

    bool IsRunning { get; }

    event EventHandler? Activated;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
