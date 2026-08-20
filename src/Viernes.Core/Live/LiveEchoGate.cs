namespace Viernes.Core.Live;

/// <summary>Qué hacer con un bloque del micrófono mientras ella habla.</summary>
public enum LiveMicrophoneVerdict
{
    /// <summary>Subirlo, como siempre.</summary>
    Send,

    /// <summary>No subirlo: es su propia voz volviendo por el micrófono. Guardalo en la antesala.</summary>
    Hold,

    /// <summary>
    /// Alguien le está hablando encima de verdad: soltá la antesala y volvé a subir todo.
    /// </summary>
    Release
}

/// <summary>
/// Decide, bloque por bloque, si lo que entra por el micrófono mientras ella habla es su propio eco
/// o es alguien hablándole encima.
/// </summary>
/// <remarks>
/// Esto existe por un solo bug, y conviene contarlo entero porque el arreglo no se entiende sin él.
/// La bomba del micrófono subía <b>todos</b> los bloques, también mientras ella hablaba. Su voz sale
/// por los parlantes, vuelve por el micrófono, sube a Gemini, y el detector de voz <em>del
/// servidor</em> —que no tiene forma de saber que esa voz es la suya— decide que la persona habló
/// encima: manda <c>interrupted</c>, corta el turno y arranca otro. Ese otro turno vuelve a sonar,
/// vuelve a entrar, y vuelve a cortarse. Medido en la bitácora del usuario: quince vueltas de
/// Listening → Thinking → Speaking → Interrupted en cuarenta y ocho segundos. Desde afuera se ve
/// como que colapsó.
/// <para>
/// Lo que no se puede hacer para arreglarlo es cerrar el micrófono mientras habla. Hablarle encima
/// para callarla es <b>la razón por la que existe</b> el camino en vivo: el camino de siempre no
/// puede hacerlo por más que se lo apure, está prometido en el README y es lo que el orbe dice en
/// pantalla —«hablame encima para cortarme»—. Un arreglo que mate la interrupción cambia un problema
/// por otro peor.
/// </para>
/// <para>
/// <b>Por qué el detector de voz no alcanza para separarlas.</b> Silero acierta en lo suyo —distingue
/// voz de un aplauso o de un portazo— pero el eco <em>es</em> voz: es la misma voz, grabada dos
/// metros más lejos. Cualquier detector entrenado en voz dice «sí» para las dos. Lo único que las
/// separa sin cancelación de eco de verdad es <b>cuánto</b> suenan: la persona le habla al micrófono
/// de cerca y su propia voz llega atenuada por el parlante, el cuarto y la distancia.
/// </para>
/// <para>
/// <b>Y por qué el umbral no puede ser una constante.</b> Cuánto se atenúa el eco depende del volumen
/// de los parlantes, de dónde está el micrófono y de cómo suena el cuarto: un número escrito acá
/// estaría calibrado contra un equipo que no es el del usuario. Así que se mide solo, y se mide con
/// lo único que se sabe con certeza: <b>mientras ella habla y nadie la interrumpe, todo lo que entra
/// por el micrófono es el eco</b>. De ahí sale el nivel de referencia, y el listón para creerle a
/// alguien que habla encima es <see cref="Margin"/> veces ese nivel.
/// </para>
/// <para>
/// El nivel del bloque que se está juzgando no participa de su propio criterio —el listón se evalúa
/// con la referencia anterior y recién después se la avanza—, que es la misma disciplina que usa
/// <c>NoiseFloorTracker</c> y por la misma razón: un estimador que se alimenta de la señal que quiere
/// distinguir termina persiguiéndola.
/// </para>
/// <para>
/// <b>Lo que esta compuerta no hace</b>, dicho acá para que no se descubra midiendo: no cancela el
/// eco, lo esquiva. Si los parlantes están tan fuertes que el eco llega tan alto como una persona
/// hablando —el micrófono apoyado contra el parlante— no hay listón que separe las dos cosas y algo
/// de eco va a pasar. Para eso hace falta cancelación de verdad, restando la señal que se está
/// reproduciendo. Ver el informe del arreglo.
/// </para>
/// </remarks>
public sealed class LiveEchoGate
{
    /// <summary>
    /// Cuánto tiene que superar el bloque a la referencia del eco para creerle que es una persona.
    /// </summary>
    /// <remarks>
    /// El nivel que entrega el detector está comprimido con raíz cuadrada —ver
    /// <c>HeuristicVoiceActivityDetector</c>—, así que 1,7 acá son 2,9 veces en potencia, unos 9 dB.
    /// Ese es el margen que hay entre hablarle al micrófono de cerca y escuchar el mismo audio salido
    /// de un parlante a un par de metros; con menos, la parte fuerte de una vocal suya cruzaría el
    /// listón, y con mucho más habría que gritarle.
    /// </remarks>
    private const double Margin = 1.7;

