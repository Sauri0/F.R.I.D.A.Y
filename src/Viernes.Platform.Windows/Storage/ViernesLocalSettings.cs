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

    /// <summary>
    /// Mantiene la activación por voz mientras el orbe está oculto, para que Viernes pueda aparecer
    /// solo al ser llamado. Silenciar sigue siendo el corte duro que libera el micrófono.
    /// </summary>
    public bool ListenWhileHidden { get; init; } = true;

    /// <summary>
    /// Cuerpo del orbe elegido por el usuario: <c>Gota</c> o <c>Nube</c>. Es preferencia, no
    /// configuración del sistema: cambiarla no altera ninguna capacidad ni ningún permiso.
    /// </summary>
    public string OrbShape { get; init; } = "Gota";

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
