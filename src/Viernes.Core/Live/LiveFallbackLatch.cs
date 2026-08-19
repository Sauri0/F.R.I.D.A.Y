namespace Viernes.Core.Live;

/// <summary>
/// La traba que manda la voz al camino de siempre cuando la sesión en vivo se porta mal.
/// </summary>
/// <remarks>
/// Sin esto, «se cae al camino de siempre» dura una conversación: la siguiente vuelve a intentar la
/// sesión en vivo, vuelve a esperar el tiempo de conexión y vuelve a fallar. El usuario no ve un
/// servicio caído, ve un asistente que tarda cinco segundos de más en contestar cada vez que le
/// habla, y eso es peor que no tener el camino nuevo.
/// <para>
/// La espera crece con cada caída seguida. Un corte de red de treinta segundos no tiene que dejar
/// apagado el camino nuevo media hora, y una cuenta sin cuota no tiene que costar un intento por
/// conversación durante todo el día: la escalera resuelve los dos casos con la misma regla.
/// </para>
/// <para>
/// Es de reloj, no de contador de intentos: la traba se abre sola cuando pasa el tiempo, sin que
/// nadie tenga que acordarse de destrabarla. <see cref="Reset"/> existe para el caso en que la
/// sesión sí abrió, que es la única prueba real de que el servicio volvió.
/// </para>
/// </remarks>
public sealed class LiveFallbackLatch
{
    /// <summary>Lo que se espera después de la primera caída.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Techo de la escalera. Más que esto es apagarlo hasta el próximo arranque.</summary>
    public static readonly TimeSpan MaximumCooldown = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _maximumCooldown;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();

    private DateTimeOffset _openAt;
    private string? _reason;
    private int _consecutiveTrips;

    /// <summary>Arma la traba.</summary>
    /// <param name="cooldown">Cuánto dura la primera espera. Por defecto <see cref="DefaultCooldown"/>.</param>
    /// <param name="maximumCooldown">Techo de la escalera. Por defecto <see cref="MaximumCooldown"/>.</param>
    /// <param name="clock">
    /// De dónde sale la hora. Se inyecta para poder probar la escalera sin esperar media hora.
    /// </param>
    public LiveFallbackLatch(
        TimeSpan? cooldown = null,
        TimeSpan? maximumCooldown = null,
        Func<DateTimeOffset>? clock = null)
    {
        _cooldown = cooldown ?? DefaultCooldown;
        _maximumCooldown = maximumCooldown ?? MaximumCooldown;

        if (_cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), "La espera tiene que ser mayor que cero.");
        }

        if (_maximumCooldown < _cooldown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCooldown),
                "El techo de la escalera no puede ser menor que el primer escalón.");
        }

        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Por qué está trabada, o <c>null</c> si la sesión en vivo se puede intentar.
    /// </summary>
    /// <remarks>
    /// Se recalcula contra el reloj en cada lectura: no hay ningún temporizador que tenga que
    /// acordarse de destrabar, así que tampoco hay forma de que quede trabada para siempre porque
    /// alguien no llamó al método que la abría.
    /// </remarks>
    public string? BlockedReason
    {
        get
        {
            lock (_gate)
            {
                if (_reason is null)
                {
                    return null;
                }

                if (_clock() >= _openAt)
                {
                    // Se abre sola, pero el contador de caídas seguidas no se toca: que haya pasado
                    // el tiempo no es prueba de que el servicio volvió, y si vuelve a fallar el
                    // próximo escalón tiene que ser más largo y no arrancar de cero otra vez.
                    _reason = null;
                    return null;
                }

                return _reason;
            }
        }
    }

    /// <summary>Cuántas caídas seguidas lleva. Cero cuando la última sesión abrió bien.</summary>
    public int ConsecutiveTrips
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveTrips;
            }
        }
    }

    /// <summary>Cuándo se vuelve a poder intentar, o <c>null</c> si se puede ahora.</summary>
    public DateTimeOffset? OpensAt
    {
        get
        {
            lock (_gate)
            {
                return _reason is null || _clock() >= _openAt ? null : _openAt;
            }
        }
    }

    /// <summary>
    /// Traba la sesión en vivo y anota por qué.
    /// </summary>
    /// <param name="reason">
    /// El motivo, dicho para una persona. Lo escribe la bitácora entera, así que quien lo arme tiene
    /// que estar seguro de que no lleva la credencial adentro; los motivos de
    /// <see cref="GeminiLiveClient"/> ya están escritos con esa regla.
    /// </param>
    public void Trip(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _consecutiveTrips++;

            // La escalera se calcula con doble para no desbordar el TimeSpan en la caída número
            // sesenta: un asistente que arranca con Windows puede pasarse el día sumando caídas.
            var factor = Math.Pow(2, Math.Min(_consecutiveTrips - 1, 20));
            var wait = TimeSpan.FromTicks((long)Math.Min(_cooldown.Ticks * factor, _maximumCooldown.Ticks));

            _openAt = _clock() + wait;
            _reason = reason;
        }
    }

    /// <summary>
    /// Destraba y borra la escalera.
    /// </summary>
    /// <remarks>
    /// Se llama cuando la sesión abrió de verdad, que es la única prueba de que el servicio volvió.
    /// Llamarlo antes —al empezar a intentar, por ejemplo— convierte la escalera en un escalón.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            _reason = null;
            _consecutiveTrips = 0;
            _openAt = default;
        }
    }
}
