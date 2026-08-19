using System.Diagnostics;
using Viernes.App.ViewModels;

namespace Viernes.App.Controls;

/// <summary>
/// El único lugar donde se decide qué está mostrando el orbe.
/// </summary>
/// <remarks>
/// Es el puerto de <c>pedirEstado(</c> y <c>pedirAnimo(</c>. Existe porque los estados no llegan de
/// a uno y ordenados: mientras se está dibujando la transición a <em>hablando</em> puede llegar un
/// <em>error</em>, y mientras se dibuja el error puede llegar un <em>volvé a reposo</em>. Sin
/// arbitraje eso se ve como un parpadeo, y con arbitraje se ve como una cosa que pasó y después
/// otra.
/// <para>
/// Las dos reglas, que son una sola mirada desde dos lados: <b>lo más urgente corta</b> —un error no
/// espera a que termine el fundido de nada— y <b>lo menos urgente espera y entra igual</b>, porque
/// perderlo sería peor que mostrarlo tarde.
/// </para>
/// <para>
/// La cola tiene <b>un solo lugar por canal</b>, y no es una limitación sino la regla: si mientras
/// se espera llegan tres pedidos de menor prioridad, el que importa es el último. Reproducir los
/// tres sería mostrar una película de estados que ya nadie está pidiendo.
/// </para>
/// <para>
/// El reloj es absoluto —<see cref="Stopwatch.GetTimestamp"/>—, no una suma de deltas de cuadro. Es
/// lo que permite que la nube, la gota y la píldora consulten el mismo canal cada una a su ritmo sin
/// que ninguna adelante el tiempo de las otras.
/// </para>
/// </remarks>
internal sealed class OrbStateChannel
{
    /// <summary>El respiro que se le da a la cola después de la transición, tal como en el fuente.</summary>
    /// <remarks>
    /// Sin él, el estado encolado entraría en el mismo cuadro en que el anterior terminó, y las dos
    /// transiciones se leerían como una sola cosa rara en vez de como dos.
    /// </remarks>
    private const double QueueGraceSeconds = 0.040;

    /// <summary>Cuánto sobrevive el ánimo a su propia duración antes de soltar la cola.</summary>
    private const double MoodTailSeconds = 0.240;

    /// <summary>Cuánto dura el aviso de una combinación prohibida.</summary>
    private const double RejectionSeconds = 2.6;

    private AssistantVisualState _state = AssistantVisualState.Idle;
    private double _stateSince = Now;
    private double _transitionEnd;
    private int _transitionPriority;

    private OrbMood? _mood;
    private double _moodEnd;

    private string? _rejection;
    private double _rejectionEnd;

    private int? _retryAttempt;
    private double _retryAt;

    /// <summary>Cambió el estado, el ánimo, la cola o el aviso. Todo lo que se dibuja o se escribe.</summary>
    internal event EventHandler? Changed;

    /// <summary>El estado que se está mostrando. No es necesariamente el último que pidieron.</summary>
    internal AssistantVisualState State => _state;

    /// <summary>El estado que está esperando a que termine la transición en vuelo. Uno, o ninguno.</summary>
    internal AssistantVisualState? QueuedState { get; private set; }

    /// <summary>El ánimo vivo.</summary>
    internal OrbMood? Mood => _mood;

    /// <summary>El ánimo que espera a que termine el de arriba. Uno, o ninguno.</summary>
    internal OrbMood? QueuedMood { get; private set; }

    /// <summary>
    /// Sube uno cada vez que arranca un ánimo. Es cómo los cuerpos se enteran de que hay uno nuevo
    /// aunque sea del mismo registro que el anterior.
    /// </summary>
    internal int MoodToken { get; private set; }

    /// <summary>Lo que hay que decirle a quien pidió una combinación prohibida. Se borra solo.</summary>
    internal string? Rejection => _rejection;

    /// <summary>Cuánto hace que el orbe está en este estado.</summary>
    internal TimeSpan TimeInState => TimeSpan.FromSeconds(Math.Max(0, Now - _stateSince));

    /// <summary>Hay algo esperando en alguna de las dos colas.</summary>
    internal bool HasQueue => QueuedState is not null || QueuedMood is not null;

    /// <summary>
    /// Cuánto hace que se hizo la pregunta que nadie contestó. La sabe el libro de misiones, no el
    /// orbe: acá entra ya calculada.
    /// </summary>
    internal TimeSpan WaitingAge { get; set; }

