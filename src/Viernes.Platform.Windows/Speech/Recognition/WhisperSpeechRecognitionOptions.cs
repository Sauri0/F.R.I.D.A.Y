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

    /// <summary>
    /// Treinta milisegundos, no cien. Cada buffer es un tic: con 100 ms la forma sólo podía moverse
    /// diez veces por segundo — a saltos, no con la voz — y el fin de frase se decidía con esa misma
    /// grosería. A 30 ms hay 33 muestras por segundo, que alcanzan para que la animación siga la voz
    /// y para cortar apenas terminás de hablar.
    /// </summary>
    public int BufferMilliseconds { get; init; } = 30;

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
    /// Elige el modelo instalado que mejor equilibra precisión y velocidad, no el más grande.
    /// </summary>
    /// <remarks>
    /// Medido en una máquina real con la misma frase de 4,7 s: <c>small</c> transcribe en ×0,88 del
    /// tiempo real y acierta nombres propios; <c>turbo</c> tarda ×4,88 —cinco veces más— y encima
    /// castellaniza el rioplatense («recórdame» por «recordame»); <c>base</c> vuela a ×0,28 pero
    /// falla el nombre propio, que es justo lo que hay que entender. Por eso turbo va último: en un
    /// asistente conversacional, veinte segundos de espera pesan más que un acento bien puesto.
    /// </remarks>
    public static string GetDefaultModelPath()
    {
        var directory = GetDefaultModelDirectory();
        string[] preference =
        [
            "ggml-small.bin",
            "ggml-base.bin",
            "ggml-large-v3-turbo-q5_0.bin",
            "ggml-large-v3-turbo-q8_0.bin",
            "ggml-large-v3-turbo.bin"
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
