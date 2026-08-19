using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Viernes.App.Controls;
using Viernes.App.Shell;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Renderiza el orbe a hojas de contactos PNG sin abrir nada en pantalla. Existe para poder juzgar
/// la forma real que dibuja WPF en vez de ajustarla a ciegas.
/// </summary>
/// <remarks>
/// Se invoca con <c>Viernes.exe --render-orb &lt;carpeta&gt;</c> y termina el proceso al guardar.
/// No toca preferencias, ni voz, ni red.
/// <para>
/// Salen <b>dos</b> hojas por cuerpo: la de los quince estados y la del movimiento. La segunda
/// existe porque la primera no puede contestar la única pregunta que importaba de esta vuelta —«si
/// lo tiro contra un borde, ¿se achata?»—: el orbe está quieto en las quince celdas, y un cuerpo
/// que ignora su propio movimiento se ve idéntico a uno que no. Se porta un comportamiento, no un
/// archivo, y un comportamiento que no se puede mirar no está hecho.
/// </para>
/// </remarks>
internal static class OrbSnapshot
{
    private const int Cell = 108;
    private const int Zoom = 3;
    private const double Dpi = 96 * Zoom;

    /// <summary>
    /// Cuántos por fila. Lo usan la hoja y el reparto de escritorio claro, y tiene que ser el mismo.
    /// </summary>
    /// <remarks>
    /// Estaba escrito dos veces —acá cuatro columnas, allá «claro a partir del quinto»— y con doce
    /// cuadros daba igual porque coincidía. Con los quince estados dejó de coincidir: la hoja pintaba
    /// las filas alternadas y el dibujo cambiaba a la quinta, así que dos filas mostraban el orbe
    /// para escritorio claro sobre fondo oscuro. Una hoja de contactos que miente es peor que no
    /// tenerla: se hicieron decisiones de color mirándola.
    /// </remarks>
    private const int Columns = 4;

    /// <summary>Las filas impares de la hoja van sobre fondo claro, y el orbe tiene que saberlo.</summary>
    private static bool IsLightRow(int index) => (index / Columns) % 2 == 1;

    /// <summary>
    /// Los quince, en el orden en que pasan.
    /// </summary>
    /// <remarks>
    /// Ya no hay columna de micrófono. El armado era una bandera encima de reposo y ahora es
    /// <see cref="AssistantVisualState.Watching"/>, con su fila entera en la tabla: se renderiza
    /// como cualquier otro.
    /// <para>
    /// Los tres que se parecen de reojo —trabajando sin vos, un proyecto te espera y esperándote—
    /// van seguidos a propósito. Separarlos era lo que la referencia dejó sin decidir, y se
    /// decidieron por presencia; la hoja de contactos es donde se comprueba si de verdad se
    /// distinguen. Mirarlos de a uno no sirve para eso.
    /// </para>
    /// </remarks>
    private static readonly (AssistantVisualState State, double Seconds)[] Frames =
    [
        // Fila 1 — el hilo de una charla, de punta a punta.
        (AssistantVisualState.Idle, 0.4),
        (AssistantVisualState.Watching, 1.0),
        (AssistantVisualState.Listening, 1.2),
        (AssistantVisualState.Thinking, 1.2),

        // Fila 2 — lo que dice y lo que pide.
        (AssistantVisualState.Speaking, 1.2),
        (AssistantVisualState.Interrupted, 0.6),
        (AssistantVisualState.Attention, 1.2),
        (AssistantVisualState.AskingPermission, 1.2),

        // Fila 3 — el trío que la referencia dejó sin resolver, en una sola fila y con reposo al
        // lado. Se separaron por presencia y no por color: trabajando sin vos no espera nada, un
        // proyecto te espera espera a otro, esperándote es el único que te debe algo a vos. Ver los
        // tres a la vez es la única forma de saber si eso se nota; de a uno siempre se distinguen.
        (AssistantVisualState.Background, 1.2),
        (AssistantVisualState.ProjectWaiting, 1.2),
        (AssistantVisualState.WaitingForYou, 1.2),
        (AssistantVisualState.Idle, 1.2),

        // Fila 4 — las cuatro que no son buenas noticias, juntas. La prueba de que capacidad
        // reducida se distingue de una falla no es mirarlas solas: es verlas al lado del rojo.
        (AssistantVisualState.Error, 1.2),
        (AssistantVisualState.Deaf, 1.2),
        (AssistantVisualState.Unconfigured, 1.2),
        (AssistantVisualState.Offline, 1.2),

        // Fila 5 — tarea larga: el sedimento se acumula abajo. Tres momentos, para verlo subir.
        (AssistantVisualState.Thinking, 14),
        (AssistantVisualState.Thinking, 30),
        (AssistantVisualState.Thinking, 70)
    ];

