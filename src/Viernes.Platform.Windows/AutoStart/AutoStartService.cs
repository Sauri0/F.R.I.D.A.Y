using Microsoft.Win32;
using System.Security;

namespace Viernes.Platform.Windows.AutoStart;

/// <summary>
/// Configura el valor HKCU Run de Viernes. Nunca requiere privilegios de administrador
/// ni modifica la configuración de otros usuarios.
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly Func<string?> _executablePathProvider;
    private readonly string _valueName;

    public AutoStartService(
        string valueName = "Viernes",
        Func<string?>? executablePathProvider = null)
    {
        if (string.IsNullOrWhiteSpace(valueName))
        {
            throw new ArgumentException("El nombre del valor de inicio no puede estar vacío.", nameof(valueName));
        }

        _valueName = valueName.Trim();
        _executablePathProvider = executablePathProvider ?? (() => Environment.ProcessPath);
    }

    public AutoStartStatus GetStatus(string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnavailableStatus("El inicio automático solo está disponible en Windows.");
        }

        try
        {
            var currentPath = TryResolveExecutablePath(executablePath, requireExistingFile: false, out var pathError);
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var registeredCommand = key?.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var isRegistered = !string.IsNullOrWhiteSpace(registeredCommand);
            var expectedCommand = currentPath is null ? null : QuoteExecutablePath(currentPath);

            return new AutoStartStatus(
                isRegistered,
                isRegistered && expectedCommand is not null &&
                    string.Equals(registeredCommand, expectedCommand, StringComparison.OrdinalIgnoreCase),
                registeredCommand,
                currentPath,
                pathError);
        }
        catch (Exception exception) when (IsExpectedPlatformFailure(exception))
        {
            return UnavailableStatus("No se pudo consultar el inicio automático.", exception.Message);
        }
    }

    public PlatformOperationResult Enable(string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PlatformOperationResult.Failure("El inicio automático solo está disponible en Windows.");
        }

        var resolvedPath = TryResolveExecutablePath(executablePath, requireExistingFile: true, out var pathError);
        if (resolvedPath is null)
        {
            return PlatformOperationResult.Failure(pathError ?? "No se pudo determinar el ejecutable de Viernes.");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return PlatformOperationResult.Failure("Windows no permitió abrir la configuración de inicio del usuario.");
            }

            // Las comillas son obligatorias para que rutas con espacios no se interpreten como comandos distintos.
            key.SetValue(_valueName, QuoteExecutablePath(resolvedPath), RegistryValueKind.String);
            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (IsExpectedPlatformFailure(exception))
        {
            return PlatformOperationResult.Failure($"No se pudo activar el inicio automático: {exception.Message}");
        }
    }

    public PlatformOperationResult Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return PlatformOperationResult.Failure("El inicio automático solo está disponible en Windows.");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
            return PlatformOperationResult.Success();
        }
        catch (Exception exception) when (IsExpectedPlatformFailure(exception))
        {
            return PlatformOperationResult.Failure($"No se pudo desactivar el inicio automático: {exception.Message}");
        }
    }

    public static string QuoteExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("La ruta del ejecutable contiene comillas no válidas.", nameof(executablePath));
        }

        return $"\"{executablePath}\"";
    }

    private string? TryResolveExecutablePath(
        string? executablePath,
        bool requireExistingFile,
        out string? errorMessage)
    {
        try
        {
            var candidate = string.IsNullOrWhiteSpace(executablePath)
                ? _executablePathProvider()
                : executablePath;

            if (string.IsNullOrWhiteSpace(candidate))
            {
                errorMessage = "No se pudo determinar la ruta del ejecutable de Viernes.";
                return null;
            }

            if (candidate.Contains('"', StringComparison.Ordinal))
            {
                errorMessage = "La ruta del ejecutable contiene comillas no válidas.";
                return null;
            }

            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "El inicio automático requiere una ruta a un archivo .exe.";
                return null;
            }

            if (requireExistingFile && !File.Exists(fullPath))
            {
                errorMessage = "El ejecutable indicado no existe.";
                return null;
            }

            errorMessage = null;
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorMessage = $"La ruta del ejecutable no es válida: {exception.Message}";
            return null;
        }
    }

    private static AutoStartStatus UnavailableStatus(string message, string? detail = null) =>
        new(false, false, null, null, detail is null ? message : $"{message} {detail}");

    private static bool IsExpectedPlatformFailure(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException;
}
