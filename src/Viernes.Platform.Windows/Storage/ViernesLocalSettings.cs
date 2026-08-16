using Viernes.Platform.Windows.Speech.Recognition;

namespace Viernes.Platform.Windows.Storage;

/// <summary>
/// Preferencias locales no sensibles. Deliberadamente no contiene claves, tokens ni credenciales.
/// </summary>
public sealed record ViernesLocalSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool MicrophoneMuted { get; init; }

    public bool StartWithWindows { get; init; }

    /// <summary>Wake-word local es el modo normal; PTT sigue disponible como control privado.</summary>
    public VoiceActivationMode VoiceActivation { get; init; } = VoiceActivationMode.LocalWakeWord;

    public IReadOnlyList<string> WakeWordPhrases { get; init; } = ["Viernes", "Hola Viernes"];

    public string RecognitionCulture { get; init; } = "es-AR";

    public string? PreferredVoiceName { get; init; }

    public SpeechRecognitionProviderKind PreferredRecognitionProvider { get; init; } =
        SpeechRecognitionProviderKind.WhisperLocal;

    /// <summary>Ruta local del modelo GGML; nunca contiene credenciales ni dispara descargas.</summary>
    public string? WhisperModelPath { get; init; }

    /// <summary>Identificador público de modelo; la clave de OpenRouter nunca se guarda aquí.</summary>
    public string? PreferredOpenRouterModel { get; init; }

    public double? WidgetLeft { get; init; }

    public double? WidgetTop { get; init; }
}
