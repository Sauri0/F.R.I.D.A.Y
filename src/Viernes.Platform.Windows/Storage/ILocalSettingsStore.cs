namespace Viernes.Platform.Windows.Storage;

public interface ILocalSettingsStore
{
    string BaseDirectory { get; }

    string SettingsFilePath { get; }

    Task<LocalSettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<PlatformOperationResult> SaveAsync(
        ViernesLocalSettings settings,
        CancellationToken cancellationToken = default);
}
