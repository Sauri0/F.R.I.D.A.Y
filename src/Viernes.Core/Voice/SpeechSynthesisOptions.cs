namespace Viernes.Core.Voice;

/// <summary>
/// Configuración de la voz neural remota. Ninguna de estas variables es secreta: la única
/// credencial sigue siendo <c>OPENROUTER_API_KEY</c>.
/// </summary>
/// <remarks>
/// A OpenRouter se le manda <em>texto</em> y devuelve audio. El micrófono no interviene, así que
/// esto no cambia la promesa de que el audio del usuario no sale de la máquina.
/// </remarks>
public sealed class SpeechSynthesisOptions
{
    public const string EnabledEnvironmentVariable = "VIERNES_TTS_REMOTE";
    public const string ModelEnvironmentVariable = "VIERNES_TTS_MODEL";
    public const string VoiceEnvironmentVariable = "VIERNES_TTS_VOICE";
    public const string InstructionsEnvironmentVariable = "VIERNES_TTS_INSTRUCTIONS";

    /// <summary>
    /// Voz elegida por el dueño escuchando cuatro candidatas decir la misma frase. Es la más humana
    /// de las probadas; a cambio tarda ~1,4 s en una frase corta contra 558 ms de mai-voice-2, y por
    /// eso el corte por oraciones importa tanto: el primer tramo es corto y sale antes.
    /// </summary>
    public const string DefaultModel = "x-ai/grok-voice-tts-1.0";
    public const string DefaultVoice = "Ara";

    /// <summary>Alternativa medida como la más rápida, si alguna vez pesa más la latencia.</summary>
    public const string FastestModel = "microsoft/mai-voice-2";
    public const string FastestVoice = "es-AR-ElenaNeural";

    /// <summary>
    /// Frecuencia de reserva. La real la informa el proveedor en el <c>Content-Type</c> y hay que
    /// usar ésa: no todos devuelven lo mismo —fish-audio manda 44,1 kHz y este 24 kHz—, y reproducir
    /// PCM a la frecuencia equivocada suena a otra voz o directamente a nada.
    /// </summary>
    public const int FallbackSampleRate = 24_000;
    public const int PcmBitsPerSample = 16;
    public const int PcmChannels = 1;

    private readonly string _model = DefaultModel;
    private readonly string _voice = DefaultVoice;
    private readonly string? _instructions;

    public bool IsEnabled { get; init; }

    public string Model
    {
        get => _model;
        init => _model = string.IsNullOrWhiteSpace(value) ? DefaultModel : value.Trim();
    }

    public string Voice
    {
        get => _voice;
        init => _voice = string.IsNullOrWhiteSpace(value) ? DefaultVoice : value.Trim();
    }

    /// <summary>Indicación de tono. La soportan algunos proveedores y el resto la ignora.</summary>
    public string? Instructions
    {
        get => _instructions;
        init
        {
            var normalized = value?.Trim();
            _instructions = string.IsNullOrEmpty(normalized) || normalized.Length > 600
                ? null
                : normalized;
        }
    }

    public static SpeechSynthesisOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;
        return new SpeechSynthesisOptions
        {
            IsEnabled = readVariable(EnabledEnvironmentVariable)?.Trim().ToLowerInvariant()
                is not ("0" or "false" or "off"),
            Model = readVariable(ModelEnvironmentVariable) ?? DefaultModel,
            Voice = readVariable(VoiceEnvironmentVariable) ?? DefaultVoice,
            Instructions = readVariable(InstructionsEnvironmentVariable)
                ?? "Hablás en español rioplatense, con calidez y naturalidad. Ritmo de conversación, no de locutor."
        };
    }
}
