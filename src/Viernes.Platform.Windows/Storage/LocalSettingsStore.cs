using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Storage;

/// <summary>Persistencia atómica de preferencias bajo %LOCALAPPDATA%\Viernes.</summary>
public sealed class LocalSettingsStore : ILocalSettingsStore
{
    private const string SettingsFileName = "settings.json";
    private readonly JsonSerializerOptions _jsonOptions;

    public LocalSettingsStore(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? GetDefaultBaseDirectory());
        SettingsFilePath = Path.Combine(BaseDirectory, SettingsFileName);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public string BaseDirectory { get; }

    public string SettingsFilePath { get; }

    public async Task<LocalSettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new LocalSettingsLoadResult(new ViernesLocalSettings(), LoadedFromDisk: false);
        }

        try
        {
            await using var stream = new FileStream(
                SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var settings = await JsonSerializer.DeserializeAsync<ViernesLocalSettings>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (settings is null)
            {
                return FailedLoad("El archivo de preferencias está vacío o no es válido.");
            }

            return new LocalSettingsLoadResult(Normalize(settings), LoadedFromDisk: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return FailedLoad($"No se pudieron leer las preferencias locales: {exception.Message}");
        }
    }

    public async Task<PlatformOperationResult> SaveAsync(
        ViernesLocalSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            temporaryPath = Path.Combine(BaseDirectory, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(settings),
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            temporaryPath = null;
            return PlatformOperationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return PlatformOperationResult.Failure(
                $"No se pudieron guardar las preferencias locales: {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static string GetDefaultBaseDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Windows no informó una carpeta LOCALAPPDATA válida.");
        }

        return Path.Combine(localApplicationData, "Viernes");
    }

    private static ViernesLocalSettings Normalize(ViernesLocalSettings settings)
    {
        var recognitionCulture = NormalizeCulture(settings.RecognitionCulture);

        return settings with
        {
            SchemaVersion = ViernesLocalSettings.CurrentSchemaVersion,
            VoiceActivation = Enum.IsDefined(settings.VoiceActivation)
                ? settings.VoiceActivation
                : VoiceActivationMode.LocalWakeWord,
            WakeWordPhrases = NormalizeWakeWordPhrases(settings.WakeWordPhrases),
            RecognitionCulture = recognitionCulture,
            PreferredVoiceName = NormalizeOptionalText(settings.PreferredVoiceName, 128),
            PreferredRecognitionProvider = Enum.IsDefined(settings.PreferredRecognitionProvider)
                ? settings.PreferredRecognitionProvider
                : SpeechRecognitionProviderKind.WhisperLocal,
            OrbShape = string.Equals(settings.OrbShape?.Trim(), "Nube", StringComparison.OrdinalIgnoreCase)
                ? "Nube"
                : "Gota",
            WhisperModelPath = NormalizeWhisperModelPath(settings.WhisperModelPath),
            PreferredOpenRouterModel = NormalizeOptionalText(settings.PreferredOpenRouterModel, 200),
            WidgetLeft = NormalizeCoordinate(settings.WidgetLeft),
            WidgetTop = NormalizeCoordinate(settings.WidgetTop)
        };
    }

    private static string NormalizeCulture(string? cultureName)
    {
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try
            {
                return CultureInfo.GetCultureInfo(cultureName.Trim()).Name;
            }
            catch (CultureNotFoundException)
            {
                // Una preferencia corrupta vuelve al idioma seguro por defecto.
            }
        }

        return "es-AR";
    }

    private static string? NormalizeOptionalText(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string? NormalizeWhisperModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var modelPath = Path.GetFullPath(value.Trim());
            var root = Path.GetFullPath(WhisperSpeechRecognitionOptions.GetDefaultModelDirectory());
            var relative = Path.GetRelativePath(root, modelPath);
            return !Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                string.Equals(Path.GetExtension(modelPath), ".bin", StringComparison.OrdinalIgnoreCase)
                    ? modelPath
                    : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> NormalizeWakeWordPhrases(IReadOnlyList<string>? phrases)
    {
        if (phrases is null)
        {
            return ["Viernes", "Hola Viernes"];
        }

        var normalized = phrases
            .Where(phrase => phrase is not null)
            .Select(phrase => string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(phrase => phrase.Length is >= 2 and <= 40 && !phrase.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return normalized.Length == 0 ? ["Viernes", "Hola Viernes"] : normalized;
    }

    private static double? NormalizeCoordinate(double? value) =>
        value is { } coordinate && double.IsFinite(coordinate) ? coordinate : null;

    private static LocalSettingsLoadResult FailedLoad(string errorMessage) =>
        new(new ViernesLocalSettings(), LoadedFromDisk: false, errorMessage);

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
