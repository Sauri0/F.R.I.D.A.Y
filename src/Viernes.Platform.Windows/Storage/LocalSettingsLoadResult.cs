namespace Viernes.Platform.Windows.Storage;

public sealed record LocalSettingsLoadResult(
    ViernesLocalSettings Settings,
    bool LoadedFromDisk,
    string? ErrorMessage = null)
{
    public bool Succeeded => ErrorMessage is null;
}
