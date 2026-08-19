using System.Windows;
using Viernes.App.Controls;
using Point = System.Windows.Point;

namespace Viernes.App.Shell;

/// <summary>
/// La física del orbe: arrastre con inercia, rebote contra los bordes e imán en las esquinas.
/// </summary>
/// <remarks>
/// El paso de integración tiene <b>techo</b> en 1/120 s: nunca es más largo que eso, aunque el
/// cuadro lo sea. Con paso libre, un cuadro largo mete un salto grande, el resorte se pasa y el
/// rebote cambia de altura según lo ocupada que esté la máquina; un objeto que rebota distinto cada
/// vez deja de leerse como objeto.
/// <para>
/// <b>Techo y no piso</b>, y la diferencia importa: acá decía «pasos fijos de 1/120 s y no el
/// <c>dt</c> del cuadro», y eso es falso por arriba de 120 Hz. En el monitor de 180 Hz donde se
/// midió esto el cuadro dura 5,56 ms —menos que los 8,33 del subpaso—, así que el bucle corre un
/// solo paso y ese paso es el del cuadro. Está bien que así sea: hacerlo piso pondría la física a
/// 120 mientras la ventana se dibuja a 180, y el orbe se movería en dos de cada tres cuadros, que
/// es exactamente el desfase del que se quejó el usuario.
/// </para>
/// <para>
/// Lo que el techo promete —que el pique se vea igual a cualquier frecuencia— está medido en
/// <c>OrbBounceRateTests</c>: entre 30 y 240 Hz el pique da entre 197,6 y 203,5 px, un 3 %. 30 y 60
/// dan exactamente lo mismo porque sus cuadros son múltiplos enteros del subpaso; las que dejan
/// resto se separan ese poco.
/// </para>
/// <para>
/// Las constantes salen medidas de la referencia ejecutable y no son intercambiables entre sí: el
/// resorte del arrastre es más duro que el de reposo porque tiene que seguir al dedo, y el
/// rozamiento del vuelo es exponencial para que la desaceleración se vea, no se calcule.
/// </para>
/// </remarks>
internal sealed class OrbMotion
{
    /// <summary>Cuánto se separa del borde de la pantalla cuando se apoya.</summary>
    public const double Margin = 20;

    /// <summary>A menos de esta distancia de un borde, el orbe termina pegado a él.</summary>
    public const double MagnetRange = 58;

    private const double SubStep = 1.0 / 120;
    private const double MaxFrame = 0.05;

    /// <summary>
    /// Rigidez del resorte del arrastre. Más dura que la del fuente, y por lo que dijo el usuario.
    /// </summary>
    /// <remarks>
    /// El fuente trae 146. Con eso —y ya amortiguado— el orbe tarda unos 250 ms en alcanzar la mano,
    /// y sostener el cursor quieto se siente como que el orbe llega tarde a todos lados: «se tarda en
    /// soltar, no reacciona bien al cursor mantenido».
    /// <para>
    /// En 300 la demora baja a unos 145 ms. Sigue habiendo retraso visible —el orbe cuelga de la
    /// mano, no está pegado— pero deja de leerse como que hay que esperarlo. Es el segundo y último
    /// número de la física que no sale de la referencia; el otro es su amortiguación, que va atada.
    /// </para>
    /// </remarks>
    private const double DragStiffness = 300;

    /// <summary>
    /// Amortiguación del resorte del arrastre. Crítica: sigue a la mano sin pasarse.
    /// </summary>
    /// <remarks>
    /// El fuente trae 15,5 sobre una rigidez de 146. Acá está en 2·√300 ≈ 34,64, que es el valor
    /// crítico para la rigidez de <see cref="DragStiffness"/>. Va atada a ella: si se cambia una hay
    /// que recalcular la otra o vuelve el sobrepaso. Se cambió porque el usuario dijo que el arrastre
    /// se sentía «tosco» y «medio raro».
    /// <para>
    /// Con 15,5 el amortiguamiento relativo es ζ ≈ 0,64: <b>subamortiguado</b>. El orbe no sólo se
    /// queda atrás de la mano —eso es el peso, y es deliberado— sino que al frenar la <em>pasa</em> y
    /// vuelve. Arrastrando en círculos eso se lee como que el orbe orbita alrededor del cursor en vez
    /// de colgar de él, que es exactamente «no sigue tanto el mouse, es medio raro».
    /// </para>
    /// <para>
    /// En ζ = 1 la demora se conserva entera —la constante de tiempo pasa de 0,13 s a 0,083 s, sigue
    /// habiendo retraso visible— y desaparece el sobrepaso. Es la diferencia entre algo que cuelga y
    /// algo que rebota. El peso no era el sobrepaso.
    /// </para>
    /// <para>
    /// El resorte de REPOSO (104/14,5, ζ ≈ 0,71) no se tocó: ahí el sobrepaso sí corresponde, porque
    /// es el orbe acomodándose solo contra un borde y no siguiendo una mano.
    /// </para>
    /// </remarks>
    private const double DragDamping = 34.64;
    private const double RestStiffness = 104;
    private const double RestDamping = 14.5;
    private const double FlyFriction = 0.075;
    private const double SettleSpeed = 44;
    private const double MinBounceSpeed = 18;

