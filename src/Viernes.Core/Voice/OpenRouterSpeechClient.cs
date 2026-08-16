using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
using Viernes.Core.OpenRouter;

namespace Viernes.Core.Voice;

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

    /// <summary>
    /// Devuelve PCM 16 bits mono a 24 kHz, o <c>null</c> cuando la voz remota no está disponible.
    /// Nunca lanza por un fallo de red: el host cae al sintetizador local y sigue hablando.
    /// </summary>
    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
        {
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
                return null;
            }

            if (response.Content.Headers.ContentLength > MaximumAudioBytes)
            {
                return null;
            }

            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return audio.Length is > 0 and <= MaximumAudioBytes ? audio : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OpenRouterException or IOException)
        {
            // La voz es un complemento: si falla, el host habla con la voz local.
            return null;
        }
    }

    /// <summary>Deriva el endpoint de audio del de chat, sin exigir configurarlo por separado.</summary>
    internal static Uri ResolveSpeechEndpoint(Uri chatEndpoint)
    {
        var absolute = chatEndpoint.AbsoluteUri;
        const string chatSuffix = "chat/completions";
        return absolute.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase)
            ? new Uri(string.Concat(absolute.AsSpan(0, absolute.Length - chatSuffix.Length), "audio/speech"))
            : new Uri(chatEndpoint, "audio/speech");
    }
}
