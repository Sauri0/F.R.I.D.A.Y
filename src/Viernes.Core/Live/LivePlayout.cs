namespace Viernes.Core.Live;

/// <summary>Por qué el parlante se quedó sin nada que sonar.</summary>
/// <remarks>
/// La distinción no es cosmética: se arreglan en lugares distintos y por eso hay que poder leer en
/// la bitácora cuál de las dos fue. Ver <see cref="LivePlayout"/>.
/// </remarks>
public enum LiveAudioGapKind
{
    /// <summary>
    /// La cola de respuesta estaba vacía cuando el driver vino a buscar audio.
    /// </summary>
    /// <remarks>
    /// El audio del servidor llegó más lento de lo que se reproduce. Se arregla del lado de la red o
    /// del colchón, no del hilo.
    /// </remarks>
    Queue,

    /// <summary>
    /// Había audio en la cola, pero el driver volvió a buscarlo más tarde de lo que tardaba en
    /// sonar lo que se había llevado.
    /// </summary>
    /// <remarks>
    /// Nadie se quedó sin audio: se quedó sin <em>hilo</em>. Es la máquina, no la red, y se arregla
    /// dándole al driver más audio por vez —más búferes o más largos—, no del lado de la cola.
    /// </remarks>
    Driver
}

/// <summary>Un hueco: cuánto silencio salió por el parlante en el medio de una respuesta.</summary>
/// <param name="Kind">De qué lado se quedó sin audio.</param>
/// <param name="Duration">Cuánto silencio se oyó.</param>
/// <param name="SinceStart">Cuánto llevaba sonando la respuesta cuando pasó.</param>
public readonly record struct LiveAudioGap(LiveAudioGapKind Kind, TimeSpan Duration, TimeSpan SinceStart);

/// <summary>Se oyó un silencio en el medio de una respuesta.</summary>
public sealed class LiveAudioGapEventArgs(LiveAudioGap gap) : EventArgs
{
    /// <summary>El hueco.</summary>
    public LiveAudioGap Gap { get; } = gap;
}

/// <summary>
/// Cuándo arrancar la reproducción, y si en el medio se oyó un silencio que no estaba en la voz.
/// </summary>
/// <remarks>
/// <b>Esto existe porque el corte de voz que reportó el usuario era invisible.</b> La salida de
/// Windows reproduce con <c>ReadFully</c>, que es lo correcto —devolver cero muestras hace que WinMM
/// dé la reproducción por terminada— pero tiene un costo que no se ve: cuando la cola se queda
/// corta, el hueco se rellena con silencio y <em>nadie se entera</em>. No hay excepción, no hay
/// contador, no hay renglón en la bitácora: sólo un usuario diciendo «como si cortara mientras
/// habla».
/// <para>
/// <b>Y hay una segunda razón, que es el arranque, y ésta está medida.</b> NAudio le pide al
/// proveedor todos sus búferes de una en la primera vuelta del hilo de reproducción. Con la
/// geometría de <c>LiveSpeakerSink</c> —cinco búferes de veinte— eso es cien milisegundos leídos de
/// golpe, y lo que falte sale de <c>ReadFully</c>, o sea silencio metido adentro de la primera
/// palabra. Banco con la geometría real, cuatro corridas idénticas:
/// <code>
///   en la cola al dar Play:   20 ms → 80 ms de relleno
///                             40 ms → 60 ms
///                             60 ms → 40 ms
///                             80 ms → 20 ms
///                            100 ms →  0 ms
///                            120 ms →  0 ms
/// </code>
/// El relleno es exactamente <c>latencia del driver menos lo que había</c>, y se anula justo al
/// llegar a la latencia. De ahí sale <see cref="DefaultPrime"/>, que no es un número elegido a ojo.
/// </para>
/// <para>
/// Es de Core y no del proyecto de Windows a propósito: acá se prueba entero sin tarjeta de sonido,
/// que es la única forma de que esto no vuelva a ser una creencia.
/// </para>
/// <para>
/// Lo tocan dos hilos —el que encola, que viene del socket, y el del driver, que lee— así que lleva
/// candado propio. No es el candado de la salida: tomar aquél desde el hilo del driver es cómo se
/// traba una reproducción, y este de acá sólo protege aritmética.
/// </para>
/// </remarks>
public sealed class LivePlayout
{
    /// <summary>
    /// Cuánto audio hace falta tener juntado antes de dejar arrancar la reproducción.
    /// </summary>
    /// <remarks>
    /// Cien milisegundos, que es la latencia del driver, que es exactamente lo que se lleva en la
    /// primera vuelta. Está medido en el banco que cita <see cref="LivePlayout"/>: con cien en la
    /// cola el relleno es cero, y cada veinte que falten son veinte de silencio adentro de la
    /// primera palabra.
    /// <para>
    /// <b>Ni uno más.</b> Todo lo que se espere de más es demora pura en la respuesta, y la respuesta
    /// después de una interrupción es justo la interacción que el usuario dijo que andaba bien. La
    /// tentación de pedir un colchón «con margen» —doscientos cincuenta, por si la red hipa— cuesta
    /// ciento cincuenta milisegundos en cada respuesta para cubrir un hipo que este número no evita:
    /// el colchón se consume en la primera vuelta pase lo que pase, y lo que protege del segundo
    /// hipo es que el servidor mande más rápido que tiempo real, no que acá se haya esperado más.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultPrime = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Un silencio más largo que esto no es un tajo en la voz: es que dejó de hablar.
    /// </summary>
    /// <remarks>
    /// <b>Es lo que separa el defecto del funcionamiento normal, y sin esto el instrumento miente.</b>
    /// Una herramienta que tarda diez segundos seca la cola con el turno abierto, y sin este techo se
    /// informaba como un hueco de diez segundos: el mismo renglón que un corte de voz de verdad,
    /// enterrándolo. Lo que el usuario oye como «se le corta la voz» son decenas o pocos cientos de
    /// milisegundos en el medio de una frase; un segundo entero ya se oye como una pausa.
    /// <para>
    /// Lo que se descarta por largo no se pierde: <see cref="Filler"/> lo cuenta igual. Lo que este
    /// techo decide es qué merece un renglón propio, no qué se mide.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MaximumTear = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _prime;
    private readonly System.Threading.Lock _gate = new();