    /// <summary>Por debajo de esta rapidez el rebote no le llega al cuerpo.</summary>
    /// <remarks>
    /// Del fuente: <c>if (sp > 220) { M.hit = … }</c>. Apoyarse contra un borde no es un choque, y
    /// un achatamiento cada vez que el imán termina de acomodar el orbe se leería como un tic.
    /// </remarks>
    private const double HitSpeed = 220;

    /// <summary>Cuánto pesa la lectura vieja en el promedio corrido de la velocidad.</summary>
    /// <remarks>
    /// 0,72 lo viejo y 0,28 lo nuevo, del fuente. Sin suavizar, un solo cuadro largo manda la estela
    /// a cualquier parte y el cuerpo pega un tirón que no corresponde a ningún movimiento real.
    /// </remarks>
    private const double VelocityMemory = 0.72;

    /// <summary>Piso del intervalo al derivar la velocidad. Un dt chico inventa velocidades.</summary>
    private const double MinVelocitySpan = 0.008;

    /// <summary>Dónde está el orbe ahora, esquina superior izquierda de sus 108 px.</summary>
    public Point Position { get; private set; }

    /// <summary>A dónde tiende cuando no está volando: el imán, o el dedo.</summary>
    public Point Target { get; private set; }

    /// <summary>Velocidad en píxeles por segundo.</summary>
    public Vector Velocity { get; private set; }

    /// <summary>Si el usuario lo tiene agarrado.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Si viene de ser soltado y todavía tiene inercia.</summary>
    public bool IsFlying { get; private set; }

    /// <summary>
    /// Si ya terminó de moverse: ni agarrado, ni volando, y el resorte de reposo ya lo dejó.
    /// </summary>
    /// <remarks>
    /// Usa <see cref="SettleSpeed"/>, el mismo umbral con el que <see cref="StepFlight"/> decide que
    /// el vuelo se acabó, y no un número nuevo: «dejó de volar» y «se quedó quieto» tienen que ser
    /// la misma frontera o hay un tramo en el que no es ninguna de las dos cosas.
    /// </remarks>
    public bool IsAtRest => !IsDragging && !IsFlying && Speed < SettleSpeed;

    /// <summary>Rapidez instantánea del integrador. Es la que decide si sigue volando y cuánto rebota.</summary>
    public double Speed => Velocity.Length;

    /// <summary>
    /// Velocidad de la ventana suavizada, en px/s. Es la que ve el cuerpo.
    /// </summary>
    /// <remarks>
    /// No es <see cref="Velocity"/>. Aquélla es el estado interno del integrador —durante el
    /// arrastre es la velocidad del resorte, no la del orbe en pantalla— y da saltos entre subpasos.
    /// Ésta se mide sobre el desplazamiento real del cuadro completo, que es lo único que el ojo vio.
    /// </remarks>
    public Vector Smoothed { get; private set; }

    /// <summary>Rapidez suavizada. Es la que decide si el vidrio se retrae.</summary>
    public double SmoothSpeed => Smoothed.Length;

    /// <summary>
    /// Cuán rápido venía moviéndose la mano. Es con lo que sale el orbe al soltarlo.
    /// </summary>
    /// <remarks>
    /// No es <see cref="Velocity"/> ni <see cref="Smoothed"/>. Aquéllas describen al orbe; ésta
    /// describe al cursor, que es quien lo tira. Ver <see cref="Drop"/>.
    /// </remarks>
    public Vector HandVelocity { get; private set; }

