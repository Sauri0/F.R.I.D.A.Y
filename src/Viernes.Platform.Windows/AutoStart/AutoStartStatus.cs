namespace Viernes.Platform.Windows.AutoStart;

public sealed record AutoStartStatus(
    bool IsRegistered,
    bool IsConfiguredForCurrentExecutable,
    string? RegisteredCommand,
    string? CurrentExecutablePath,
    string? ErrorMessage = null)
{
    public bool IsAvailable => ErrorMessage is null;
}
