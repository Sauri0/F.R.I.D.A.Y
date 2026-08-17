using System.Diagnostics;
using Viernes.Core.Tools;

namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Ejecuta el puñado de acciones de PC que Viernes tiene permitidas. Todo lo que hace es abrir algo:
/// no borra, no cierra, no eleva, no cambia configuración y no ejecuta comandos arbitrarios.
/// </summary>
/// <remarks>
/// La lista de aplicaciones es una allowlist cerrada y escrita a mano. El destino que llega desde el
/// modelo <b>nunca</b> se usa como ruta ni como línea de comandos: sólo se busca en esta tabla, y si
/// no está, no pasa nada. Ese es el punto: el modelo elige de un menú, no escribe la orden.
/// </remarks>
public sealed partial class WindowsPcActionExecutor : IPcActionExecutor
{
    /// <summary>Páginas de Configuración permitidas, por su URI oficial <c>ms-settings:</c>.</summary>
    private static readonly Dictionary<string, (string Uri, string Label)> SettingsPages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sonido"] = ("ms-settings:sound", "Sonido"),
            ["audio"] = ("ms-settings:sound", "Sonido"),
            ["pantalla"] = ("ms-settings:display", "Pantalla"),
            ["bluetooth"] = ("ms-settings:bluetooth", "Bluetooth"),
            ["red"] = ("ms-settings:network", "Red e internet"),
            ["wifi"] = ("ms-settings:network-wifi", "Wi-Fi"),
            ["bateria"] = ("ms-settings:batterysaver", "Batería"),
            ["batería"] = ("ms-settings:batterysaver", "Batería"),
            ["micrófono"] = ("ms-settings:privacy-microphone", "Privacidad del micrófono"),
            ["microfono"] = ("ms-settings:privacy-microphone", "Privacidad del micrófono"),
            ["privacidad"] = ("ms-settings:privacy", "Privacidad"),
            ["aplicaciones"] = ("ms-settings:appsfeatures", "Aplicaciones instaladas"),
            ["notificaciones"] = ("ms-settings:notifications", "Notificaciones"),
            ["inicio"] = ("ms-settings:startupapps", "Aplicaciones de inicio")
        };

    /// <summary>
    /// Aplicaciones permitidas. Se abren por nombre de ejecutable resuelto por Windows, no por ruta
    /// suministrada desde afuera.
    /// </summary>
    private static readonly Dictionary<string, (string Command, string Label)> Applications =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["calculadora"] = ("calc.exe", "Calculadora"),
            ["bloc de notas"] = ("notepad.exe", "Bloc de notas"),
            ["notepad"] = ("notepad.exe", "Bloc de notas"),
            ["explorador"] = ("explorer.exe", "Explorador de archivos"),
            ["archivos"] = ("explorer.exe", "Explorador de archivos"),
            ["terminal"] = ("wt.exe", "Terminal"),
            ["configuración"] = ("ms-settings:", "Configuración"),
            ["configuracion"] = ("ms-settings:", "Configuración")
        };

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "open_settings",
        "open_application",
        "show_desktop",
        "media_control",
        "volume",
        "focus_application",
        "close_application",
        "search_web",
        "play_music",
        "minimize_application",
        "restore_application",
        "read_controls",
        "click_control",
        "set_text",
        "undo",
        "what_did_you_do",
        "see_screen",
        "move_cursor",
        "click",
        "double_click",
        "right_click",
        "type_text",
        "press_key",
        "scroll",
        "lock_screen"
    };

    /// <summary>Teclas sueltas que se piden por nombre en una charla.</summary>
    private static readonly Dictionary<string, byte> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["intro"] = 0x0D, ["tab"] = 0x09, ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["espacio"] = 0x20, ["space"] = 0x20, ["backspace"] = 0x08, ["borrar"] = 0x08,
        ["suprimir"] = 0x2E, ["delete"] = 0x2E, ["arriba"] = 0x26, ["abajo"] = 0x28,
        ["izquierda"] = 0x25, ["derecha"] = 0x27, ["inicio"] = 0x24, ["fin"] = 0x23,
        ["pagearriba"] = 0x21, ["pageabajo"] = 0x22, ["f5"] = 0x74, ["f11"] = 0x7A
    };

    /// <summary>Teclas multimedia. Windows las rutea a la aplicación que esté reproduciendo.</summary>
    private static readonly Dictionary<string, (byte Key, string Label)> MediaKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["play"] = (0xB3, "Reproducir/pausar"),
            ["pause"] = (0xB3, "Reproducir/pausar"),
            ["play_pause"] = (0xB3, "Reproducir/pausar"),
            ["pausa"] = (0xB3, "Reproducir/pausar"),
            ["next"] = (0xB0, "Siguiente"),
            ["siguiente"] = (0xB0, "Siguiente"),
            ["previous"] = (0xB1, "Anterior"),
            ["anterior"] = (0xB1, "Anterior"),
            ["stop"] = (0xB2, "Detener"),
            ["parar"] = (0xB2, "Detener")
        };

    private static readonly Dictionary<string, (byte Key, int Repeat, string Label)> VolumeKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = (0xAF, 4, "Subí el volumen"),
            ["subir"] = (0xAF, 4, "Subí el volumen"),
            ["down"] = (0xAE, 4, "Bajé el volumen"),
            ["bajar"] = (0xAE, 4, "Bajé el volumen"),
            ["mute"] = (0xAD, 1, "Silencié el audio"),
            ["silenciar"] = (0xAD, 1, "Silencié el audio")
        };

    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Pedidos de música sin nombre: no hay nada que buscar, hay que abrir y sonar.</summary>
    private static readonly HashSet<string> GenericMusicRequests = new(StringComparer.Ordinal)
    {
        "spotify", "musica", "algo", "algo de musica", "cualquier cosa", "lo que sea", "play"
    };

    // DllImport y no LibraryImport: el generador de LibraryImport emite código no seguro, y
    // habilitar unsafe en todo el ensamblado para cuatro llamadas sin punteros no se paga.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool attach);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    /// <summary>
    /// Espera hasta <paramref name="timeout"/> a que se cumpla una condición sobre las ventanas.
    /// </summary>
    /// <remarks>
    /// Es la mitad que faltaba: hasta ahora, «cerrá el bloc de notas» contestaba «le pedí que se
    /// cierre» y ahí terminaba. Que el pedido saliera bien no dice nada sobre si la ventana se fue
    /// —una aplicación puede preguntar si querés guardar, o directamente ignorar el mensaje—. Sin
    /// comprobar el efecto, el asistente informa intenciones y las hace pasar por hechos, que es la
    /// forma más rápida de que dejes de creerle.
    /// </remarks>
    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return condition();
    }

    /// <summary>
    /// Como <see cref="WaitFor"/>, pero exige que la condición siga siendo cierta un rato después.
    /// </summary>
    /// <remarks>
    /// Una aplicación empaquetada crea su ventana marco antes de que arranque el proceso, y esa
    /// ventana parpadea: aparece y se va. Con una sola comprobación, el verificador daba «Abrí
    /// Calculadora» a los 62 ms y cuatro milisegundos después no encontraba ninguna ventana. Un
    /// destello no es haber abierto algo; exigir que persista es lo que distingue el arranque real
    /// del parpadeo.
    /// </remarks>
    private static bool WaitForStable(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                Thread.Sleep(600);
                if (condition())
                {
                    return true;
                }
            }

            Thread.Sleep(150);
        }

        return false;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int count);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private const uint WindowMessageClose = 0x0010;

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, nuint extraInfo);

    /// <summary>
    /// Misma función que <c>keybd_event</c>, declarada aparte para escribir Unicode: con el indicador
    /// correspondiente, el segundo argumento deja de ser un código de rastreo y pasa a ser el
    /// carácter. Es lo que hace que «ñ» y las tildes salgan sin depender del teclado configurado.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void keybd_eventUnicode(byte virtualKey, ushort character, uint flags, nuint extraInfo);

    private readonly InstalledApplications _installed = new();

    public WindowsPcActionExecutor() => _installed.Warm();

    public IReadOnlySet<string> SupportedActions { get; } = Supported;

    /// <summary>Nombres instalados, para que la herramienta le diga al modelo qué puede abrir.</summary>
    public IReadOnlyCollection<string> InstalledApplicationNames => _installed.Names;

    private readonly ActionJournal _journal = new();

    public Task<PcActionOutcome> ExecuteAsync(
        string action,
        string? target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = action.ToLowerInvariant();

        // Deshacer y contar no se anotan: uno consume el diario y el otro sólo lo lee. Anotarlos
        // haría que «deshacé» pudiera deshacerse a sí mismo, que no significa nada.
        if (normalized is "undo")
        {
            return Task.FromResult(Undo());
        }

        if (normalized is "what_did_you_do")
        {
            return Task.FromResult(Recount());
        }

        var outcome = Perform(normalized, target);
        if (outcome.Executed)
        {
            _journal.Record(new JournalEntry(normalized, target, outcome.Message, InverseOf(normalized, target)));
        }

        return Task.FromResult(outcome);
    }

    /// <summary>
    /// Qué acción revierte a cuál. Lo que no aparece acá no tiene vuelta atrás, y eso es información
    /// tan útil como el resto: al deshacer, Viernes dice qué no puede desarmar en vez de fingir.
    /// </summary>
    private static (string Action, string? Target)? InverseOf(string action, string? target) => action switch
    {
        "open_application" => ("close_application", target),
        "close_application" => ("open_application", target),
        "minimize_application" => ("restore_application", target),
        "restore_application" => ("minimize_application", target),
        "volume" when target is not null && target.Contains("up", StringComparison.OrdinalIgnoreCase)
            => ("volume", "down"),
        "volume" when target is not null && target.Contains("subir", StringComparison.OrdinalIgnoreCase)
            => ("volume", "down"),
        "volume" when target is not null && target.Contains("down", StringComparison.OrdinalIgnoreCase)
            => ("volume", "up"),
        "volume" when target is not null && target.Contains("bajar", StringComparison.OrdinalIgnoreCase)
            => ("volume", "up"),
        "volume" when target is not null && target.Contains("mute", StringComparison.OrdinalIgnoreCase)
            => ("volume", "mute"),
        "media_control" when target is not null && target.StartsWith("pau", StringComparison.OrdinalIgnoreCase)
            => ("media_control", "play"),
        "media_control" when target is not null && target.StartsWith("play", StringComparison.OrdinalIgnoreCase)
            => ("media_control", "pause"),
        "media_control" when target is not null && target.StartsWith("next", StringComparison.OrdinalIgnoreCase)
            => ("media_control", "previous"),
        "media_control" when target is not null && target.StartsWith("sig", StringComparison.OrdinalIgnoreCase)
            => ("media_control", "previous"),
        "show_desktop" => ("show_desktop", null),
        _ => null
    };

    private PcActionOutcome Undo()
    {
        var entry = _journal.TakeLastReversible();
        if (entry is null)
        {
            return new PcActionOutcome(false, "No hay nada que pueda deshacer.");
        }

        var (action, target) = entry.Inverse!.Value;
        var result = Perform(action, target);
        return result.Executed
            ? new PcActionOutcome(true, $"Deshice: {entry.Description}")
            : new PcActionOutcome(false, $"No pude deshacer «{entry.Description}»: {result.Message}");
    }

    private PcActionOutcome Recount()
    {
        var recent = _journal.Recent(8);
        if (recent.Count == 0)
        {
            return new PcActionOutcome(true, "No hice nada todavía en esta sesión.");
        }

        var lines = recent.Select(entry =>
            $"- {entry.Description}{(entry.Inverse is null ? " (no se puede deshacer)" : string.Empty)}");
        return new PcActionOutcome(
            true,
            $"Lo último que hice:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    private PcActionOutcome Perform(string action, string? target)
    {
        return action switch
        {
            "open_settings" => OpenSettings(target),
            "open_application" => OpenApplication(target),
            "show_desktop" => ShowDesktop(),
            "media_control" => MediaControl(target),
            "volume" => Volume(target),
            "focus_application" => FocusApplication(target),
            "close_application" => CloseApplication(target),
            "search_web" => SearchWeb(target),
            "play_music" => PlayMusic(target),
            "minimize_application" => ChangeWindowState(target, ShowMinimized, "Minimicé"),
            "restore_application" => ChangeWindowState(target, ShowRestore, "Restauré"),
            "read_controls" => UiAutomationActions.ReadControls(target),
            "click_control" => UiAutomationActions.ClickControl(target),
            "set_text" => UiAutomationActions.SetText(target),
            "see_screen" => SeeScreen(target),
            "move_cursor" => MoveCursor(target),
            "click" => ClickAt(target, MouseLeftDown, MouseLeftUp, 1, "Hice clic"),
            "double_click" => ClickAt(target, MouseLeftDown, MouseLeftUp, 2, "Hice doble clic"),
            "right_click" => ClickAt(target, MouseRightDown, MouseRightUp, 1, "Hice clic derecho"),
            "type_text" => TypeText(target),
            "press_key" => PressNamedKey(target),
            "scroll" => Scroll(target),
            "lock_screen" => LockScreen(),
            _ => new PcActionOutcome(false, "Esa acción no está habilitada.")
        };
    }

    /// <summary>
    /// Teclas multimedia: Windows las entrega a la aplicación que esté reproduciendo, sea Spotify,
    /// YouTube o lo que fuere. Controla el reproductor sin tener que conocerlo.
    /// </summary>
    private static PcActionOutcome MediaControl(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !MediaKeys.TryGetValue(target.Trim(), out var key))
        {
            return new PcActionOutcome(false, "Puedo reproducir, pausar, pasar a la siguiente o volver a la anterior.");
        }

        PressKey(key.Key);
        return new PcActionOutcome(true, $"{key.Label}.");
    }

    private static PcActionOutcome Volume(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !VolumeKeys.TryGetValue(target.Trim(), out var key))
        {
            return new PcActionOutcome(false, "Puedo subir, bajar o silenciar el volumen.");
        }

        for (var press = 0; press < key.Repeat; press++)
        {
            PressKey(key.Key);
        }

        return new PcActionOutcome(true, $"{key.Label}.");
    }

    private static void PressKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, 0);
        keybd_event(virtualKey, 0, KeyEventKeyUp, 0);
    }

    private const int ShowMinimized = 6;
    private const int ShowRestore = 9;

    /// <summary>
    /// Minimiza o restaura <b>una</b> ventana.
    /// </summary>
    /// <remarks>
    /// Sin esto, «minimizá esto» sólo tenía show_desktop para ofrecer, que minimiza todo. El modelo
    /// no se equivocó: hizo lo único que existía. La acción faltaba.
    /// </remarks>
    private static PcActionOutcome ChangeWindowState(string? target, int command, string verb)
    {
        var (window, title) = FindWindow(target);
        if (window == nint.Zero)
        {
            return new PcActionOutcome(false, $"No encontré ninguna ventana abierta de «{target}».");
        }

        ShowWindow(window, command);
        var wantMinimized = command == ShowMinimized;
        return WaitFor(() => IsIconic(window) == wantMinimized, TimeSpan.FromSeconds(2))
            ? new PcActionOutcome(true, $"{verb} {title}.")
            : new PcActionOutcome(false, $"{title} no cambió de estado.");
    }

    /// <summary>Trae al frente una ventana que ya está abierta, en vez de abrir otra instancia.</summary>
    private static PcActionOutcome FocusApplication(string? target)
    {
        var (window, title) = FindWindow(target);
        if (window == nint.Zero)
        {
            return new PcActionOutcome(false, $"No encontré ninguna ventana abierta de «{target}».");
        }

        ShowWindow(window, ShowRestore);
        ForceForeground(window);

        return WaitFor(() => GetForegroundWindow() == window, TimeSpan.FromSeconds(2))
            ? new PcActionOutcome(true, $"Traje {title} al frente.")
            : new PcActionOutcome(
                false,
                $"{title} no pasó al frente; Windows bloqueó el cambio de foco. Está parpadeando en la barra.");
    }

    /// <summary>
    /// Trae una ventana al frente sorteando el bloqueo de foco de Windows.
    /// </summary>
    /// <remarks>
    /// <c>SetForegroundWindow</c> a secas no alcanza: Windows sólo se lo permite al proceso que ya
    /// está adelante, para que ninguna aplicación te robe el teclado mientras escribís. Desde un
    /// asistente en segundo plano la llamada devuelve éxito y la ventana apenas parpadea en la barra
    /// —que era justamente lo que estaba pasando, con la acción informando que la había traído—.
    /// <para>
    /// El camino aceptado es engancharse temporalmente a la cola de entrada del hilo que tiene el
    /// foco: durante ese rato Windows considera que somos el mismo hilo y deja hacer el cambio. Se
    /// desengancha enseguida, porque compartir la cola de entrada con otro proceso más tiempo del
    /// necesario acopla su suerte a la nuestra.
    /// </para>
    /// </remarks>
    private static void ForceForeground(nint window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            SetForegroundWindow(window);
            return;
        }

        AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
            BringWindowToTop(window);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// Cierra pidiendo, no matando: <c>WM_CLOSE</c> es el equivalente a apretar la X, así que la
    /// aplicación puede preguntar si querés guardar. Nunca se mata el proceso, que perdería trabajo.
    /// </summary>
    private static PcActionOutcome CloseApplication(string? target)
    {
        var (window, title) = FindWindow(target);
        if (window == nint.Zero)
        {
            return new PcActionOutcome(false, $"No encontré ninguna ventana abierta de «{target}».");
        }

        if (!PostMessage(window, WindowMessageClose, nint.Zero, nint.Zero))
        {
            return new PcActionOutcome(false, $"{title} no aceptó el pedido de cierre.");
        }

        // Se comprueba el efecto, no el envío: una aplicación puede preguntar si querés guardar y
        // quedarse abierta. Decir «la cerré» sin mirar sería inventar.
        // Cinco segundos y no tres: una ventana que viene de restaurarse está animándose, y el
        // pedido de cierre queda encolado detrás. Con el plazo corto, deshacer un «abrí» reportaba
        // fallo por impaciencia y no porque la aplicación se hubiera negado.
        return WaitFor(() => FindWindow(target).Window == nint.Zero, TimeSpan.FromSeconds(5))
            ? new PcActionOutcome(true, $"Cerré {title}.")
            : new PcActionOutcome(
                false,
                $"Le pedí a {title} que se cierre pero sigue abierta; puede estar preguntándote algo.");
    }

    /// <summary>
    /// Busca por ventana y por título, no por proceso.
    /// </summary>
    /// <remarks>
    /// Una aplicación de la Store no tiene ventana propia: la aloja <c>ApplicationFrameHost</c>, que
    /// es el mismo proceso para todas. Con la calculadora abierta, <c>CalculatorApp</c> reporta
    /// ventana 0 y título vacío, mientras el título real —«Calculadora»— vive en el host. Buscar por
    /// proceso no podía encontrarla, y actuar sobre el host habría afectado a las demás aplicaciones
    /// empaquetadas. El título además está en el idioma de Windows, que es justamente como lo nombra
    /// el usuario en voz alta.
    /// </remarks>
    private static (nint Window, string Title) FindWindow(string? target)
    {
        var matches = FindWindowsWithTitles(target);
        if (matches.Count == 0)
        {
            return (nint.Zero, string.Empty);
        }

        // El título más corto que contenga lo pedido: «Spotify» antes que «Spotify — Explorar».
        var best = matches.MinBy(match => match.Title.Length);
        return (best.Window, best.Title);
    }

    private static List<nint> FindWindows(string? target) =>
        FindWindowsWithTitles(target).Select(match => match.Window).ToList();

    private static List<(nint Window, string Title)> FindWindowsWithTitles(string? target)
    {
        var matches = new List<(nint, string)>();
        if (string.IsNullOrWhiteSpace(target))
        {
            return matches;
        }

        var needle = Simplify(target);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new System.Text.StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            var title = buffer.ToString();
            if (Simplify(title).Contains(needle, StringComparison.Ordinal))
            {
                matches.Add((window, title));
            }

            return true;
        }, nint.Zero);

        return matches;
    }

    /// <summary>Minúsculas sin acentos: «Calculadora» tiene que coincidir con «calculadora».</summary>
    private static string Simplify(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Abre el navegador con una búsqueda. Es una consulta codificada sobre un buscador fijo, no una
    /// URL libre: el texto que llega del modelo no puede convertirse en una dirección arbitraria.
    /// </summary>
    private static PcActionOutcome SearchWeb(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué buscar.");
        }

        var query = Uri.EscapeDataString(target.Trim());
        return Launch($"https://www.google.com/search?q={query}", $"Busqué «{target.Trim()}» en el navegador.");
    }

    /// <summary>
    /// Pide una canción por nombre dentro de Spotify, en vez de buscarla en la web.
    /// </summary>
    /// <remarks>
    /// Spotify entiende <c>spotify:search:…</c>, así que «poné Creep de Radiohead» abre la aplicación
    /// parada sobre esa búsqueda. Es hasta donde se llega sin credenciales de Spotify: para que
    /// además le dé play a la primera pista haría falta su API —con su OAuth y su client id— o
    /// automatizar clics sobre la interfaz, que se rompe con cada rediseño. Prefiero dejar la mano
    /// a un clic de distancia y decirlo, antes que fingir que sonó.
    /// </remarks>
    private PcActionOutcome PlayMusic(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            // Sin nombre no hay nada que buscar: se retoma lo que hubiera sonando.
            return MediaControl("play");
        }

        var query = target.Trim();
        if (_installed.Resolve("spotify") is null)
        {
            return new PcActionOutcome(false, "No tenés Spotify instalado, así que no puedo poner música ahí.");
        }

        // «Poné música» a secas llega como target genérico —«spotify», «música»— y buscar esa
        // palabra adentro de Spotify no es lo que nadie pidió: se abre y se retoma lo que había.
        if (GenericMusicRequests.Contains(Simplify(query)))
        {
            var opened = OpenApplication("spotify");
            return opened.Executed
                ? new PcActionOutcome(true, "Puse Spotify.")
                : opened;
        }

        // El mensaje dice que NO está sonando, en la primera frase. Antes empezaba con «Abrí Spotify
        // buscando…» y el modelo lo leía como que la música ya estaba puesta: contestaba «listo, ahí
        // va» sobre un buscador abierto en silencio.
        return Launch(
            $"spotify:search:{Uri.EscapeDataString(query)}",
            $"NO puse la música: sólo abrí Spotify con la búsqueda de «{query}». " +
            "Si tenés herramientas de Spotify, usá ésas para reproducirla de verdad; " +
            "si no, decile al usuario que le dé play él.");
    }

    /// <summary>
    /// Mira la pantalla. El resultado no es texto: es la imagen que después el modelo interpreta.
    /// </summary>
    private static PcActionOutcome SeeScreen(string? target)
    {
        var activeOnly = target is not null &&
            (target.Contains("ventana", StringComparison.OrdinalIgnoreCase) ||
             target.Contains("activ", StringComparison.OrdinalIgnoreCase) ||
             target.Contains("window", StringComparison.OrdinalIgnoreCase));

        var image = ScreenCapture.CaptureAsDataUrl(activeOnly);
        if (image is null)
        {
            return new PcActionOutcome(false, "Windows no dejó capturar la pantalla.");
        }

        var (width, height) = ScreenCapture.LastImageSize;
        return new PcActionOutcome(
            true,
            $"{(activeOnly ? "Miré la ventana activa" : "Miré la pantalla")}. " +
            $"La imagen mide {width}x{height}: dame las coordenadas leídas sobre ella, no sobre tu " +
            "idea de la resolución de la pantalla.",
            image);
    }

    /// <summary>
    /// Coordenadas absolutas en píxeles, como «820,430». El modelo las saca de la captura, que es
    /// la única forma de que apuntar a algo tenga sentido: sin ver, un número es una adivinanza.
    /// </summary>
    private static bool TryParsePoint(string? target, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var parts = target.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y))
        {
            return false;
        }

        // Las coordenadas vienen leídas de la captura, que va reducida. Se traducen a pantalla real
        // acá; pedirle al modelo que multiplique sería pedirle que adivine una resolución que no ve.
        x = ScreenCapture.LastOrigin.X + (int)Math.Round(x * ScreenCapture.LastScale);
        y = ScreenCapture.LastOrigin.Y + (int)Math.Round(y * ScreenCapture.LastScale);
        return true;
    }

    private static PcActionOutcome MoveCursor(string? target)
    {
        if (!TryParsePoint(target, out var x, out var y))
        {
            return new PcActionOutcome(false, "Necesito las coordenadas como «x,y».");
        }

        SetCursorPos(x, y);
        return new PcActionOutcome(true, $"Moví el cursor a {x}, {y}.");
    }

    private static PcActionOutcome ClickAt(string? target, uint down, uint up, int times, string verb)
    {
        // Sin coordenadas se hace clic donde ya está el cursor: «hacé clic» después de moverlo.
        if (TryParsePoint(target, out var x, out var y))
        {
            SetCursorPos(x, y);
        }

        for (var press = 0; press < times; press++)
        {
            mouse_event(down, 0, 0, 0, nuint.Zero);
            mouse_event(up, 0, 0, 0, nuint.Zero);
        }

        return new PcActionOutcome(true, $"{verb}.");
    }

    /// <summary>
    /// Escribe texto como si lo tipearas. Va por unidades de UTF-16 y no por códigos de tecla, así
    /// que las tildes y la ñ salen bien sin depender de la distribución del teclado.
    /// </summary>
    private static PcActionOutcome TypeText(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué escribir.");
        }

        foreach (var character in target)
        {
            const uint UnicodeKey = 0x0004;
            keybd_eventUnicode(0, character, UnicodeKey, 0);
            keybd_eventUnicode(0, character, UnicodeKey | KeyEventKeyUp, 0);
        }

        return new PcActionOutcome(true, $"Escribí «{target}».");
    }

    private static PcActionOutcome PressNamedKey(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !NamedKeys.TryGetValue(target.Trim(), out var key))
        {
            return new PcActionOutcome(false, $"No conozco la tecla «{target}».");
        }

        PressKey(key);
        return new PcActionOutcome(true, $"Apreté {target.Trim()}.");
    }

    private static PcActionOutcome Scroll(string? target)
    {
        var down = target is null || target.Contains("abajo", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("down", StringComparison.OrdinalIgnoreCase);
        const uint MouseWheel = 0x0800;
        mouse_event(MouseWheel, 0, 0, down ? -360 : 360, nuint.Zero);
        return new PcActionOutcome(true, down ? "Bajé la vista." : "Subí la vista.");
    }

    private static PcActionOutcome LockScreen() => LockWorkStation()
        ? new PcActionOutcome(true, "Bloqueé la sesión.")
        : new PcActionOutcome(false, "Windows no dejó bloquear la sesión.");

    private static PcActionOutcome OpenSettings(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Launch("ms-settings:", "Abrí Configuración.");
        }

        if (!SettingsPages.TryGetValue(target.Trim(), out var page))
        {
            var available = string.Join(", ", SettingsPages.Values.Select(item => item.Label).Distinct());
            return new PcActionOutcome(
                false,
                $"No tengo habilitada esa página de Configuración. Puedo abrir: {available}.");
        }

        return Launch(page.Uri, $"Abrí Configuración en {page.Label}.");
    }

    private PcActionOutcome OpenApplication(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué aplicación abrir.");
        }

        var name = target.Trim();

        // Primero el puñado escrito a mano, que cubre las de sistema sin acceso directo.
        //
        // Esta rama devolvía directamente, sin pasar por la verificación, y por eso seguía diciendo
        // «Abrí Calculadora» en cincuenta milisegundos con la aplicación sin arrancar. Verificar una
        // de las dos rutas y no la otra es no verificar: el usuario no sabe cuál se usó.
        if (Applications.TryGetValue(name, out var known))
        {
            var beforeKnown = FindWindows(known.Label).ToHashSet();
            var startedKnown = Launch(known.Command, $"Abrí {known.Label}.");
            return startedKnown.Executed
                ? VerifyWindowAppeared(known.Label, known.Label, beforeKnown)
                : startedKnown;
        }

        // Después, cualquier cosa instalada. El modelo elige del catálogo real; nunca compone una
        // ruta, así que un texto leído de la web no se vuelve un ejecutable.
        var resolved = _installed.Resolve(name);

        // Si no está en el catálogo pero es una ruta o un ejecutable, se abre igual.
        //
        // Antes esto se negaba, y era una negativa mal fundada: el catálogo cubre lo que aparece en
        // el menú Inicio, y hay muchísimo que no aparece ahí —un .exe portable, un documento, una
        // carpeta, una dirección web—. Negarse porque no está en una lista no protege de nada: lo
        // que protege es de dónde viene el pedido, no si el nombre figuraba.
        if (resolved is null)
        {
            var direct = Environment.ExpandEnvironmentVariables(name);
            var looksOpenable = System.IO.File.Exists(direct) ||
                System.IO.Directory.Exists(direct) ||
                direct.Contains("://", StringComparison.Ordinal) ||
                direct.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            if (looksOpenable)
            {
                var directLabel = System.IO.Path.GetFileNameWithoutExtension(direct);
                var beforeDirect = FindWindows(directLabel).ToHashSet();
                var started = Launch(direct, $"Abrí {direct}.");
                return started.Executed && System.IO.File.Exists(direct)
                    ? VerifyWindowAppeared(directLabel, direct, beforeDirect)
                    : started;
            }

            return new PcActionOutcome(false, $"No encontré ninguna aplicación instalada que se llame «{name}».");
        }

        // Lo que es un archivo se arranca; todo lo demás es un identificador de aplicación y se lanza
        // por el explorador, que es exactamente como lo abre el menú Inicio.
        // Se anota qué ventanas coincidían ANTES de lanzar nada: sin esa foto previa no hay forma de
        // distinguir «se abrió» de «ya estaba».
        var before = FindWindows(name).ToHashSet();
        var label = InstalledApplications.IsLaunchableFile(resolved)
            ? System.IO.Path.GetFileNameWithoutExtension(resolved)
            : name;
        var launched = InstalledApplications.IsLaunchableFile(resolved)
            ? Launch(resolved, $"Abrí {label}.")
            : LaunchPackaged(resolved, name);

        return launched.Executed
            ? VerifyWindowAppeared(name, label, before)
            : launched;
    }

    /// <summary>
    /// Confirma que apareció una ventana que antes no estaba, y que sigue ahí.
    /// </summary>
    /// <remarks>
    /// Arrancar el proceso no es abrir la aplicación: puede morir, pedir permisos o tardar. Se exige
    /// una ventana <em>nueva</em> —la primera versión se dejó engañar por la ventana de la terminal,
    /// cuyo título contenía la palabra buscada— y que <em>persista</em>, porque una aplicación
    /// empaquetada crea su marco antes de arrancar y ese marco parpadea. Comparar el antes con el
    /// después, y esperar a que se estabilice, es la verificación entera.
    /// </remarks>
    private static PcActionOutcome VerifyWindowAppeared(string needle, string label, HashSet<nint> before) =>
        WaitForStable(() => FindWindows(needle).Except(before).Any(), TimeSpan.FromSeconds(10))
            ? new PcActionOutcome(true, $"Abrí {label}.")
            : new PcActionOutcome(
                false,
                $"Lancé {label} pero no llegué a ver su ventana. Puede estar tardando en cargar.");

    private static PcActionOutcome LaunchPackaged(string applicationId, string spokenName)
    {
        // El identificador sale del catálogo de Windows, no del modelo, pero se valida igual antes
        // de convertirlo en un argumento: una comprobación barata que cierra la puerta a que un
        // nombre raro se transforme en otra cosa al pasar por la línea de comandos.
        if (applicationId.Any(character =>
                character is '"' or '\'' or '&' or '|' or '<' or '>' or '^' || char.IsControl(character)))
        {
            return new PcActionOutcome(false, $"El identificador de «{spokenName}» no es utilizable.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{applicationId}",
                UseShellExecute = true
            });
            return new PcActionOutcome(true, $"Abrí {spokenName}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PcActionOutcome(false, $"Windows no dejó abrir {spokenName}.");
        }
    }

    /// <summary>Mostrar el escritorio se hace por el Shell de Windows, sin simular teclas.</summary>
    private static PcActionOutcome ShowDesktop()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return new PcActionOutcome(false, "Windows no expuso el Shell para minimizar todo.");
            }

            var shell = Activator.CreateInstance(shellType);
            shellType.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            return new PcActionOutcome(true, "Mostré el escritorio.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PcActionOutcome(false, "No pude mostrar el escritorio.");
        }
    }

    private static PcActionOutcome Launch(string command, string successMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = true
            });
            return new PcActionOutcome(true, successMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PcActionOutcome(false, "Windows no dejó abrir eso.");
        }
    }
}