    /// <summary>
    /// Los cuadros de la hoja de movimiento: qué velocidad se le pone al cuerpo y cuánto se la deja
    /// actuar antes de sacarle la foto.
    /// </summary>
    /// <remarks>
    /// El estado es siempre reposo a propósito. Con un estado agitado no habría forma de saber si lo
    /// que se ve es la deformación del viaje o el gesto del estado, y la hoja serviría para
    /// confirmar cualquier cosa.
    /// </remarks>
    /// <param name="Caption">Lo que va debajo de la celda. Lleva el número, no un adjetivo.</param>
    /// <param name="Speed">Rapidez de la ventana en px/s. 1500 es el tope del efecto.</param>
    /// <param name="Degrees">Hacia dónde viaja. 0 es a la derecha, 90 hacia abajo.</param>
    /// <param name="Dragging">Si el usuario lo tiene agarrado. Sólo así la gota pierde polvo.</param>
    /// <param name="HitToken">Cambiarlo dispara un golpe. Repetirlo deja ver cómo sigue el anterior.</param>
    /// <param name="HitNormalX">Normal del borde golpeado: −1 el derecho.</param>
    /// <param name="HitNormalY">Normal del borde golpeado: −1 el de abajo.</param>
    /// <param name="Seconds">Cuánto se deja correr esta situación antes de la foto.</param>
    private readonly record struct MotionFrame(
        string Caption,
        double Speed,
        double Degrees,
        bool Dragging,
        int HitToken,
        double HitNormalX,
        double HitNormalY,
        double Seconds);

    /// <summary>
    /// Las tres preguntas de la hoja: ¿se estira?, ¿se estira hacia donde va?, ¿acusa el golpe y el
    /// arrastre?
    /// </summary>
    /// <remarks>
    /// La fila del medio cae sobre fondo claro —<see cref="IsLightRow"/> con cuatro columnas— y eso
    /// es a propósito: el halo de la nube cambia de fórmula con el escritorio, así que la fila de
    /// las cuatro direcciones sirve además para verlo del otro lado.
    /// <para>
    /// Los dos cuadros del golpe comparten el mismo <c>HitToken</c>: el segundo no dispara un golpe
    /// nuevo, muestra cómo termina el primero. Un golpe que sólo se ve en el cuadro en que ocurre no
    /// se puede juzgar en una hoja de contactos.
    /// </para>
    /// </remarks>
    private static readonly MotionFrame[] MotionFrames =
    [
        // Fila 1 — la misma dirección, cuatro rapideces. Es la escala del efecto.
        new("quieto", 0, 0, false, 0, 0, 0, 1.0),
        new("→ 375 px/s · 25 %", 375, 0, false, 0, 0, 0, 0.8),
        new("→ 825 px/s · 55 %", 825, 0, false, 0, 0, 0, 0.8),
        new("→ 1500 px/s · 100 %", 1500, 0, false, 0, 0, 0, 0.8),

        // Fila 2 — la misma rapidez, cuatro direcciones. Si el estirón no sigue al viaje, se ve acá.
        new("↓ 1125 px/s · 90°", 1125, 90, false, 0, 0, 0, 0.8),
        new("↘ 1125 px/s · 45°", 1125, 45, false, 0, 0, 0, 0.8),
        new("← 1125 px/s · 180°", 1125, 180, false, 0, 0, 0, 0.8),
        new("↑ 1125 px/s · 270°", 1125, 270, false, 0, 0, 0, 0.8),

        // Fila 3 — los dos eventos. El golpe llega contra el borde derecho a fondo y rebota.
        new("golpe borde derecho · 0,10 s", 260, 180, false, 1, -1, 0, 0.10),
        new("golpe borde derecho · 0,45 s", 120, 180, false, 1, -1, 0, 0.35),
        new("arrastre → 1200 px/s", 1200, 0, true, 1, -1, 0, 1.2),
        new("arrastre ↖ 1200 px/s · 225°", 1200, 225, true, 1, -1, 0, 1.2)
    ];

