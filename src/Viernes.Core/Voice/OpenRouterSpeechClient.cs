using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
using Viernes.Core.OpenRouter;

namespace Viernes.Core.Voice;

/// <summary>Audio PCM de 16 bits mono junto con la frecuencia que informó el proveedor.</summary>
public sealed record SynthesizedSpeech(byte[] Pcm, int SampleRate);

/// <summary>
/// Voz neural a través de <c>/api/v1/audio/speech</c> de OpenRouter. Sustituye al sintetizador de
/// Windows, que es síntesis concatenativa y suena como tal.
/// </summary>
/// <remarks>
/// Pide PCM crudo en lugar de MP3 para que el host pueda reproducirlo sin decodificador: lo que
/// vuelve es exactamente lo que la placa de sonido necesita.
/// </remarks>
public sealed class OpenRouterSpeechClient
{
    private const int MaximumCharacters = 1_200;
    private const int MaximumAudioBytes = 12 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ViernesOptions _options;
    private readonly SpeechSynthesisOptions _speech;
    private readonly Uri _endpoint;

    public OpenRouterSpeechClient(
        HttpClient httpClient,
        ViernesOptions options,
        SpeechSynthesisOptions speech)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _speech = speech ?? throw new ArgumentNullException(nameof(speech));
        _endpoint = ResolveSpeechEndpoint(options.OpenRouterEndpoint);
    }

    public bool IsAvailable => _speech.IsEnabled && _options.HasApiKey;

    /// <summary>Último motivo por el que la voz remota no pudo hablar. Para diagnóstico, sin secretos.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Devuelve PCM de 16 bits mono con la frecuencia que informó el proveedor, o <c>null</c> si no
    /// se pudo. Nunca lanza por un fallo de red: el host cae al sintetizador local y sigue hablando.
    /// </summary>
    public async Task<SynthesizedSpeech?> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        // Este retorno temprano no dejaba motivo, y el host lo trazaba como «falló · sin motivo».
        // Decir «no hay API key» en vez de nada es la diferencia entre mirar el entorno y salir a
        // buscar un bug de red que no existe.
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            LastFailure = !_speech.IsEnabled
                ? "La voz neural está desactivada por configuración."
                : !_options.HasApiKey
                    ? "El proceso no tiene OPENROUTER_API_KEY en su entorno."
                    : "No había texto para sintetizar.";
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaximumCharacters)
        {
            trimmed = trimmed[..MaximumCharacters];
        }

        var payload = new
        {
            model = _speech.Model,
            input = trimmed,
            voice = _speech.Voice,
            response_format = "pcm",
            instructions = _speech.Instructions
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GetApiKey());
        request.Headers.TryAddWithoutValidation("X-Title", _options.ApplicationName);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Sin esto, un slug de modelo equivocado se ve igual que un corte de red: silencio.
                LastFailure = $"HTTP {(int)response.StatusCode} al pedir «{_speech.Model}» con voz «{_speech.Voice}».";
                return null;
            }

            if (response.Content.Headers.ContentLength > MaximumAudioBytes)
            {
                LastFailure = "El audio devuelto supera el límite seguro.";
                return null;
            }

            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (audio.Length is <= 0 or > MaximumAudioBytes)
            {
                LastFailure = "El audio devuelto está vacío o es demasiado grande.";
                return null;
            }

            LastFailure = null;
            return new SynthesizedSpeech(audio, ReadSampleRate(response.Content.Headers.ContentType?.ToString()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OpenRouterException or IOException)
        {
            // La voz es un complemento: si falla, el host habla con la voz local.
            LastFailure = $"{exception.GetType().Name} al contactar el servicio de voz.";
            return null;
        }
    }

    /// <summary>
    /// El proveedor informa la frecuencia en el <c>Content-Type</c>, por ejemplo
    /// <c>audio/pcm; rate=24000; channels=1</c>. Suponerla es lo que hace que la voz suene mal o no
    /// suene: distintos modelos devuelven distintas frecuencias.
    /// </summary>
    internal static int ReadSampleRate(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return SpeechSynthesisOptions.FallbackSampleRate;
        }

        foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("rate=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(part["rate=".Length..], out var rate) && rate is >= 8_000 and <= 192_000
                ? rate
                : SpeechSynthesisOptions.FallbackSampleRate;
        }

        return SpeechSynthesisOptions.FallbackSampleRate;
    }

    /// <summary>Deriva el endpoint de audio del de chat, sin exigir configurarlo por separado.</summary>
    public static Uri ResolveSpeechEndpoint(Uri chatEndpoint)
    {
        var absolute = chatEndpoint.AbsoluteUri;
        const string chatSuffix = "chat/completions";
        return absolute.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase)
            ? new Uri(string.Concat(absolute.AsSpan(0, absolute.Length - chatSuffix.Length), "audio/speech"))
            : new Uri(chatEndpoint, "audio/speech");
    }
}