    /// <summary>
    /// Cuánto hacia atrás se mira para saber a qué velocidad venía la mano.
    /// </summary>
    /// <remarks>
    /// No sale del boceto: es de acá, y sale de cómo se tira algo de verdad. <b>Nadie suelta el botón
    /// en el mismo instante en que deja de mover el mouse</b>: levantar el dedo lleva su tiempo y en
    /// esos milisegundos la mano ya está quieta. Un gesto real es «envión, freno un instante, suelto».
    /// <para>
    /// Acá había un promedio corrido con memoria por cuadro (0,72), que a 180 Hz da una constante de
    /// tiempo de unos 17 ms: el envión se olvidaba en tres cuadros. Medido, con una pausa de 60 ms
    /// antes de soltar —que es de lo más común— un envión de 1400 px/s salía a <b>19</b>. Eso es «lo
    /// suelto y se queda en el lugar».
    /// </para>
    /// <para>
    /// Con una ventana de tiempo la pausa corta no borra nada, porque el envión sigue adentro de los
    /// últimos 120 ms; y una pausa larga sí lo borra, que es lo correcto: si apoyaste el orbe y te
    /// quedaste, no lo estás tirando. Es lo mismo que hacen los sistemas de toque, por la misma razón.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan HandWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Cuánto tarda en apagarse un envión después de que la mano se detuvo.
    /// </summary>
    /// <remarks>
    /// Con la ventana sola no alcanzaba, y está medido con trece tiros del usuario: el pico de la
    /// mano daba entre 2469 y 6545 px/s y el tiro salía en CERO seis de trece veces. En todas ésas
    /// el objetivo y el cursor coincidían exactamente al soltar, o sea que la mano ya estaba quieta.
    /// <para>
    /// El error era tomar el desplazamiento a lo largo de la ventana entera: una pausa dentro de la
    /// ventana lo diluye, y una pausa más larga que la ventana lo borra. Pero frenar antes de soltar
    /// no es arrepentirse —levantar el dedo lleva su tiempo— y lo que uno espera es que lo que
    /// venías haciendo todavía cuente.
    /// </para>
    /// <para>
    /// Ahora se busca el PICO de los últimos 300 ms y se lo apaga según hace cuánto fue, con una
    /// gracia antes de empezar a apagarlo: hasta 80 ms sale entero, a 240 ms sale a la mitad, a
    /// 400 ms ya no sale. Un envión seguido de una pausa normal tira; apoyarlo y quedarse quieto,
    /// no. Es lo que hace la mano de verdad.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan HandFade = TimeSpan.FromMilliseconds(320);

    /// <summary>Cuánto puede estar quieta la mano antes de que el envión empiece a apagarse.</summary>
    private static readonly TimeSpan HandGrace = TimeSpan.FromMilliseconds(80);

    /// <summary>Dónde estuvo la mano en los últimos <see cref="HandWindow"/>, para poder tirar.</summary>
    private readonly List<(double At, Point Where)> _hand = [];

    /// <summary>Reloj del arrastre, en segundos desde que se agarró.</summary>
    private double _dragClock;

    /// <summary>Cuándo entró la última muestra de la mano, en el reloj del arrastre.</summary>
    private double _lastSampleAt;

    /// <summary>Cuántas muestras de la mano hay en la ventana. Para poder mirar qué decidió el tiro.</summary>
    public int HandSamples => _hand.Count;

    /// <summary>La rapidez más alta que alcanzó la mano en este arrastre.</summary>
    /// <remarks>
    /// Es el número que separa las dos explicaciones posibles de «no lo puedo tirar»: si el pico fue
    /// alto y el tiro salió en cero, el problema es la ventana de tiempo o la pausa; si el pico
    /// también fue cero, el objetivo nunca siguió al cursor y el problema está antes.
    /// </remarks>
    public double HandPeak { get; private set; }

    /// <summary>Cuánto duró el arrastre, en segundos.</summary>
    public double DragSeconds => _dragClock;

    /// <summary>
    /// Cuánto hace que no entra una muestra de la mano, en segundos.
    /// </summary>
    /// <remarks>
    /// Las muestras las pone el bucle de cuadro. Si el hilo de la interfaz está ocupado dibujando, el
    /// bucle no corre, y el orbe se puede soltar con una foto de la mano vieja. Esto es lo que deja
    /// verlo desde afuera en vez de deducirlo.
    /// </remarks>
    public double SinceLastSample => _dragClock - _lastSampleAt;