    /// <summary>
    /// Rinde las dos hojas del cuerpo pedido y devuelve las dos rutas, una por línea.
    /// </summary>
    /// <remarks>
    /// Dos rutas en un solo <c>string</c> porque quien llama las escribe con un <c>WriteLine</c> y
    /// no hay razón para tocarlo desde acá: la salida sigue siendo una ruta por línea, que es lo que
    /// se copia y se pega.
    /// </remarks>
    public static async Task<string> RunAsync(string outputDirectory, OrbShape shape = OrbShape.Gota)
    {
        Directory.CreateDirectory(outputDirectory);

        var statesPath = await RenderStatesAsync(outputDirectory, shape);
        var motionPath = await RenderMotionAsync(outputDirectory, shape);
        return $"{statesPath}{Environment.NewLine}{motionPath}";
    }

    /// <summary>
    /// La ventana de trabajo: fuera de pantalla, sin barra, sin foco y sin fondo.
    /// </summary>
    /// <remarks>
    /// Los dos cuerpos dibujan sobre <c>CompositionTarget.Rendering</c>, que sólo corre si hay una
    /// ventana viva. Por eso se abre una de verdad en vez de medir el control suelto.
    /// </remarks>
    private static Window OpenHost(OrbShape shape, out FrameworkElement orb, out IOrbBody body)
    {
        var host = new Window
        {
            Width = Cell,
            Height = Cell,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        if (shape == OrbShape.Nube)
        {
            var nube = new NubeOrb { Width = Cell, Height = Cell };
            orb = nube;
            body = nube;
        }
        else
        {
            var gota = new LiquidOrb { Width = Cell, Height = Cell };
            orb = gota;
            body = gota;
        }

        host.Content = orb;
        host.Show();
        return host;
    }

    private static BitmapSource Capture(Window host, FrameworkElement orb)
    {
        host.UpdateLayout();
        var target = new RenderTargetBitmap(Cell * Zoom, Cell * Zoom, Dpi, Dpi, PixelFormats.Pbgra32);
        target.Render(orb);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// La hoja del movimiento: el cuerpo con velocidad puesta, en varias direcciones y rapideces, y
    /// el golpe contra un borde.
    /// </summary>
    /// <remarks>
    /// El movimiento se reinyecta <b>en cada cuadro</b> y no una vez por celda. Tiene que ser así:
    /// la estela de la nube es un resorte por partícula que se integra con el tiempo, y una sola
    /// inyección se apagaría antes de la foto. Es también la razón de que cada celda espere de
    /// verdad en vez de dibujarse de un saque.
    /// </remarks>
    private static async Task<string> RenderMotionAsync(string outputDirectory, OrbShape shape)
    {
        var host = OpenHost(shape, out var orb, out var body);
        var shots = new List<(BitmapSource Image, string Caption)>();
        var pendingHitToken = 0;

        // El cierre cubre las DOS filas y no sólo la de la física. Vivía en el finally del segundo
        // try, así que una excepción en el bucle de las doce celdas dejaba la Window abierta:
        // medido inyectando un throw en la celda 3, Application.Current.Windows.Count daba 1 con el
        // cierre viejo y 0 con éste. El proceso terminaba igual —App.RenderOrbAndExitAsync llama a
        // Shutdown() en su propio finally—, así que esto no arregla un cuelgue: arregla que el
        // banco deje la ventana de otro cuerpo viva mientras rinde el siguiente.
        try
        {
            var sink = (IOrbMotionSink)body;
            var motion = default(OrbMotionSample);

            void Pump(object? sender, EventArgs e) => sink.ReportMotion(motion);

            CompositionTarget.Rendering += Pump;
            try
            {
                for (var index = 0; index < MotionFrames.Length; index++)
                {
                    var frame = MotionFrames[index];
                    body.State = AssistantVisualState.Idle;
                    body.IsLightDesktop = IsLightRow(index);

                    var radians = frame.Degrees * Math.PI / 180;
                    motion = new OrbMotionSample(
                        Math.Cos(radians) * frame.Speed,
                        Math.Sin(radians) * frame.Speed,
                        frame.Dragging,
                        frame.HitToken,
                        frame.HitNormalX,
                        frame.HitNormalY,

                        // A fondo: la hoja tiene que mostrar el golpe más fuerte que existe, no uno
                        // promedio. Los intermedios se leen interpolando; el tope, no.
                        1.0);

                    await Task.Delay(TimeSpan.FromSeconds(frame.Seconds));
                    shots.Add((Capture(host, orb), frame.Caption));
                }
            }
            finally
            {
                // Desengancharse antes de cerrar. Un manejador de Rendering que sobrevive a su
                // ventana se sigue llamando por cada cuadro de todo el proceso.
                CompositionTarget.Rendering -= Pump;
            }

            // El último token que vio el cuerpo se le pasa a la fila de la física para que ésta
            // siga contando desde ahí. Ver el remark de RunPhysicsAsync.
            pendingHitToken = motion.HitToken;
            shots.AddRange(await RunPhysicsAsync(host, orb, body, sink, shots.Count, pendingHitToken));
        }
        finally
        {
            host.Close();
        }

        // La quinta fila estrena un cuerpo y no reusa el de arriba, así que abre y cierra su propia
        // ventana. Va después del cierre y no antes: dos hosts vivos a la vez son dos cuerpos
        // dibujando sobre el mismo Rendering, y no hay nada que ganar con eso.
        shots.AddRange(await RunRebirthAsync(shape, shots.Count, pendingHitToken));

        var path = Path.Combine(outputDirectory, $"orb-{shape.ToString().ToLowerInvariant()}-movimiento.png");
        SaveContactSheet(shots, path);
        return path;
    }

    /// <summary>
    /// La fila del cuerpo recién nacido: qué hace con un golpe que ocurrió antes de que existiera.
    /// </summary>
    /// <remarks>
    /// Cambiar de gota a nube crea un cuerpo nuevo, pero el <see cref="Shell.OrbMotion"/> de la
    /// ventana es el mismo y sigue mandando el token del último choque. El cuerpo nuevo arrancaba
    /// con el suyo en 0, la comparación daba verdadero, y ejecutaba el golpe entero —ondas, patada
    /// de escala y todo el polvo empujado contra la normal— sin que nada hubiera chocado.
    /// <para>
    /// Las dos primeras celdas son la prueba de que ya no pasa: el cuerpo nace, le llega el token
    /// viejo y tiene que salir redondo, igual que la celda «quieto» de la primera fila. Las dos
    /// últimas son la otra mitad y sin ellas la primera no prueba nada —un cuerpo sordo también
    /// saldría redondo—: se sube el token una vez, que ahora sí es un golpe suyo, y tiene que
    /// acusarlo. Adoptar no es ignorar.
    /// </para>
    /// </remarks>
    private static async Task<List<(BitmapSource Image, string Caption)>> RunRebirthAsync(
        OrbShape shape,
        int firstIndex,
        int pendingHitToken)
    {
        var host = OpenHost(shape, out var orb, out var body);
        var shots = new List<(BitmapSource Image, string Caption)>();

        try
        {
            body.State = AssistantVisualState.Idle;
            body.IsLightDesktop = IsLightRow(firstIndex);

            var sink = (IOrbMotionSink)body;

            // Quieto y con el golpe a fondo contra el borde derecho: si algo se mueve en las dos
            // primeras celdas sale del token y de ningún otro lado.
            var motion = new OrbMotionSample(0, 0, false, pendingHitToken, -1, 0, 1.0);

            void Pump(object? sender, EventArgs e) => sink.ReportMotion(motion);

            CompositionTarget.Rendering += Pump;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(0.06));
                shots.Add((Capture(host, orb), $"nace con golpe {pendingHitToken} viejo · 0,06 s"));

                await Task.Delay(TimeSpan.FromSeconds(0.34));
                shots.Add((Capture(host, orb), "el mismo golpe viejo · 0,40 s"));

                motion = motion with { HitToken = pendingHitToken + 1 };

                await Task.Delay(TimeSpan.FromSeconds(0.06));
                shots.Add((Capture(host, orb), $"golpe {pendingHitToken + 1}, ya suyo · 0,06 s"));

                await Task.Delay(TimeSpan.FromSeconds(0.34));
                shots.Add((Capture(host, orb), "el golpe suyo · 0,40 s"));
            }
            finally
            {
                CompositionTarget.Rendering -= Pump;
            }
        }
        finally
        {
            host.Close();
        }

        return shots;
    }