    private bool _playing;
    private bool _read;
    private long _firstReadAt;
    private long _handedBytes;

    private bool _dryOpen;
    private long _dryBytes;
    private bool _wasFed;
    private TimeSpan _lateReported;

    /// <summary>Arma la cuenta con el colchón por omisión.</summary>
    public LivePlayout()
        : this(DefaultPrime)
    {
    }

    /// <summary>Arma la cuenta con un colchón elegido. Se pasa distinto sólo desde las pruebas.</summary>
    public LivePlayout(TimeSpan prime) => _prime = prime;

    /// <summary>Cuántos huecos se informaron desde que se armó.</summary>
    public int Gaps { get; private set; }

    /// <summary>Cuánto silencio suman los huecos informados.</summary>
    public TimeSpan GapTotal { get; private set; }

    /// <summary>
    /// Todo el silencio que <c>ReadFully</c> metió, se haya informado o no.
    /// </summary>
    /// <remarks>
    /// Va aparte de <see cref="GapTotal"/> justamente porque no juzga: acá entra la pausa de una
    /// herramienta y el final de cada respuesta, que no son defectos. Sirve para el renglón de
    /// cierre y para poder decir «se rellenaron X ms» sin tener que afirmar que algo estuvo mal.
    /// </remarks>
    public TimeSpan Filler { get; private set; }