    private int _hitToken;
    private double _hitNormalX;
    private double _hitNormalY;
    private double _hitStrength;

    /// <summary>Hasta cuándo el golpe sigue siendo noticia. Ver <see cref="Sample"/>.</summary>
    private DateTime _hitFreshUntilUtc = DateTime.MinValue;

    /// <summary>
    /// Cuánto vale un golpe como noticia. Pasado eso, el token queda pero la fuerza se apaga.
    /// </summary>
    /// <remarks>
    /// Un cuerpo consume el golpe en el cuadro siguiente, así que con dos cuadros a 30 fps sobra. El
    /// punto no es el número: es que la fuerza y la normal dejen de ser estado permanente.
    /// </remarks>
    private static readonly TimeSpan HitFreshness = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Lo que hay que contarle al cuerpo en este cuadro.
    /// </summary>
    /// <remarks>
    /// El golpe se apaga solo pasado <see cref="HitFreshness"/>, y el token queda. Antes la fuerza y
    /// la normal del último choque se guardaban para siempre: un cuerpo recién creado —cambiar de
    /// gota a nube— recibía el token viejo y, si no lo adoptaba sin ejecutarlo, pegaba el respingo
    /// entero de un choque de hace media hora. El cuerpo ya se protege de eso adoptando el token, y
    /// esto cierra la misma puerta del otro lado: el emisor deja de ser una fuente de estado viejo
    /// para cualquier consumidor que se agregue después y no conozca la regla.
    /// </remarks>
    public OrbMotionSample Sample
    {
        get
        {
            var fresh = DateTime.UtcNow <= _hitFreshUntilUtc;
            return new OrbMotionSample(
                Smoothed.X,
                Smoothed.Y,
                IsDragging,
                _hitToken,
                fresh ? _hitNormalX : 0,
                fresh ? _hitNormalY : 0,
                fresh ? _hitStrength : 0);
        }
    }

    /// <summary>Deja el orbe donde se lo pone, sin inercia ni destino pendiente.</summary>
    public void Teleport(Point position)
    {
        Position = position;
        Target = position;
        Velocity = default;
        Smoothed = default;
        IsFlying = false;
    }

    /// <summary>
    /// Le da un destino sin moverlo: el resorte de reposo lo lleva hasta ahí.
    /// </summary>
    /// <remarks>
    /// Es lo que usa la llegada desde la bandeja. Animar <c>Left</c> y <c>Top</c> con un
    /// <c>Storyboard</c> pelearía con el bucle de física, que escribe la posición cuadro a cuadro;
    /// darle un destino al mismo resorte que ya existe hace que la llegada se mueva igual que
    /// cualquier otro desplazamiento del orbe, que es justamente lo que se quiere.
    /// </remarks>
    public void Nudge(Point target)
    {
        IsFlying = false;
        Target = target;
    }

    /// <summary>
    /// Lo lanza hacia un destino con un empujón, en vez de llevarlo con el resorte.
    /// </summary>
    /// <remarks>
    /// Es el viaje entre monitores vecinos. La diferencia con <see cref="Nudge"/> no es de velocidad
    /// sino de tipo de movimiento: el resorte de reposo <em>tira</em> del orbe y llega frenando; esto
    /// lo <em>suelta</em> y llega con la inercia que le quedó, rebotando contra el borde si sobró.
    /// Un objeto que cruza una pantalla tirado por un resorte se ve arrastrado por un hilo; tirado de
    /// un envión, se ve viajando.
    /// <para>
    /// El empuje es proporcional a la distancia —no una velocidad fija— así que cruzar una pantalla
    /// chica y una grande tarda parecido.
    /// </para>
    /// </remarks>
    /// <param name="target">Dónde tiene que terminar.</param>
    /// <param name="kick">Cuánto empuje horizontal por píxel de distancia.</param>
    /// <param name="lift">Empujón vertical del despegue. Negativo arquea hacia arriba.</param>
    public void Launch(Point target, double kick, double lift)
    {
        IsDragging = false;
        IsFlying = true;
        Target = target;
        Velocity = new Vector((target.X - Position.X) * kick, lift);
    }