    /// <summary>
    /// El runtime avisa que volvió a abrir el micrófono, y en qué número de intento va.
    /// </summary>
    /// <remarks>
    /// Es la única entrada que la parte visual de <em>sorda reintentando</em> necesita de afuera:
    /// reintentar es del runtime, dibujar el intento es de acá. Mientras nadie llame a esto la
    /// escalera se cuenta sola desde que entró en sorda, así que el orbe se ve bien aunque el
    /// cableado todavía no exista; pero entonces la cuenta regresiva es una estimación y no la
    /// verdad.
    /// </remarks>
    internal void ReportRetry(int attempt)
    {
        _retryAttempt = attempt;
        _retryAt = Now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Dónde está parada la escalera de reintentos ahora mismo.</summary>
    internal OrbRetry Retry => _retryAttempt is { } attempt
        ? OrbDeafRetry.Resolve(attempt, Now - _retryAt)
        : OrbDeafRetry.Resolve(TimeInState.TotalSeconds);

    /// <summary>
    /// Pide un estado. Puede entrar ya, entrar encolado, o —si es el mismo— no pasar nada.
    /// </summary>
    /// <param name="state">El estado que se quiere mostrar.</param>
    /// <param name="force">
    /// Entra sí o sí, sin mirar prioridades. Lo usa la cola al soltar lo que tenía guardado y
    /// cualquier secuencia guionada, que ya sabe en qué orden quiere las cosas.
    /// </param>
    internal void Request(AssistantVisualState state, bool force = false)
    {
        Poll();

        if (state == _state)
        {
            return;
        }

        var priority = OrbPriority.Of(state);
        var now = Now;

        if (!force && now < _transitionEnd && priority < _transitionPriority)
        {
            QueuedState = state;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var spec = OrbTransitions.For(_state, state);
        _transitionEnd = now + spec.Seconds;
        _transitionPriority = priority;
        _stateSince = now;
        _state = state;

        // Salir de sorda tira lo que dijo el runtime: la próxima vez que se quede sorda, la escalera
        // arranca de cero. Si no, volvería directo al cuarto intento y el estirón ya no se vería.
        if (state != AssistantVisualState.Deaf)
        {
            _retryAttempt = null;
        }

        if (QueuedState == state)
        {
            QueuedState = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pide un ánimo. Puede rebotar por prohibido, cortar al que está, encolarse, o ignorarse.
    /// </summary>
    internal void RequestMood(OrbMood mood)
    {
        Poll();

        if (OrbPriority.Blocks(mood, _state))
        {
            _rejection = $"«{OrbMoods.Label(mood)}» no va sobre {OrbPalette.For(_state).Name}";
            _rejectionEnd = Now + RejectionSeconds;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_mood is { } current)
        {
            // El mismo ánimo dos veces seguidas no se reinicia: repetir «urgente» mientras «urgente»
            // está corriendo no es más urgente, es un parpadeo.
            if (current == mood)
            {
                return;
            }

            if (OrbPriority.Of(mood) <= OrbPriority.Of(current))
            {
                QueuedMood = mood;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        StartMood(mood);
    }

    /// <summary>Corta el ánimo en curso y lo que estuviera esperando.</summary>
    internal void ClearMood()
    {
        if (_mood is null && QueuedMood is null)
        {
            return;
        }

        _mood = null;
        QueuedMood = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Suelta lo que la cola tenga vencido. Hay que llamarla una vez por cuadro desde cualquiera de
    /// los que miran el canal; llamarla de más no adelanta nada porque el reloj es absoluto.
    /// </summary>
    internal void Poll()
    {
        var now = Now;
        var changed = false;

        if (_rejection is not null && now >= _rejectionEnd)
        {
            _rejection = null;
            changed = true;
        }

        if (_mood is not null && now >= _moodEnd)
        {
            _mood = null;
            changed = true;

            if (QueuedMood is { } queuedMood)
            {
                QueuedMood = null;
                StartMood(queuedMood);
                changed = false;
            }
        }

        // La cola de estados se mide contra la transición que hay en vuelo ahora, no contra la que
        // había cuando se encoló: si en el medio entró un error, lo encolado espera al error.
        if (QueuedState is { } queuedState && now >= _transitionEnd + QueueGraceSeconds)
        {
            QueuedState = null;
            Request(queuedState, force: true);
            return;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartMood(OrbMood mood)
    {
        _mood = mood;
        _moodEnd = Now + OrbMoods.Duration(mood).TotalSeconds + MoodTailSeconds;
        MoodToken++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