    /// <summary>
    /// La última fila: el arrastre y el tiro con la física de verdad, no con velocidades a mano.
    /// </summary>
    /// <remarks>
    /// Las doce celdas anteriores prueban que el cuerpo <em>sabe</em> deformarse; ésta prueba que el
    /// cable está puesto. Acá nadie inventa una velocidad: se arrastra el orbe con el resorte
    /// 146/15,5 —el que nunca había corrido—, se lo suelta, vuela con su roce y choca contra un
    /// borde de verdad. Si el <c>Sample</c> no viajara, o el rebote no levantara el golpe, la fila
    /// saldría con cuatro orbes redondos y nadie tendría que discutir nada.
    /// <para>
    /// La pared se planta a 200 px de donde quedó el orbe al soltarlo y no en un número fijo: con el
    /// roce exponencial, la rapidez con la que llega depende sólo de esa distancia, así que fijarla
    /// es fijar la fuerza del golpe sin depender de cuánto haya avanzado el arrastre.
    /// </para>
    /// <para>
    /// El <see cref="OrbMotion"/> de acá es una fuente de tokens nueva y empieza en cero, pero el
    /// cuerpo llega de las doce celdas sintéticas con el suyo más alto. Por eso los tokens de esta
    /// fila se corren con <paramref name="hitTokenBase"/>: sin eso el primer <c>Sample</c> de la
    /// física traía un token distinto del último visto, el cuerpo lo leía como un golpe —de fuerza
    /// 0— y la fila arrancaba con un choque que nunca ocurrió. No movía las mediciones, pero era un
    /// evento inventado en la hoja que se presenta como prueba.
    /// </para>
    /// </remarks>
    /// <param name="hitTokenBase">El último token de golpe que se le mandó al cuerpo antes de esta fila.</param>
    private static async Task<List<(BitmapSource Image, string Caption)>> RunPhysicsAsync(
        Window host,
        FrameworkElement orb,
        IOrbBody body,
        IOrbMotionSink sink,
        int firstIndex,
        int hitTokenBase)
    {
        body.State = AssistantVisualState.Idle;

        // Las cuatro caen en la misma fila, así que una sola consulta alcanza. Si alguna vez son
        // más de cuatro hay que preguntarlo por celda: una hoja de contactos con el fondo de una
        // fila y el dibujo de la otra ya mintió una vez.
        body.IsLightDesktop = IsLightRow(firstIndex);

        var motion = new OrbMotion();
        var bounds = new Rect(20, 20, 4000, 420);
        motion.Teleport(new Point(bounds.Left, 200));

        var target = motion.Position;
        var dragging = true;
        var lastTicks = 0L;
        motion.BeginDrag();

        void Pump(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs rendering)
            {
                return;
            }

            var ticks = rendering.RenderingTime.Ticks;
            var delta = lastTicks == 0 ? 1.0 / 60 : (ticks - lastTicks) / (double)TimeSpan.TicksPerSecond;
            lastTicks = ticks;
            delta = Math.Clamp(delta, 0, 0.05);

            if (dragging)
            {
                // El dedo se va a la derecha a 1500 px/s. El resorte tiene que alcanzarlo y quedarse
                // 159 px atrás mientras lo hace, que es lo que da el retardo c·v/k.
                target = new Point(target.X + (1500 * delta), target.Y);
                motion.DragTo(target);
            }

            motion.Step(delta, bounds);

            // El corrimiento del token, no la velocidad: los px/s de esta fila son los de la física
            // y no se tocan. Ver el remark.
            var sample = motion.Sample;
            sink.ReportMotion(sample with { HitToken = sample.HitToken + hitTokenBase });
        }