    /// <summary>Empieza el arrastre. Desde acá el destino lo manda el puntero.</summary>
    /// <remarks>
    /// El destino arranca donde está el orbe y no donde está el dedo: si arrancara en el dedo, el
    /// primer cuadro del arrastre sería un salto de todo el radio del orbe.
    /// </remarks>
    public void BeginDrag()
    {
        IsDragging = true;
        IsFlying = false;
        Target = Position;

        // La medición de la mano arranca de cero, y desde donde está el orbe. Si quedaran las
        // muestras del arrastre anterior, el primer cuadro derivaría una velocidad entre dos puntos
        // que no tienen nada que ver, y el orbe saldría disparado apenas lo agarrás.
        _hand.Clear();
        _dragClock = 0;
        _lastSampleAt = 0;
        _hand.Add((0, Position));
        HandVelocity = default;
        HandPeak = 0;
    }

    /// <summary>
    /// Mueve el <em>objetivo</em> del arrastre. El orbe llega ahí por el resorte, no de un salto.
    /// </summary>
    /// <remarks>
    /// Acá estaba <c>ReportDragged</c>, que anotaba la posición que Windows le había impuesto a la
    /// ventana con <c>DragMove()</c> y estimaba la velocidad para poder soltarla. Mientras existió,
    /// el resorte de arrastre 146/15,5 nunca corrió: el tick llamaba a <c>ReportDragged</c> y jamás a
    /// <see cref="Step"/>. Y <c>DragMove()</c> clavaba la ventana al cursor, así que el orbe no podía
    /// quedarse atrás de la mano —que es exactamente lo que le da peso—.
    /// </remarks>
    public void DragTo(Point target)
    {
        if (!IsDragging)
        {
            return;
        }

        Target = target;
    }

    /// <summary>
    /// Suelta el orbe y lo tira con la velocidad que traía <b>la mano</b>.
    /// </summary>
    /// <remarks>
    /// Sale con <see cref="HandVelocity"/> y no con <see cref="Velocity"/>. Con la del resorte, un
    /// arrastre bien amortiguado no se podía tirar: el orbe ya estaba encima del cursor y su
    /// velocidad interna era casi cero, así que soltarlo lo dejaba caer ahí mismo. Y al revés, con un
    /// resorte flojo el tiro salía de la inercia del propio resorte —o sea de lo mal que seguía a la
    /// mano—, que es una forma rara de decidir con cuánta fuerza sale algo.
    /// <para>
    /// Con la de la mano las dos cosas quedan separadas y cada una hace lo suyo: el resorte decide
    /// cómo se siente arrastrar, y el tiro sale de cuán rápido movías el cursor al soltar. Es lo que
    /// hace cualquier cosa que uno arroja.
    /// </para>
    /// </remarks>
    public void Drop()
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        IsFlying = true;
        Velocity = HandVelocity;

        // Soltarlo casi quieto no debería mandarlo a ningún lado: el resto lo hace el imán.
        if (Speed < 60)
        {
            Velocity *= 0.5;
        }