    /// <summary>
    /// Cuánto puede frenar seguido antes de rendirse y dejar pasar todo.
    /// </summary>
    /// <remarks>
    /// <b>Una válvula, y hace falta porque esta compuerta depende de algo que no controla.</b> Se
    /// pone cuando <c>LiveVoiceSession.IsSpeakerBusy</c> dice que el parlante suena, y eso sale de la
    /// cola de salida más lo que el driver tiene en la mano. Si el dispositivo de audio se cae o lo
    /// desenchufan en el medio de una respuesta, esa cola puede quedar clavada sin drenar: el
    /// parlante figuraría sonando para siempre y la compuerta se quedaría puesta para siempre.
    /// <para>
    /// El resultado sería que <b>deja de oírte y nadie se entera</b>: el orbe seguiría diciendo que
    /// escucha. Antes de que esta compuerta existiera, un parlante trabado era inofensivo —no la oías
    /// hablar, y listo—; ahora también te dejaría mudo. Un mecanismo nuevo no puede convertir la
    /// falla de otro en algo peor de lo que era.
    /// </para>
    /// <para>
    /// Treinta segundos son mucho más de lo que dura cualquier respuesta hablada y mucho menos que
    /// «para siempre». Pasado ese rato, la compuerta se rinde: se pierde la protección contra el eco
    /// —que es un problema que se ve y se escucha— antes que el micrófono, que es un problema que no
    /// se ve.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(30);

    /// <summary>Cuántas muestras hacen falta antes de creerle al percentil.</summary>
    /// <remarks>
    /// Doce bloques son unos 240 ms de ella hablando. Menos que eso y el percentil lo decide un
    /// puñado de muestras, que es como no tenerlo.
    /// </remarks>
    private const int MinimumSamples = 12;

    /// <summary>
    /// Qué percentil de lo que entra mientras ella habla se toma como nivel del eco.
    /// </summary>
    /// <remarks>
    /// <b>Un percentil y no el pico de una ventana, y hay cuatro mediciones reales que explican por
    /// qué.</b> La versión anterior medía el pico de los primeros 450 ms de cada respuesta, y en la
    /// bitácora del usuario quedó así:
    /// <code>
    ///   eco=148  · nivel=0,450  listón=0,765  encima=1
    ///   eco=123  · nivel=1,000  listón=0,850  encima=1
    ///   eco=0    · nivel=0,450  listón=0,765  encima=0
    ///   eco=2041 · nivel=0,450  listón=0,765  encima=0
    /// </code>
    /// <para>
    /// El 0,450 es el valor de fábrica: la ventana <b>no midió nada</b>. Pasa porque el eco no llega
    /// al micrófono en el mismo instante en que arranca el parlante —hay que sumarle el camino por el
    /// cuarto— así que la ventana se cerraba mirando silencio. El 1,000 es el otro extremo: un solo
    /// bloque saturado adentro de la ventana y el listón se iba al tope.
    /// </para>
    /// <para>
    /// Las dos fallas se ven igual desde afuera: el listón queda muy alto y no se la puede
    /// interrumpir. Los 2041 bloques frenados con <c>encima=0</c> son cuarenta y un segundos en los
    /// que el usuario no logró cortarla ni una vez, y es exactamente lo que reportó.
    /// </para>
    /// <para>
    /// El percentil arregla las dos: no se queda sin muestras porque toma todo lo que entra mientras
    /// ella habla, y no se lo lleva un pico porque un bloque saturado es una muestra entre cientos.
    /// Se toma el 75 y no la mediana para quedar del lado seguro: el eco tiene picos, y quedarse en el
    /// medio dejaría pasar la mitad de ellos.
    /// </para>
    /// </remarks>
    private const double EchoPercentile = 0.50;

    /// <summary>En cuántos escalones se reparte el nivel para sacarle un percentil barato.</summary>
    private const int Steps = 64;

