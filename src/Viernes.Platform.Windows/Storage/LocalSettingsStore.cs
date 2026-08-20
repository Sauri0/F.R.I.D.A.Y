using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
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

            var texto = await new StreamReader(stream).ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(texto))
            {
                return FailedLoad("El archivo de preferencias está vacío.");
            }

            try
            {
                var settings = JsonSerializer.Deserialize<ViernesLocalSettings>(texto, _jsonOptions);
                if (settings is not null)
                {
                    return new LocalSettingsLoadResult(Normalize(settings), LoadedFromDisk: true);
                }
            }
            catch (JsonException)
            {
                // Un campo solo no puede costar el archivo entero. Ver RescatarLoQueSirva.
            }

            return RescatarLoQueSirva(texto);
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
            AssistantName = AssistantIdentity.Normalize(settings.AssistantName),
            WakeWordPhrases = NormalizeWakeWordPhrases(settings.WakeWordPhrases),
            RecognitionCulture = recognitionCulture,
            PreferredVoiceName = NormalizeOptionalText(settings.PreferredVoiceName, 128),
            PreferredRecognitionProvider = Enum.IsDefined(settings.PreferredRecognitionProvider)
                ? settings.PreferredRecognitionProvider
                : SpeechRecognitionProviderKind.WhisperLocal,
            OrbShape = string.Equals(settings.OrbShape?.Trim(), "Nube", StringComparison.OrdinalIgnoreCase)
                ? "Nube"
                : "Gota",

            // Un archivo escrito a mano con 50 —queriendo decir «50 %»— pediría un orbe de 5400 px
            // que no entra en ninguna pantalla y que además se lleva puesto el ancho de la ventana.
            OrbScale = OrbScaleRange.Clamp(settings.OrbScale),
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

    /// <summary>
    /// Deja pasar sólo las frases escritas a mano; ausencia y basura vuelven a <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> no es un fallo acá, es la respuesta normal: significa «derivalas del nombre».
    /// Devolver una lista fija de fábrica sería justamente el error que rompe el renombrado.
    /// </remarks>
    private static IReadOnlyList<string>? NormalizeWakeWordPhrases(IReadOnlyList<string>? phrases)
    {
        if (phrases is null)
        {
            return null;
        }

        var normalized = phrases
            .Where(phrase => phrase is not null)
            .Select(phrase => string.Join(' ', phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(phrase => phrase.Length is >= 2 and <= 40 && !phrase.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static double? NormalizeCoordinate(double? value) =>
        value is { } coordinate && double.IsFinite(coordinate) ? coordinate : null;

    /// <summary>
    /// Salva campo por campo lo que el archivo tenga de bueno, en vez de tirarlo entero.
    /// </summary>
    /// <remarks>
    /// <b>Esto existe por la promesa de que actualizar no hace perder nada.</b> Antes, cualquier
    /// campo con el tipo cambiado —una versión futura que convierta <c>orbShape</c> de texto a
    /// objeto, alguien que edite el archivo a mano y se coma una comilla— hacía que
    /// <c>DeserializeAsync</c> tirara, y el <c>catch</c> devolvía un <see cref="ViernesLocalSettings"/>
    /// <b>nuevo y vacío</b>. No se perdía el campo malo: se perdía todo. El asistente volvía a
    /// llamarse Viernes, el orbe a ser una gota, la posición se olvidaba, y el primer guardado
    /// escribía eso encima del archivo. En silencio.
    /// <para>
    /// Acá cada propiedad se prueba sola —se arma un objeto de una sola clave y se intenta leerlo—
    /// y sólo se descartan las que no entran. Con quince campos buenos y uno roto, se conservan
    /// quince. Es lento, y no importa: corre una vez por arranque y sólo cuando el archivo ya falló.
    /// </para>
    /// <para>
    /// Si el JSON no se puede ni parsear —le falta una llave, está truncado— no hay nada que
    /// rescatar, y entonces se hace la otra mitad del trabajo: el archivo se guarda a un costado
    /// antes de que el primer guardado lo pise, así que lo que había sigue existiendo.
    /// </para>
    /// </remarks>
    private LocalSettingsLoadResult RescatarLoQueSirva(string texto)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(texto);
        }
        catch (JsonException exception)
        {
            return NoSePudo($"El archivo de preferencias no se pudo leer: {exception.Message}", texto);
        }

        using (documento)
        {
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
            {
                return NoSePudo("El archivo de preferencias no tiene la forma esperada.", texto);
            }

            var buenas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var descartadas = new List<string>();

            foreach (var propiedad in documento.RootElement.EnumerateObject())
            {
                // Cada campo se prueba en un objeto de una sola clave: si entra, entra solo.
                var sola = "{" + JsonSerializer.Serialize(propiedad.Name) + ":" + propiedad.Value.GetRawText() + "}";
                try
                {
                    _ = JsonSerializer.Deserialize<ViernesLocalSettings>(sola, _jsonOptions);
                    buenas[propiedad.Name] = propiedad.Value.Clone();
                }
                catch (JsonException)
                {
                    descartadas.Add(propiedad.Name);
                }
            }

            if (descartadas.Count == 0)
            {
                // Todos entran de a uno pero no juntos: no sé rescatar eso y forzarlo sería adivinar.
                return NoSePudo("El archivo de preferencias no se pudo interpretar.", texto);
            }

            var rearmado = new Dictionary<string, JsonElement>(buenas, StringComparer.Ordinal);

            try
            {
                var settings = JsonSerializer.Deserialize<ViernesLocalSettings>(
                    JsonSerializer.Serialize(rearmado, _jsonOptions),
                    _jsonOptions);

                if (settings is not null)
                {
                    return new LocalSettingsLoadResult(
                        Normalize(settings),
                        LoadedFromDisk: true,
                        "Se descartaron preferencias que no se pudieron leer y se conservó el resto: " +
                        string.Join(", ", descartadas) + ".");
                }
            }
            catch (JsonException)
            {
            }

            return NoSePudo("El archivo de preferencias no se pudo interpretar.", texto);
        }
    }

    /// <summary>Falla dejando una copia del archivo ilegible al lado, y diciendo dónde quedó.</summary>
    private LocalSettingsLoadResult NoSePudo(string motivo, string texto)
    {
        var copia = GuardarAUnCostado(texto);
        return FailedLoad(copia is null
            ? motivo
            : motivo + $" Se guardó una copia en {copia} antes de empezar de nuevo.");
    }

    /// <summary>
    /// Deja una copia del archivo ilegible al lado, y devuelve dónde quedó.
    /// </summary>
    /// <remarks>
    /// El archivo que no se pudo leer se va a pisar en el primer guardado. Copiarlo antes cuesta una
    /// llamada y es la diferencia entre «perdiste tus preferencias» y «están acá al lado».
    /// </remarks>
    private string? GuardarAUnCostado(string texto)
    {
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            var destino = Path.Combine(
                BaseDirectory,
                $"{SettingsFileName}.roto-{DateTime.Now:yyyyMMdd-HHmmss}");

            if (!File.Exists(destino))
            {
                File.WriteAllText(destino, texto);
            }

            return destino;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
