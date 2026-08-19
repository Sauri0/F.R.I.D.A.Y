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

    /// <summary>
    /// Cuánto hace que terminó la transición cuando todavía no hubo ninguna.
    /// </summary>
    /// <remarks>
    /// Más que la más larga del repertorio —«cualquiera → esperándote», 960 ms— por varios órdenes.
    /// Es lo que hace que el primer cuadro dibuje un estado y no el final de una transición que
    /// nunca existió.
    /// </remarks>
    private const double SettledSeconds = 3600;

    private AssistantVisualState _state = AssistantVisualState.Idle;
    private double _stateSince = Now;
    private double _transitionEnd;
    private int _transitionPriority;

    private OrbTransition _transition = OrbTransitions.Default;
    private AssistantVisualState _transitionFrom = AssistantVisualState.Idle;
    private double _transitionStart;
    private int _transitionToken;

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

    /// <summary>
    /// La transición en vuelo, o la última que hubo. La elige el canal y nadie más.
    /// </summary>
    /// <remarks>
    /// El canal ya la resolvía para saber cuándo soltar la cola; publicarla es lo que evita que el
    /// cuerpo la vuelva a resolver por su cuenta con un <c>desde</c> viejo y termine dibujando otra
    /// entrada de la tabla que la que el canal midió.
    /// </remarks>
    internal OrbTransition Transition => _transition;

    /// <summary>De dónde salió <see cref="Transition"/>.</summary>
    internal AssistantVisualState TransitionFrom => _transitionFrom;

    /// <summary>
    /// Cuántos segundos lleva <see cref="Transition"/>, contados desde que el canal la decidió.
    /// </summary>
    /// <remarks>
    /// Y no desde que el cuerpo se enteró, que es lo que hacía antes. Con el control descargado el
    /// bucle de render se corta, el canal vence igual la transición y suelta la cola; si el reloj
    /// del cuerpo arrancara al volver, dibujaría una transición que el canal ya dio por terminada.
    /// </remarks>
    internal double TransitionElapsed => _transitionToken == 0 ? SettledSeconds : Now - _transitionStart;

    /// <summary>
    /// Sube uno por transición. Es cómo el cuerpo se entera de que hay una nueva aunque el par de
    /// estados sea el mismo que el de la anterior.
    /// </summary>
    internal int TransitionToken => _transitionToken;

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
    /// Entra sí o sí, sin mirar prioridades. Es para una secuencia guionada, que ya sabe en qué
    /// orden quiere las cosas. <b>La cola no lo usa</b> y no hay que volver a dárselo: soltar lo
    /// encolado forzando es saltearse el arbitraje justo en el momento en que más hace falta.
    /// </param>
    internal void Request(AssistantVisualState state, bool force = false)
    {
        Poll();
        Apply(state, force);
    }

    /// <summary>
    /// El cuerpo del pedido, sin pasar por <see cref="Poll"/>.
    /// </summary>
    /// <remarks>
    /// Separado para que <see cref="Poll"/> pueda soltar la cola y reafirmar el pedido vigente sin
    /// llamarse a sí mismo de vuelta.
    /// </remarks>
    private void Apply(AssistantVisualState state, bool force)
    {
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

        _transition = OrbTransitions.For(_state, state);
        _transitionFrom = _state;
        _transitionStart = now;
        _transitionEnd = now + _transition.Seconds;
        _transitionPriority = priority;
        _transitionToken++;
        _stateSince = now;
        _state = state;

        // Salir de sorda tira lo que dijo el runtime: la próxima vez que se quede sorda, la escalera
        // arranca de cero. Si no, volvería directo al cuarto intento y el estirón ya no se vería.
        if (state != AssistantVisualState.Deaf)
        {
            _retryAttempt = null;
        }

        // La cola se limpia SIEMPRE que algo entra, no sólo cuando lo que entra es lo encolado. Esta
        // línea parece redundante y no lo es: lo encolado esperaba detrás de OTRA cosa, y si un corte
        // por prioridad se llevó esa cosa, lo que esperaba atrás quedó pidiendo un turno en una fila
        // que ya no existe. Sin esto, «pensando» + «volvé a reposo» encolado + «error» terminaba con
        // el orbe abandonando el error solo, a los 270 ms, sin que nadie lo pidiera: la propiedad
        // decía error y el canal decía reposo, y como la propiedad no había cambiado, volver a
        // escribirle error no disparaba nada. El orbe se quedaba en reposo para siempre mientras la
        // app creía estar mostrando una falla.
        QueuedState = null;

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

            // Se suelta por la puerta de siempre y sin forzar: la cola entra cuando le toca, no
            // saltándose el arbitraje. Forzar acá era además la segunda mitad del bug de arriba —el
            // force pasaba por encima de la comparación de prioridades y metía en pantalla algo que
            // ya nadie estaba pidiendo.
            Apply(queuedState, force: false);
            return;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Lo mismo que <see cref="Poll()"/>, y además reafirma el pedido vigente.
    /// </summary>
    /// <param name="requested">
    /// Lo que el pedido —la <c>DependencyProperty</c> del cuerpo o de la píldora— dice que hay que
    /// mostrar ahora mismo.
    /// </param>
    /// <remarks>
    /// La propiedad es un <b>pedido</b> y el canal es lo que se muestra, y son dos cosas que pueden
    /// quedar diciendo distinto. El problema no es que se separen: es que hasta acá no había forma
    /// de volver a preguntar. Escribir de nuevo el mismo valor en la propiedad no dispara nada
    /// —WPF no notifica cuando el valor no cambió—, así que una divergencia de un cuadro quedaba
    /// pegada para toda la sesión.
    /// <para>
    /// Con esto el cuerpo vuelve a preguntar en cada cuadro y lo peor que puede hacer cualquier bug
    /// futuro de arbitraje es un parpadeo. Es la parte que importa del arreglo: la otra tapa el
    /// agujero que se encontró, esta hace que el próximo no sea permanente.
    /// </para>
    /// </remarks>
    internal void Poll(AssistantVisualState requested)
    {
        Poll();

        // Ya está puesto, o ya está pedido y esperando su turno: no hay nada que reafirmar. Sin esta
        // guarda la reafirmación volvería a encolar lo mismo una vez por cuadro.
        if (requested == _state || requested == QueuedState)
        {
            return;
        }

        Apply(requested, force: false);
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