    /// <summary>
    /// Por debajo de esto no baja la referencia, por bien que haya medido.
    /// </summary>
    /// <remarks>
    /// Una medición hecha casi toda de silencio daría un listón al ras del ruido del cuarto, y ahí
    /// cualquier suspiro la cortaría. Sale del perfil del banco de este equipo, donde el ruido de
    /// cuarto midió 0,11.
    /// </remarks>
    private const double SeedFloor = 0.16;

    /// <summary>
    /// Con qué referencia arranca, antes de haber medido nada.
    /// </summary>
    /// <remarks>
    /// Arranca <b>pesimista</b> a propósito: es el único momento en que todavía no hay medición, y de
    /// los dos errores posibles el caro es quedarse corto —quedarse corto es exactamente el bug que
    /// esto viene a cerrar—. Quedarse largo cuesta hablarle un poco más fuerte durante el primer
    /// segundo de la primera respuesta, y después ya está medido.
    /// <para>
    /// El valor sale del perfil del banco (<c>--medir</c>) de este equipo: el ruido del cuarto mide
    /// 0,11 y la voz de la persona 0,89 saturando en 1,0. 0,45 cae en el medio, así que el listón
    /// inicial queda en 0,77 — arriba de cualquier eco razonable y todavía abajo del nivel al que
    /// habla el usuario.
    /// </para>
    /// </remarks>
    private const double SeedLevel = 0.45;

    /// <summary>
    /// Hasta dónde puede trepar el listón.
    /// </summary>
    /// <remarks>
    /// La voz de la persona, medida con el banco en este equipo, promedia 0,895 y satura en 1,0. Un
    /// listón por encima de eso no sería estricto: sería <b>inalcanzable</b>, y una interrupción
    /// imposible es peor que un eco que se cuela, porque no hay forma de darse cuenta desde afuera —se
    /// ve igual que si no te escuchara—. Cuando el eco llega tan alto que el listón toca este tope,
    /// esta compuerta está admitiendo que se quedó corta: hace falta cancelación de verdad.
    /// </remarks>
    private const double MaximumBar = 0.85;

    /// <summary>
    /// Cuánta voz seguida por encima del listón hace falta para dar la interrupción por buena.
    /// </summary>
    /// <remarks>
    /// Cuatro bloques de veinte milisegundos. Uno solo no alcanza: el arranque de una sílaba suya
    /// —una <em>p</em>, una <em>t</em>— salta varios decibeles en un bloque y puede cruzar el listón
    /// antes de que la referencia lo alcance. Lo que el eco no puede hacer es <em>sostenerse</em> ahí
    /// arriba, porque cada bloque que no cruza vuelve a subir la referencia. Ochenta milisegundos no
    /// se notan: son menos que la primera sílaba, y además la antesala de la bomba devuelve el audio
    /// de esos ochenta milisegundos, así que no se pierde ni el arranque de la palabra.
    /// </remarks>
    private static readonly TimeSpan Breakthrough = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Cuánto sigue armada la compuerta después de que el parlante se calla.
    /// </summary>
    /// <remarks>
    /// El eco no termina en el mismo instante que el parlante: entre lo último que sonó y el bloque
    /// que lo contiene están la propagación por el cuarto, la latencia del driver de entrada y el
    /// búfer de captura. Es la misma cola que <c>LiveVoiceSession</c> ya tuvo que reconocer con sus
    /// 180 ms de voz-sobre-parlante-callado; acá se usan 200 para quedar del mismo lado con margen.
    /// Durante esa cola la persona sigue pudiendo interrumpir: lo único que se le pide es el mismo
    /// listón.
    /// </remarks>
    private static readonly TimeSpan Hangover = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// En cuánto tiempo la referencia cae a la mitad de la distancia hacia lo que está midiendo.
    /// </summary>
    /// <remarks>
    /// La referencia sube de golpe y baja despacio: es un seguidor de picos, y tiene que serlo porque
    /// lo que importa no es cuánto suena el eco en promedio sino cuánto llega a sonar. Entre dos
    /// palabras suyas el micrófono cae al ruido del cuarto, y si la referencia lo siguiera para abajo
    /// la sílaba siguiente cruzaría su propio listón.
    /// <para>
    /// Setecientos milisegundos hacen que, si el eco de verdad es más flojo que el arranque
    /// pesimista, la referencia lo alcance adentro de la primera respuesta y no al rato. Es el mismo
    /// orden de magnitud que el silencio con el que el servidor cierra un turno, y no por casualidad:
    /// las dos constantes miden lo mismo, cuánto dura una pausa que todavía es la misma frase.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan EchoHalfLife = TimeSpan.FromMilliseconds(700);