        CompositionTarget.Rendering += Pump;
        var shots = new List<(BitmapSource Image, string Caption)>();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(0.9));
            shots.Add((Capture(host, orb), $"arrastre real · {motion.SmoothSpeed:0} px/s"));

            dragging = false;
            bounds = new Rect(bounds.Left, bounds.Top, motion.Position.X + 200 - bounds.Left, bounds.Height);
            motion.Drop();

            // Se espera el golpe de verdad y no un tiempo fijo: el orbe tarda lo que tarda en cruzar,
            // y una espera a ojo saca la foto antes o después del choque según la máquina.
            var waited = 0.0;
            while (motion.Sample.HitToken == 0 && waited < 4)
            {
                await Task.Delay(16);
                waited += 0.016;
            }

            var strength = motion.Sample.HitStrength;
            await Task.Delay(TimeSpan.FromSeconds(0.06));
            shots.Add((Capture(host, orb), $"golpe real · fuerza {strength:0.00} · 0,06 s"));

            await Task.Delay(TimeSpan.FromSeconds(0.34));
            shots.Add((Capture(host, orb), "el mismo golpe · 0,40 s"));

            await Task.Delay(TimeSpan.FromSeconds(1.6));
            shots.Add((Capture(host, orb), $"ya quieto · {motion.SmoothSpeed:0} px/s"));
        }
        finally
        {
            CompositionTarget.Rendering -= Pump;
        }

        return shots;
    }

    private static async Task<string> RenderStatesAsync(string outputDirectory, OrbShape shape)
    {
        var host = OpenHost(shape, out var orb, out var body);

        var shots = new List<(BitmapSource Image, string Caption)>();
        try
        {
            for (var index = 0; index < Frames.Length; index++)
            {
                var (state, seconds) = Frames[index];

                // Los dos cuerpos cambian de dibujo con el escritorio, así que los dos tienen que
                // enterarse de en qué fila caen. Se piden por la interfaz y no por el tipo concreto:
                // antes había una rama por cuerpo y agregar un tercero sería agregar una tercera.
                body.State = state;
                body.IsLightDesktop = IsLightRow(index);

                // El reloj de las animaciones corre en tiempo real, así que se espera de verdad.
                await Task.Delay(TimeSpan.FromSeconds(seconds));

                // El nombre de la tabla y no el del enum: la hoja se mira para decidir si dos
                // estados se distinguen, y para eso hace falta leer «un proyecto te espera», no
                // «ProjectWaiting».
                shots.Add((Capture(host, orb), $"{OrbPalette.For(state).Name} · {seconds:0.0}s"));
            }
        }
        finally
        {
            host.Close();
        }

        var sheetPath = Path.Combine(outputDirectory, $"orb-{shape.ToString().ToLowerInvariant()}.png");
        SaveContactSheet(shots, sheetPath);
        return sheetPath;
    }

    private static void SaveContactSheet(
        IReadOnlyList<(BitmapSource Image, string Caption)> shots,
        string path)
    {
        var rows = (int)Math.Ceiling(shots.Count / (double)Columns);
        var cell = Cell * Zoom;
        const int label = 28;
        var width = Columns * cell;
        var height = rows * (cell + label);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Dos bandas de fondo para ver la silueta sobre escritorio oscuro y sobre claro.
            for (var row = 0; row < rows; row++)
            {
                var background = row % 2 == 0
                    ? new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0xE7, 0xED, 0xF3));
                context.DrawRectangle(
                    background,
                    null,
                    new Rect(0, row * (cell + label), width, cell + label));
            }

            for (var index = 0; index < shots.Count; index++)
            {
                var column = index % Columns;
                var row = index / Columns;
                var x = column * cell;
                var y = row * (cell + label);
                context.DrawImage(shots[index].Image, new Rect(x, y, cell, cell));

                var onDark = row % 2 == 0;
                var text = new FormattedText(
                    shots[index].Caption,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"),
                    14,
                    onDark ? Brushes.White : Brushes.Black,
                    1.0);
                context.DrawText(text, new Point(x + 8, y + cell + 5));
            }
        }

        var sheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(sheet));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
