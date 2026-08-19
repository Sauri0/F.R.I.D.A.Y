namespace Viernes.Core.Live;

/// <summary>Lo que cambió al escribir un bloque en la compuerta.</summary>
public enum LiveSpeechEdge
{
    /// <summary>Nada: sigue como estaba.</summary>
    None,

    /// <summary>Arrancó a hablar.</summary>
    Started,

    /// <summary>Se calló y aguantó callado lo suficiente como para dar la frase por terminada.</summary>
    Finished
}

/// <summary>
/// Convierte el veredicto bloque a bloque del detector de voz en dos bordes: arrancó y terminó.
/// </summary>
/// <remarks>
/// Existe porque el detector opina cada veinte milisegundos y el orbe no puede parpadear cada veinte
/// milisegundos. Entre dos palabras hay silencio, y un borde de «terminó» en cada silencio deja el
/// orbe yendo de «te escucho» a «pensando» y de vuelta tres veces por oración.
/// <para>
/// El silencio que hay que aguantar es el mismo que usa el servidor para dar tu turno por cerrado
/// —<see cref="GeminiLiveOptions.SilenceDurationMs"/>, 700 ms por defecto—, y eso no es una
/// coincidencia cómoda: es el único valor que hace que el orbe pase a «pensando» en el mismo
/// instante en que el servidor efectivamente se pone a pensar. Un valor propio más corto adelanta el
/// dibujo a los hechos y uno más largo lo atrasa; los dos se ven como que el orbe miente.
/// </para>
/// <para>
/// El borde de arranque no tiene espera: pasa apenas el detector dice que hay voz. Del otro lado hay
/// una asimetría a propósito —empezar a escuchar tarde se nota mucho más que dejar de escuchar
/// tarde—, y la misma asimetría está en el reconocedor de siempre.
/// </para>
/// </remarks>
public sealed class LiveUserSpeechGate
{
    private readonly TimeSpan _silenceHold;
    private TimeSpan _silence;
    private bool _speaking;

    /// <summary>Arma la compuerta.</summary>
    /// <param name="silenceHold">
    /// Cuánto silencio hay que aguantar para dar la frase por terminada. Pasale el mismo que la
    /// sesión: <c>TimeSpan.FromMilliseconds(options.SilenceDurationMs)</c>.
    /// </param>
    public LiveUserSpeechGate(TimeSpan silenceHold)
    {
        if (silenceHold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceHold), "El silencio a aguantar tiene que ser mayor que cero.");
        }

        _silenceHold = silenceHold;
    }

    /// <summary>Si ahora mismo se considera que la persona está hablando.</summary>
    public bool IsSpeaking => _speaking;

    /// <summary>Cuánto silencio lleva acumulado desde la última voz.</summary>
    public TimeSpan Silence => _silence;

    /// <summary>
    /// Escribe el veredicto de un bloque y devuelve el borde, si lo hubo.
    /// </summary>
    /// <param name="isVoice">Lo que dijo el detector de este bloque.</param>
    /// <param name="blockDuration">Cuánto dura el bloque.</param>
    public LiveSpeechEdge Write(bool isVoice, TimeSpan blockDuration)
    {
        if (blockDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(blockDuration), "Un bloque no puede durar menos que nada.");
        }

        if (isVoice)
        {
            _silence = TimeSpan.Zero;
            if (_speaking)
            {
                return LiveSpeechEdge.None;
            }

            _speaking = true;
            return LiveSpeechEdge.Started;
        }

        if (!_speaking)
        {
            // El silencio no se acumula cuando no venías hablando: si lo hiciera, la primera vez
            // que alguien habla después de un rato callado saldría un «terminó» apenas se calle una
            // milésima, porque el contador ya venía pasado de largo.
            return LiveSpeechEdge.None;
        }

        _silence += blockDuration;
        if (_silence < _silenceHold)
        {
            return LiveSpeechEdge.None;
        }

        _speaking = false;
        return LiveSpeechEdge.Finished;
    }

    /// <summary>Vuelve al arranque. Para cuando se abre o se cierra una conversación.</summary>
    public void Reset()
    {
        _speaking = false;
        _silence = TimeSpan.Zero;
    }
}
