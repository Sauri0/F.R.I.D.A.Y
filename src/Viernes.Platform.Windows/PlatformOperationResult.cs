namespace Viernes.Platform.Windows;

/// <summary>Resultado no excepcional de una operación dependiente de Windows.</summary>
public sealed record PlatformOperationResult(bool Succeeded, string? ErrorMessage = null)
{
    public static PlatformOperationResult Success() => new(true);

    public static PlatformOperationResult Failure(string errorMessage) => new(false, errorMessage);
}