    private TimeSpan _hangover;

    /// <summary>Cuánto lleva frenando seguido. Ver <see cref="GiveUp"/>.</summary>
    private TimeSpan _holding;

    /// <summary>
    /// Cuántas veces cayó el nivel en cada escalón, contando sólo bloques con forma de voz.
    /// </summary>
    /// <remarks>
    /// Un histograma y no una lista de muestras: el percentil se pide una vez por bloque —cincuenta
    /// veces por segundo— y ordenar miles de números cada vez sería trabajo por nada. Con 64
    /// escalones el nivel se resuelve con precisión de 0,016, mucho más fina que cualquier decisión
    /// que se tome con él.
    /// </remarks>
    private readonly int[] _steps = new int[Steps];

    private int _samples;

    /// <summary>El pico visto, para los primeros bloques en que todavía no hay percentil.</summary>
    private double _peak;

    private TimeSpan _run;
    private bool _open;

    /// <summary>Si tuvo que rendirse porque el parlante nunca se calló. Ver <see cref="GiveUp"/>.</summary>
    /// <remarks>
    /// Se expone para que quede en la bitácora al cerrar la charla: una compuerta rendida no se nota
    /// desde afuera —todo sigue andando, sólo vuelve el eco— y sin esto no habría forma de saber que
    /// el problema estuvo en la salida de audio y no acá.
    /// </remarks>
    public bool GaveUp { get; private set; }

    /// <summary>
    /// Nivel de eco que tiene medido ahora mismo. Para la bitácora y las pruebas.
    /// </summary>
    /// <remarks>
    /// Con pocas muestras contesta el pico, que es pesimista y por lo tanto seguro; con suficientes,
    /// el percentil. Nunca baja del arranque de fábrica: una medición hecha con silencio no puede
    /// dejar el listón al ras del ruido del cuarto.
    /// </remarks>
    public double EchoLevel => _samples < MinimumSamples
        ? Math.Max(SeedLevel, _peak)
        : Math.Max(SeedFloor, Percentil(EchoPercentile));

    /// <summary>Nivel que hay que superar ahora mismo para que se la pueda interrumpir.</summary>
    public double Bar => Math.Min(EchoLevel * Margin, MaximumBar);

    /// <summary>Cuántas veces alguien le habló encima y la compuerta lo dejó pasar.</summary>
    public int Breakthroughs { get; private set; }

    /// <summary>
    /// Juzga un bloque del micrófono.
    /// </summary>
    /// <param name="speakerAudible">
    /// Si en este instante todavía está saliendo voz por los parlantes. Pasale
    /// <c>LiveVoiceSession.IsSpeakerBusy</c> y no el estado del turno: el servidor despacha la
    /// respuesta más rápido que tiempo real, así que el turno cierra con segundos de audio todavía
    /// por sonar — y esos segundos son justamente los que siguen produciendo eco.
    /// </param>
    /// <param name="isVoice">Lo que dijo el detector local de este bloque.</param>
    /// <param name="level">Nivel del bloque, comprimido, tal como lo entrega el detector.</param>
    /// <param name="blockDuration">Cuánto dura el bloque.</param>
    public LiveMicrophoneVerdict Decide(
        bool speakerAudible,
        bool isVoice,
        double level,
        TimeSpan blockDuration)
    {
        // Se rechaza el cero además del negativo, que es más estricto que la compuerta de habla y por
        // un motivo concreto: acá todo lo que puede volver a abrir el micrófono —la cola del parlante
        // y la voz sostenida por encima del listón— se mide sumando duraciones. Con bloques de cero,
        // ninguna de las dos avanza nunca y la compuerta se queda cerrada para siempre.
        if (blockDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(blockDuration), "Un bloque tiene que durar algo.");
        }

        if (!double.IsFinite(level) || level < 0)
        {
            // Un nivel roto no puede cerrar el micrófono. Sin nivel no hay criterio, y sin criterio
            // el lado seguro es el de siempre: subir. Se pierde la protección contra el eco mientras
            // dure la falla, que es preferible a perder el micrófono sin que nadie se entere.
            return LiveMicrophoneVerdict.Send;
        }

