namespace Viernes.Platform.Windows.AutoStart;

/// <summary>Administra el inicio de Viernes para el usuario actual.</summary>
public interface IAutoStartService
{
    AutoStartStatus GetStatus(string? executablePath = null);

    PlatformOperationResult Enable(string? executablePath = null);

    PlatformOperationResult Disable();
}
