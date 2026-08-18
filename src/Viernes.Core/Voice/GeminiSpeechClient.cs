using System.Net.Http.Json;
using System.Text.Json;

namespace Viernes.Core.Voice;

/// <summary>
/// La voz, por Google. Aoede, con el registro que corresponda al momento.
/// </summary>
/// <remarks>
/// El usuario escuchó las catorce voces femeninas que existen y eligió Aoede. Y pidió algo más
/// difícil que elegir una voz: que <em>varíe</em> —«debe tener humanidad y variación, no siempre
/// estructurada»—. Como la API no expone velocidad ni tono, la variación entra por la indicación de
/// interpretación que va delante del texto; de armarla se ocupa <see cref="VoiceRegister"/>.
/// <para>
/// Esto reemplaza a la voz por OpenRouter, no la borra: sigue habiendo caída a la voz del sistema si
/// esto falla, que es lo que evita quedarse mudo por un problema de red.
/// </para>
/// </remarks>
public sealed class GeminiSpeechClient
{
    /// <summary>Modelo de voz. Verificado disponible en la cuenta del usuario.</summary>
    public const string DefaultModel = "gemini-3.1-flash-tts-preview";

    /// <summary>El audio vuelve siempre a esta frecuencia; no se negocia ni se informa.</summary>
    public const int OutputSampleRate = 24_000;

    private const int MaximumAudioBytes = 12 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKey;
    private readonly TimeProvider _time;

    public GeminiSpeechClient(HttpClient httpClient, Func<string?> apiKey, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Lo último que salió mal, para poder decirlo en vez de quedarse callada.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Hay con qué hablar.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey());

    /// <summary>
    /// Convierte texto en audio, dicho como corresponde a este momento.
    /// </summary>
    /// <remarks>
    /// Devuelve <c>null</c> —y deja el motivo en <see cref="LastFailure"/>— en vez de lanzar: la voz
    /// es un complemento y quedarse sin ella no puede tumbar el turno. Quien llama cae a la voz del
    /// sistema.
    /// </remarks>
    public async Task<SynthesizedSpeech?> SynthesizeAsync(
        string text,
        VoiceMoment moment = VoiceMoment.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            LastFailure = "No hay clave de Google configurada.";
            return null;
        }

        var direction = VoiceRegister.For(moment, TimeOnly.FromDateTime(_time.GetLocalNow().DateTime));

        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = $"{direction}\n\nDecí exactamente esto:\n{text}" } } }
            },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new { voiceName = VoiceRegister.DefaultVoice }
                    }
                }
            }
        };

        try
        {
            using var response = await _httpClient
                .PostAsJsonAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models/{DefaultModel}:generateContent?key={key}",
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // El 503 por demanda es frecuente y pasajero: se distingue para que quien llama
                // pueda decidir reintentar en vez de tratarlo como una falla de configuración.
                LastFailure = response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    ? "El modelo de voz está saturado; probá de nuevo en un momento."
                    : $"HTTP {(int)response.StatusCode} al pedir la voz.";
                return null;
            }

            using var document = await JsonDocument
                .ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var data = ReadAudio(document.RootElement);
            if (data is null)
            {
                LastFailure = "La respuesta no trajo audio.";
                return null;
            }

            var pcm = Convert.FromBase64String(data);
            if (pcm.Length is <= 0 or > MaximumAudioBytes)
            {
                LastFailure = "El audio devuelto está vacío o es demasiado grande.";
                return null;
            }

            LastFailure = null;
            return new SynthesizedSpeech(pcm, OutputSampleRate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or JsonException
            or FormatException
            or IOException)
        {
            LastFailure = $"No pude pedir la voz: {exception.GetType().Name}";
            return null;
        }
    }

    /// <summary>
    /// Saca el audio de la respuesta sin asumir en qué parte viene.
    /// </summary>
    /// <remarks>
    /// El modelo puede devolver varias <c>parts</c> en un mismo candidato, y la de audio no siempre
    /// es la primera. Buscar la que tenga <c>inlineData</c> en vez de indexar en cero es lo que evita
    /// quedarse mudo porque llegó un texto adelante.
    /// </remarks>
    private static string? ReadAudio(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var inline) &&
                    inline.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.String)
                {
                    return data.GetString();
                }
            }
        }

        return null;
    }
}