        HandVelocity = default;
        _hand.Clear();
    }

    /// <summary>
    /// Avanza la simulación. <paramref name="bounds"/> es dónde puede estar la esquina del orbe.
    /// </summary>
    public void Step(double dt, Rect bounds)
    {
        var previous = Position;

        // Mientras se arrastra se mide la velocidad DE LA MANO, y con eso se tira después.
        //
        // Antes el tiro salía con la velocidad del resorte, y eso funcionaba de casualidad: el
        // resorte iba tan atrás del cursor que siempre traía inercia. Con la amortiguación crítica
        // llega al cursor y se queda, así que si uno frena aunque sea un instante antes de soltar
        // —que es lo que hace todo el mundo— el resorte estaba parado y el orbe no salía a ningún
        // lado. «No lo puedo tirar»: el peso y el tiro dependían del mismo defecto.
        //
        // Separadas, cada una hace lo suyo: la amortiguación decide cómo se siente seguir a la mano,
        // y el tiro sale de cuán rápido movías el cursor. Que es como se tira algo.
        if (IsDragging)
        {
            _dragClock += dt;
            _hand.Add((_dragClock, Target));
            _lastSampleAt = _dragClock;

            // Se tiran las muestras viejas, pero se conserva SIEMPRE una del otro lado del borde de
            // la ventana: sin ella, con la mano quieta se irían acumulando muestras iguales hasta
            // que la más vieja también estuviera quieta, y la velocidad daría cero antes de tiempo.
            var corte = _dragClock - HandWindow.TotalSeconds;
            while (_hand.Count > 2 && _hand[1].At < corte)
            {
                _hand.RemoveAt(0);
            }

            // El envión es el tramo MÁS RÁPIDO de la ventana, no el promedio de toda ella. Se mide
            // sobre pares de muestras separadas al menos 30 ms —menos que eso es ruido de un cuadro
            // suelto— y se apaga según hace cuánto pasó.
            var mejor = default(Vector);
            var mejorRapidez = 0.0;
            var mejorFin = 0.0;

            for (var i = 0; i < _hand.Count; i++)
            {
                for (var j = i + 1; j < _hand.Count; j++)
                {
                    var tramo = _hand[j].At - _hand[i].At;
                    if (tramo < 0.030)
                    {
                        continue;
                    }

                    var v = new Vector(
                        (_hand[j].Where.X - _hand[i].Where.X) / tramo,
                        (_hand[j].Where.Y - _hand[i].Where.Y) / tramo);

                    // Mayor o IGUAL, no mayor: con la mano a velocidad pareja todos los tramos
                    // empatan, y como los pares se recorren de viejo a nuevo, con «mayor» ganaba el
                    // más viejo y la decadencia lo castigaba por algo que seguía pasando. Empate va
                    // al más reciente.
                    if (v.Length >= mejorRapidez)
                    {
                        mejorRapidez = v.Length;
                        mejor = v;
                        mejorFin = _hand[j].At;
                    }
                }
            }

            // Primero una gracia, y recién después el apagado. Levantar el dedo del botón lleva su
            // tiempo y en esos milisegundos la mano ya está quieta: castigar eso es castigar la
            // mecánica de soltar, no un cambio de intención.
            var desdeElPico = _dragClock - mejorFin;
            var vigencia = Math.Clamp(
                1 - ((desdeElPico - HandGrace.TotalSeconds) / HandFade.TotalSeconds),
                0,
                1);

            HandVelocity = mejor * vigencia;
            HandPeak = Math.Max(HandPeak, mejorRapidez);
        }

        var remaining = Math.Min(MaxFrame, dt);
        while (remaining > 0.0001)
        {
            var h = remaining > SubStep ? SubStep : remaining;
            remaining -= h;

            if (IsDragging)
            {
                StepDrag(h);
            }
            else if (IsFlying)
            {
                StepFlight(h, bounds);
            }
            else
            {
                StepSettle(h, bounds);
            }
        }

        // La velocidad que ve el cuerpo se mide una vez por cuadro sobre el desplazamiento entero, y
        // no subpaso a subpaso: el ojo vio el cuadro, no los doce subpasos de adentro.
        var span = Math.Max(MinVelocitySpan, dt);
        Smoothed = new Vector(
            (Smoothed.X * VelocityMemory) + ((Position.X - previous.X) / span * (1 - VelocityMemory)),
            (Smoothed.Y * VelocityMemory) + ((Position.Y - previous.Y) / span * (1 - VelocityMemory)));
    }

    /// <summary>
    /// El resorte del arrastre: duro, para seguir al dedo, pero resorte al fin.
    /// </summary>
    /// <remarks>
    /// Es más duro que el de reposo —146 contra 104— porque tiene que alcanzar la mano, y aun así se
    /// queda atrás al arrancar. Esa demora <em>es</em> el peso del orbe: con <c>DragMove()</c> no
    /// había forma de tenerla, porque Windows pega la ventana al cursor.
    /// <para>
    /// Lo que <b>no</b> es el peso es pasarse al frenar. Acá decía que se pasaba «un poco» y lo daba
    /// por parte del efecto; no lo es. Ver <see cref="DragDamping"/>: la amortiguación es crítica y
    /// el orbe llega al cursor sin cruzarlo.
    /// </para>
    /// </remarks>
    private void StepDrag(double h)
    {
        Velocity = new Vector(
            Velocity.X + ((DragStiffness * (Target.X - Position.X)) - (DragDamping * Velocity.X)) * h,
            Velocity.Y + ((DragStiffness * (Target.Y - Position.Y)) - (DragDamping * Velocity.Y)) * h);

        Position = new Point(Position.X + (Velocity.X * h), Position.Y + (Velocity.Y * h));
    }

    private void StepFlight(double h, Rect bounds)
    {
        var friction = Math.Pow(FlyFriction, h);
        Position = new Point(Position.X + Velocity.X * h, Position.Y + Velocity.Y * h);
        Velocity = new Vector(Velocity.X * friction, Velocity.Y * friction);

        if (Position.X < bounds.Left)
        {
            Position = new Point(bounds.Left, Position.Y);
            Bounce(1, 0);
        }
        else if (Position.X > bounds.Right)
        {
            Position = new Point(bounds.Right, Position.Y);
            Bounce(-1, 0);
        }

        if (Position.Y < bounds.Top)
        {
            Position = new Point(Position.X, bounds.Top);
            Bounce(0, 1);
        }
        else if (Position.Y > bounds.Bottom)
        {
            Position = new Point(Position.X, bounds.Bottom);
            Bounce(0, -1);
        }

        if (Speed >= SettleSpeed)
        {
            return;
        }

        IsFlying = false;
        Velocity *= 0.5;
        Target = Magnetize(Position, bounds);
    }

    private void StepSettle(double h, Rect bounds)
    {
        Target = new Point(
            Math.Clamp(Target.X, bounds.Left, bounds.Right),
            Math.Clamp(Target.Y, bounds.Top, bounds.Bottom));

        Velocity = new Vector(
            Velocity.X + (RestStiffness * (Target.X - Position.X) - RestDamping * Velocity.X) * h,
            Velocity.Y + (RestStiffness * (Target.Y - Position.Y) - RestDamping * Velocity.Y) * h);

        Position = new Point(Position.X + Velocity.X * h, Position.Y + Velocity.Y * h);
    }

    /// <summary>
    /// El rebote devuelve entre el 14 % y el 46 % de la velocidad según con cuánta fuerza llegó: una
    /// pelota lanzada rebota, una apoyada no.
    /// </summary>
    private void Bounce(double nx, double ny)
    {
        var speed = Speed;
        var restitution = Math.Clamp(0.14 + speed / 3600, 0.14, 0.46);

        Velocity = nx != 0
            ? new Vector(Math.Abs(Velocity.X) * restitution * nx, Velocity.Y * 0.86)
            : new Vector(Velocity.X * 0.86, Math.Abs(Velocity.Y) * restitution * ny);

        Velocity = new Vector(
            Math.Abs(Velocity.X) < MinBounceSpeed ? 0 : Velocity.X,
            Math.Abs(Velocity.Y) < MinBounceSpeed ? 0 : Velocity.Y);

        // Recién acá el golpe deja de ser un asunto de la ventana. Antes la ventana rebotaba y nadie
        // le avisaba a la gota que había chocado: el rebote existía y no se veía.
        if (speed <= HitSpeed)
        {
            return;
        }

        _hitToken++;
        _hitNormalX = nx;
        _hitNormalY = ny;
        _hitStrength = Math.Min(1, speed / OrbMotionSample.SpeedReference);
        _hitFreshUntilUtc = DateTime.UtcNow + HitFreshness;
    }

    /// <summary>
    /// El imán: a menos de 58 px de un borde, el orbe termina pegado. Es lo que hace que quede
    /// prolijo sin pedirle puntería a nadie.
    /// </summary>
    private static Point Magnetize(Point position, Rect bounds)
    {
        var x = Math.Clamp(position.X, bounds.Left, bounds.Right);
        var y = Math.Clamp(position.Y, bounds.Top, bounds.Bottom);

        if (x < bounds.Left + MagnetRange)
        {
            x = bounds.Left;
        }
        else if (x > bounds.Right - MagnetRange)
        {
            x = bounds.Right;
        }

        if (y < bounds.Top + MagnetRange)
        {
            y = bounds.Top;
        }
        else if (y > bounds.Bottom - MagnetRange)
        {
            y = bounds.Bottom;
        }

        return new Point(x, y);
    }

    /// <summary>Ajusta la posición cuando cambia el área útil, sin pegar un salto.</summary>
    public void ClampInto(Rect bounds)
    {
        Position = new Point(
            Math.Clamp(Position.X, bounds.Left, bounds.Right),
            Math.Clamp(Position.Y, bounds.Top, bounds.Bottom));
        Target = new Point(
            Math.Clamp(Target.X, bounds.Left, bounds.Right),
            Math.Clamp(Target.Y, bounds.Top, bounds.Bottom));
    }
}
