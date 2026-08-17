namespace Viernes.Platform.Windows.Speech.Recognition;

public sealed record WhisperSpeechRecognitionOptions
{
    public string ModelPath { get; init; } = GetDefaultModelPath();

    public string Language { get; init; } = "es";

    /// <summary>
    /// Dispositivo de entrada. <c>-1</c> es <c>WAVE_MAPPER</c>: el predeterminado de Windows, que es
    /// el mismo que usa SAPI para el wake word.
    /// </summary>
    /// <remarks>
    /// Fijarlo en 0 era un error silencioso: el dispositivo 0 no es el predeterminado sino el
    /// primero de la lista, y basta con tener instalado un micrófono virtual —Sonar, Voicemod,
    /// NVIDIA Broadcast, OBS— para que quede primero y entregue silencio. El wake word oía y la
    /// captura no, sobre la misma máquina.
    /// </remarks>
    public int InputDeviceNumber { get; init; } = ResolveDefaultInputDevice();

    public const int DefaultInputDevice = -1;

    private static int ResolveDefaultInputDevice()
    {
        var configured = Environment.GetEnvironmentVariable("VIERNES_INPUT_DEVICE");
        return int.TryParse(configured, out var parsed) && parsed >= -1
            ? parsed
            : DefaultInputDevice;
    }

    public int BufferMilliseconds { get; init; } = 100;

    public TimeSpan MinimumRecordingDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaximumRecordingDuration { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan CaptureStopTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public long MinimumModelSizeBytes { get; init; } = 1024 * 1024;

    public bool RequireModelUnderViernesLocalAppData { get; init; } = true;

    public static string GetDefaultModelDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("LOCALAPPDATA no está disponible.");
        }

        return Path.Combine(localApplicationData, "Viernes", "Models", "Whisper");
    }

    /// <summary>
    /// Elige el mejor modelo instalado, de más preciso a menos. Turbo es ~5x más rápido que
    /// large-v3 conservando casi toda la precisión; base queda como piso porque siempre estuvo.
    /// </summary>
    public static string GetDefaultModelPath()
    {
        var directory = GetDefaultModelDirectory();
        string[] preference =
        [
            "ggml-large-v3-turbo.bin",
            "ggml-large-v3-turbo-q8_0.bin",
            "ggml-large-v3-turbo-q5_0.bin",
            "ggml-small.bin",
            "ggml-base.bin"
        ];

        foreach (var candidate in preference)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, "ggml-base.bin");
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        if (InputDeviceNumber < -1 || BufferMilliseconds is < 20 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(InputDeviceNumber));
        }

        if (MinimumRecordingDuration <= TimeSpan.Zero ||
            MaximumRecordingDuration <= MinimumRecordingDuration ||
            CaptureStopTimeout <= TimeSpan.Zero || MinimumModelSizeBytes <= 0)
        {
            throw new ArgumentException("La configuración temporal de Whisper no es válida.");
        }
    }
}