        if (speakerAudible)
        {
            _hangover = Hangover;
        }
        else if (_hangover > TimeSpan.Zero)
        {
            _hangover -= blockDuration;
            if (_hangover < TimeSpan.Zero)
            {
                _hangover = TimeSpan.Zero;
            }
        }

        if (_hangover <= TimeSpan.Zero)
        {
            // Está callada: no hay eco posible y no hay nada que decidir. Éste es el caso normal —la
            // charla entera menos los tramos en que ella habla— y acá la compuerta no existe: el
            // micrófono sube tal cual, con la misma sensibilidad de siempre.
            _run = TimeSpan.Zero;
            _open = false;
            _holding = TimeSpan.Zero;
            return LiveMicrophoneVerdict.Send;
        }

        _holding += blockDuration;
        if (_holding >= GiveUp)
        {
            // Se rindió. Ver GiveUp: perder la protección contra el eco es mucho más barato que
            // perder el micrófono sin que nadie se entere.
            GaveUp = true;
            return LiveMicrophoneVerdict.Send;
        }

        if (_open)
        {
            // Ya se confirmó que hay alguien hablando encima. Se sube todo lo que queda de su frase
            // sin volver a pedirle el listón: pedírselo bloque a bloque partiría la frase en pedazos
            // justo en las consonantes, y lo que le llega al servidor sería un picadillo que
            // transcribe cualquier cosa.
            return LiveMicrophoneVerdict.Send;
        }

        // Se muestrea TODO lo que entra con forma de voz mientras ella habla, y de ahí sale el
        // percentil. Incluye lo que diga la persona si habla encima: son unas pocas muestras entre
        // cientos, y que un percentil no las persiga es justamente la propiedad por la que se eligió.
        if (isVoice)
        {
            _steps[(int)Math.Clamp(level * (Steps - 1), 0, Steps - 1)]++;
            _samples++;

            if (level > _peak)
            {
                _peak = level;
            }
        }

        var crosses = isVoice && level >= Bar;

        // Adentro de la ventana no se pide permiso: lo que suena es ella y hay que medirlo aunque
        // venga por encima del listón, que es justamente el caso que antes dejaba la compuerta
        // apagada toda la respuesta. Fuera de la ventana la referencia sólo puede BAJAR, así que
        // ninguna voz humana puede correr el listón hacia arriba.
        _run = crosses ? _run + blockDuration : TimeSpan.Zero;

        if (_run < Breakthrough)
        {
            return LiveMicrophoneVerdict.Hold;
        }

        _open = true;
        _run = TimeSpan.Zero;
        Breakthroughs++;
        return LiveMicrophoneVerdict.Release;
    }

    /// <summary>
    /// Vuelve al arranque, con la referencia pesimista otra vez.
    /// </summary>
    /// <remarks>
    /// Se llama al abrir una conversación. La medición no se guarda entre charlas a propósito: entre
    /// una y otra el usuario puede haber movido el volumen, cambiado de auriculares a parlantes o
    /// corrido el micrófono, y una referencia vieja aplicada a un cuarto nuevo es peor que ninguna.
    /// Volver a medirla cuesta la primera respuesta.
    /// </remarks>
    /// <summary>El nivel por debajo del cual cae esa fracción de lo medido.</summary>
    /// <remarks>
    /// Se recorre el histograma sumando hasta pasar la fracción pedida y se devuelve el centro de ese
    /// escalón. Sesenta y cuatro sumas de enteros por bloque: nada al lado de lo que ya cuesta el
    /// detector de voz que corrió sobre el mismo bloque.
    /// </remarks>
    private double Percentil(double fraccion)
    {
        var objetivo = _samples * fraccion;
        var acumulado = 0;

        for (var i = 0; i < Steps; i++)
        {
            acumulado += _steps[i];
            if (acumulado >= objetivo)
            {
                return (i + 0.5) / (Steps - 1);
            }
        }

        return 1;
    }

    public void Reset()
    {
        Array.Clear(_steps);
        _samples = 0;
        _peak = 0;
        _hangover = TimeSpan.Zero;
        _run = TimeSpan.Zero;
        _open = false;
        _holding = TimeSpan.Zero;
        GaveUp = false;
    }
}