    /// <summary>Si la reproducción está en marcha según esta cuenta.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playing;
            }
        }
    }

    /// <summary>
    /// Si ya se puede arrancar la reproducción.
    /// </summary>
    /// <param name="queued">Cuánto audio hay juntado esperando salir.</param>
    /// <param name="noMoreComing">
    /// Si el turno ya cerró y no va a llegar más audio. Una respuesta de una palabra puede no juntar
    /// el colchón nunca, y esperarlo la dejaría muda: cuando no viene más, lo que hay es todo lo que
    /// va a haber y sale ya.
    /// </param>
    public bool ShouldStart(TimeSpan queued, bool noMoreComing) =>
        queued > TimeSpan.Zero && (noMoreComing || queued >= _prime);

    /// <summary>Arrancó la reproducción. Se llama <b>antes</b> de darle el play al dispositivo.</summary>
    /// <remarks>
    /// Antes y no después porque la primera lectura del driver puede llegar mientras <c>Play</c>
    /// todavía no volvió, y una lectura fuera de la cuenta deja el reparto corrido.
    /// </remarks>
    public void NoteStarted()
    {
        lock (_gate)
        {
            _playing = true;
            Limpiar();
        }
    }

    /// <summary>Se paró la reproducción: la callaron, se cerró el dispositivo o se vació la cola.</summary>
    public void NoteStopped()
    {
        lock (_gate)
        {
            _playing = false;
            Limpiar();
        }
    }

    /// <summary>
    /// La respuesta terminó: lo que el parlante lea a partir de acá es silencio esperado.
    /// </summary>
    /// <remarks>
    /// <b>Sin esto, el silencio entre una respuesta y la siguiente se informaba como un corte.</b>
    /// Con <c>ReadFully</c> el hilo del driver no se detiene nunca solo, así que sigue leyendo cola
    /// vacía entre turnos; cuando llega el audio del turno siguiente ese hueco «se cierra» y, sin
    /// esta marca, se informa entero. Es lo mismo que hacía el intento anterior con una bandera
    /// leída desde el hilo del driver — pero acá es un borrado y no una consulta, que es la
    /// diferencia entre depender de que la bandera esté puesta a tiempo y no depender.
    /// </remarks>
    public void NoteTurnEnded()
    {
        lock (_gate)
        {
            _dryOpen = false;
            _dryBytes = 0;
        }
    }

    /// <summary>
    /// El driver vino a buscar audio. Devuelve el hueco si se cerró uno en esta lectura.
    /// </summary>
    /// <remarks>
    /// Corre en el hilo del driver, así que no puede tardar ni asignar: son cuatro sumas y una
    /// comparación.
    /// <para>
    /// El hueco se informa cuando <b>se cierra</b>, no cuando empieza: uno que nunca se cierra es el
    /// final de la respuesta —la cola se vacía y el parlante sigue leyendo silencio hasta la
    /// próxima— y ése no es un corte, es que terminó de hablar. Los que se cierran porque volvió a
    /// haber voz son los que la persona oye como un tajo en el medio de una frase.
    /// </para>
    /// </remarks>
    /// <param name="availableBytes">Lo que había en la cola justo antes de leer.</param>
    /// <param name="requestedBytes">Lo que el driver se llevó, relleno incluido.</param>
    /// <param name="timestamp">Sello de <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</param>
    public LiveAudioGap? NoteRead(int availableBytes, int requestedBytes, long timestamp)
    {
        if (requestedBytes <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_playing)
            {
                return null;
            }

            // El reparto se cuenta desde la PRIMERA LECTURA y no desde el play, porque entre uno y
            // otra hay un despacho del ThreadPool que en esta máquina llegó a tardar 56 ms —medido—.
            // Contando desde el play, esos 56 ms son un atraso del driver que nunca sonó: no se le
            // había entregado nada al dispositivo todavía. Era un falso positivo en cada arranque, y
            // encima gastaba el único aviso de atraso que la charla iba a dar.
            if (!_read)
            {
                _read = true;
                _firstReadAt = timestamp;
            }

            _handedBytes += requestedBytes;
            var since = System.Diagnostics.Stopwatch.GetElapsedTime(_firstReadAt, timestamp);

            LiveAudioGap? gap = null;

            if (availableBytes >= requestedBytes)
            {
                if (_dryOpen && _dryBytes > 0)
                {
                    var silencio = LiveAudioFormat.OutputDurationOf(_dryBytes);
                    if (silencio <= MaximumTear)
                    {
                        gap = new LiveAudioGap(LiveAudioGapKind.Queue, silencio, since);
                    }
                }

                _dryOpen = false;
                _dryBytes = 0;
            }
            else if (availableBytes > 0 || _dryOpen || _wasFed)
            {
                // Se rellenó con silencio lo que faltaba. El caso de cero bytes sin nada abierto y
                // sin haber estado sonando es el parlante callado entre turnos, que no es un hueco.
                _dryOpen = true;
                var relleno = requestedBytes - availableBytes;
                _dryBytes += relleno;
                Filler += LiveAudioFormat.OutputDurationOf(relleno);
            }

            _wasFed = availableBytes >= requestedBytes;

            if (gap is null)
            {
                gap = Atraso(since);
            }

            if (gap is null)
            {
                return null;
            }

            Gaps++;
            GapTotal += gap.Value.Duration;
            return gap;
        }
    }

    /// <summary>
    /// Si el driver se atrasó más de lo que ya se había informado.
    /// </summary>
    /// <remarks>
    /// <b>Por marca de agua y no por bandera, que es el arreglo.</b> El reparto es acumulado: lo
    /// entregado menos el tiempo transcurrido. Su techo es la latencia del driver, así que una vez
    /// que se fue a negativo <em>no puede volver a positivo</em> — el dispositivo nunca pide más
    /// rápido que tiempo real. Con una bandera que sólo se baja cuando el reparto vuelve a dar, el
    /// primer atraso apagaba el aviso para toda la charla y los siguientes se tragaban enteros.
    /// <para>
    /// Guardando cuánto se informó, un atraso sostenido sigue informándose una sola vez —el déficit
    /// no crece— y uno nuevo encima del anterior se informa por lo que agregó. El margen es para no
    /// escribir un renglón por cada milésima de deriva.
    /// </para>
    /// </remarks>
    private LiveAudioGap? Atraso(TimeSpan since)
    {
        // Lo entregado tiene que alcanzar para cubrir el tiempo que pasó: el dispositivo consume en
        // tiempo real y no espera a nadie. Si se le entregó menos, la diferencia ya salió por el
        // parlante como silencio.
        var deficit = since - LiveAudioFormat.OutputDurationOf(_handedBytes);
        if (deficit <= _lateReported + TimeSpan.FromMilliseconds(20))
        {
            return null;
        }

        var nuevo = deficit - _lateReported;
        _lateReported = deficit;
        return new LiveAudioGap(LiveAudioGapKind.Driver, nuevo, since);
    }

    private void Limpiar()
    {
        _read = false;
        _firstReadAt = 0;
        _handedBytes = 0;
        _dryOpen = false;
        _dryBytes = 0;
        _wasFed = false;
        _lateReported = TimeSpan.Zero;
    }
}
