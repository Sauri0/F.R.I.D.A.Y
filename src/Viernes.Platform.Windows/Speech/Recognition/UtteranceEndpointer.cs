namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>Por qué se dio por terminada una frase.</summary>
public enum UtteranceStopReason
{
    /// <summary>Todavía no terminó.</summary>
    None = 0,

    /// <summary>Nadie habló dentro del plazo inicial.</summary>
    InitialSilence,

    /// <summary>Habló y se calló el tiempo suficiente.</summary>
    EndSilence,

    /// <summary>Se llegó al máximo permitido sin que se callara.</summary>
    MaximumDuration
}

/// <summary>Plazos con los que se decide dónde empieza y dónde termina una frase.</summary>
public sealed record UtteranceEndpointerOptions
{
    /// <summary>Cuánto se espera a que alguien empiece a hablar.</summary>
    public TimeSpan InitialSilenceTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Cuánto silencio hace falta después de hablar para dar la frase por cerrada.</summary>
    public TimeSpan EndSilenceTimeout { get; init; } = TimeSpan.FromMilliseconds(850);

    /// <summary>Tope duro, por si nunca se calla.</summary>
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cuánta energía de voz sostenida hace falta para dar por empezada la frase.
    /// </summary>
    /// <remarks>
    /// Eran 240 ms y hacían falta seguidos. Un «sí», un «dale» o un «listo» tienen unos 200 ms de
    /// núcleo vocálico: no llegaban nunca. Con 150 ms y decaimiento en vez de reinicio, la palabra
    /// corta entra y el portazo —dos bloques y se apaga— sigue afuera.
    /// </remarks>
    public TimeSpan RequiredVoiceEnergy { get; init; } = TimeSpan.FromMilliseconds(150);

    internal void Validate()
    {
        if (InitialSilenceTimeout <= TimeSpan.Zero ||
            EndSilenceTimeout < TimeSpan.FromMilliseconds(200) ||
            EndSilenceTimeout > TimeSpan.FromSeconds(3) ||
            MaximumDuration <= InitialSilenceTimeout ||
            MaximumDuration > TimeSpan.FromSeconds(120) ||
            RequiredVoiceEnergy <= TimeSpan.Zero)
        {
            throw new ArgumentException("Los plazos de fin de frase no son válidos.");
        }
    }
}

/// <summary>
/// Decide, sobre una secuencia de veredictos del detector, dónde empieza y dónde termina la frase.
/// </summary>
/// <remarks>
/// Está separado del detector a propósito: el detector dice si <em>este</em> bloque es voz, y esto
/// dice si <em>ya empezó a hablar</em> y si <em>ya terminó</em>. Mezclarlos era lo que había antes,
/// y significaba que cambiar de detector cambiaba también el criterio de fin de frase — o sea que
/// comparar dos detectores comparaba dos cosas a la vez y no se sabía cuál explicaba la diferencia.
/// <para>
/// Además es lógica pura sobre una secuencia de valores: se puede probar entera sin micrófono, que
/// es exactamente lo que no se podía hacer cuando vivía adentro de la sesión de captura.
/// </para>
/// </remarks>
public sealed class UtteranceEndpointer
{
    private readonly UtteranceEndpointerOptions _options;
    private TimeSpan _voiceEnergy;
    private TimeSpan _trailingSilence;
    private TimeSpan _elapsed;

    /// <summary>Arma el detector de fin de frase con los plazos dados.</summary>
    public UtteranceEndpointer(UtteranceEndpointerOptions? options = null)
    {
        _options = options ?? new UtteranceEndpointerOptions();
        _options.Validate();
    }

    /// <summary>Si ya se dio por empezada la frase.</summary>
    public bool VoiceStarted { get; private set; }

    /// <summary>Cuánto audio se lleva observado.</summary>
    public TimeSpan Elapsed => _elapsed;

    /// <summary>En qué momento del audio se dio por empezada la voz.</summary>
    public TimeSpan VoiceStartedAt { get; private set; }

    /// <summary>Silencio acumulado desde la última voz, una vez empezada la frase.</summary>
    public TimeSpan TrailingSilence => _trailingSilence;

    /// <summary>Por qué terminó, si terminó.</summary>
    public UtteranceStopReason StopReason { get; private set; }

    /// <summary>
    /// Suma un bloque ya juzgado por el detector y devuelve si con eso la frase terminó.
    /// </summary>
    /// <param name="isVoice">Lo que dijo el detector para este bloque.</param>
    /// <param name="frameDuration">Cuánto dura el bloque.</param>
    /// <returns><see cref="UtteranceStopReason.None"/> mientras siga abierta.</returns>
    public UtteranceStopReason Observe(bool isVoice, TimeSpan frameDuration)
    {
        if (StopReason != UtteranceStopReason.None)
        {
            return StopReason;
        }

        _elapsed += frameDuration;
        if (isVoice)
        {
            _voiceEnergy += frameDuration;
            if (_voiceEnergy >= _options.RequiredVoiceEnergy)
            {
                if (!VoiceStarted)
                {
                    VoiceStarted = true;

                    // El comienzo real es donde arrancó la energía, no donde se confirmó: si no, la
                    // frase que se manda a transcribir empieza 150 ms tarde y se come la primera
                    // consonante.
                    VoiceStartedAt = _elapsed - _voiceEnergy;
                }

                _trailingSilence = TimeSpan.Zero;
            }
        }
        else if (VoiceStarted)
        {
            // Un bache corto no reinicia nada: adentro de una frase hay micro-silencios.
            _trailingSilence += frameDuration;
        }
        else
        {
            // Decaer en vez de reiniciar conserva lo que distingue a la voz del golpe. Un portazo
            // son dos bloques y vuelve a cero enseguida; una palabra corta son cinco o seis seguidos
            // y cruza el piso igual. Antes esto ponía la cuenta en cero y hacían falta 240 ms
            // *seguidos*: un «dale» no llegaba nunca y la captura devolvía texto vacío.
            _voiceEnergy = _voiceEnergy > frameDuration
                ? _voiceEnergy - frameDuration
                : TimeSpan.Zero;
        }

        if (!VoiceStarted && _elapsed >= _options.InitialSilenceTimeout)
        {
            StopReason = UtteranceStopReason.InitialSilence;
        }
        else if (VoiceStarted && _trailingSilence >= _options.EndSilenceTimeout)
        {
            StopReason = UtteranceStopReason.EndSilence;
        }
        else if (_elapsed >= _options.MaximumDuration)
        {
            StopReason = UtteranceStopReason.MaximumDuration;
        }

        return StopReason;
    }

    /// <summary>Empieza de nuevo, como si no se hubiera oído nada.</summary>
    public void Reset()
    {
        _voiceEnergy = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        _elapsed = TimeSpan.Zero;
        VoiceStarted = false;
        VoiceStartedAt = TimeSpan.Zero;
        StopReason = UtteranceStopReason.None;
    }
}
