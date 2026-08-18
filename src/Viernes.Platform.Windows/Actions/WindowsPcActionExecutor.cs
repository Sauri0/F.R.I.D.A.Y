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
    private static extern uint SendInput(uint count, Input[] inputs, int size);

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

    private const uint MouseWheel = 0x0800;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    /// <summary>Sube de un control a la ventana de nivel superior que lo contiene.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    private const uint AncestorRoot = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder text, int count);

    /// <summary>
    /// Pregunta si el compositor tiene la ventana escondida.
    /// </summary>
    /// <remarks>
    /// Las aplicaciones de la Store dejan ventanas fantasma: visibles para <c>IsWindowVisible</c>,
    /// con título, y sin embargo no dibujadas en ningún lado. Sin esta comprobación, «la ventana de
    /// adelante» podía resolver a una aplicación cerrada hace media hora.
    /// </remarks>
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    private const int DwmAttributeCloaked = 14;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventUnicode = 0x0004;

    /// <summary>Un evento de entrada sintética, tal como lo espera <c>SendInput</c>.</summary>
    /// <remarks>
    /// Se reemplazó a <c>keybd_event</c>, y no por gusto. La declaración anterior tipaba el segundo
    /// argumento como <c>ushort</c> cuando el nativo es <c>BYTE bScan</c>: al escribir con el
    /// indicador Unicode, todo carácter por encima de U+00FF se truncaba al byte bajo y salía otra
    /// cosa —la raya larga U+2014 se convertía en el carácter de control 0x14—. <c>SendInput</c>
    /// además devuelve cuántos eventos entraron, así que un teclado bloqueado por otra aplicación se
    /// puede informar en vez de darlo por hecho.
    /// </remarks>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    /// <summary>La parte variable de <see cref="Input"/>: teclado o mouse, nunca las dos.</summary>
    /// <remarks>
    /// Las dos variantes se declaran superpuestas aunque sólo se lea una, porque el tamaño que
    /// <c>SendInput</c> exige es el de la unión completa: si faltara la del mouse, la estructura
    /// mediría menos de lo que el sistema espera y la llamada fallaría entera.
    /// </remarks>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct InputData
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public MouseInputData Mouse;

        [System.Runtime.InteropServices.FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Character;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>
    /// Teclas que el teclado extendido duplica y que hay que marcar como tales.
    /// </summary>
    /// <remarks>
    /// Sin el indicador, varias aplicaciones leen «Suprimir» como el punto del teclado numérico y las
    /// flechas como sus gemelas numéricas. Es el mismo código virtual para las dos teclas físicas: lo
    /// único que las distingue es este bit.
    /// </remarks>
    private static readonly HashSet<byte> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3
    ];

    private static Input KeyboardEvent(ushort virtualKey, ushort character, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputData
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Character = character,
                Flags = flags,
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    private static Input MouseEvent(uint flags, int data) => new()
    {
        Type = InputMouse,
        Data = new InputData
        {
            Mouse = new MouseInputData
            {
                X = 0,
                Y = 0,
                // La rueda hacia abajo es un delta negativo, y el campo nativo es sin signo: la
                // conversión es la que el propio Windows hace del otro lado.
                Data = unchecked((uint)data),
                Flags = flags,
                Time = 0,
                ExtraInfo = nint.Zero
            }
        }
    };

    /// <summary>Manda eventos de entrada y dice si el sistema los aceptó todos.</summary>
    private static bool Send(params Input[] inputs) =>
        SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<Input>()) == (uint)inputs.Length;

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
        var entry = _journal.PeekLastReversible();
        if (entry is null)
        {
            return new PcActionOutcome(false, "No hay nada que pueda deshacer.");
        }

        var (action, target) = entry.Inverse!.Value;
        var result = Perform(action, target);
        if (!result.Executed)
        {
            // La entrada se queda en el diario a propósito: que la inversa haya fallado una vez no
            // significa que vaya a fallar siempre —la aplicación puede estar preguntando si querés
            // guardar—, y borrarla convertía un «probá de nuevo» en «no hay nada que deshacer».
            return new PcActionOutcome(
                false,
                $"No pude deshacer «{entry.Description}»: {result.Message} Lo dejo anotado por si querés reintentar.");
        }

        _journal.Discard(entry);
        return new PcActionOutcome(true, $"Deshice: {entry.Description}");
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

        return PressKey(key.Key)
            ? new PcActionOutcome(true, $"{key.Label}.")
            : new PcActionOutcome(false, "Windows no dejó mandar la tecla multimedia.");
    }

    private static PcActionOutcome Volume(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !VolumeKeys.TryGetValue(target.Trim(), out var key))
        {
            return new PcActionOutcome(false, "Puedo subir, bajar o silenciar el volumen.");
        }

        for (var press = 0; press < key.Repeat; press++)
        {
            if (!PressKey(key.Key))
            {
                return new PcActionOutcome(false, "Windows no dejó tocar el volumen.");
            }
        }

        return new PcActionOutcome(true, $"{key.Label}.");
    }

    /// <summary>Aprieta y suelta una tecla por su código virtual; dice si el sistema la aceptó.</summary>
    private static bool PressKey(byte virtualKey)
    {
        var flags = ExtendedKeys.Contains(virtualKey) ? KeyEventExtended : 0u;
        return Send(
            KeyboardEvent(virtualKey, 0, flags),
            KeyboardEvent(virtualKey, 0, flags | KeyEventKeyUp));
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

    /// <summary>Identificador del proceso de Viernes, para no confundir sus ventanas con las ajenas.</summary>
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private static bool IsOwnWindow(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId == OwnProcessId;
    }

    private static string TitleOf(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(length + 1);
        GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ClassNameOf(nint window)
    {
        var buffer = new System.Text.StringBuilder(256);
        var written = GetClassName(window, buffer, buffer.Capacity);
        return written > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>Clases que son el escritorio o la barra de tareas, no una aplicación con la que operar.</summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Button",
        "Windows.UI.Core.CoreWindow"
    };

    private static bool IsForeignApplicationWindow(nint window)
    {
        if (window == nint.Zero || !IsWindowVisible(window) || IsIconic(window) || IsOwnWindow(window))
        {
            return false;
        }

        if (DwmGetWindowAttribute(window, DwmAttributeCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return TitleOf(window).Length > 0 && !ShellWindowClasses.Contains(ClassNameOf(window));
    }

    /// <summary>
    /// La ventana con la que el usuario estaba trabajando: la de adelante si no es la nuestra, y si
    /// no, la primera ajena en orden de apilado.
    /// </summary>
    /// <remarks>
    /// Es la pieza que faltaba en todo lo que sigue. La ventana de Viernes es <c>Topmost</c> y tiene
    /// el foco justo después de que el usuario termina de hablarle, así que <c>GetForegroundWindow</c>
    /// devuelve el orbe: mirarla era fotografiarse a sí mismo —108×108 píxeles de nada— y escribir
    /// era meterle el texto a su propia caja. <c>EnumWindows</c> recorre de arriba hacia abajo, con lo
    /// cual saltear las propias deja arriba de todo exactamente la que el usuario está mirando.
    /// </remarks>
    internal static (nint Window, string Title) FrontForeignWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsForeignApplicationWindow(foreground))
        {
            return (foreground, TitleOf(foreground));
        }

        var found = (Window: nint.Zero, Title: string.Empty);
        EnumWindows((window, _) =>
        {
            if (!IsForeignApplicationWindow(window))
            {
                return true;
            }

            found = (window, TitleOf(window));
            return false;
        }, nint.Zero);

        return found;
    }

    /// <summary>
    /// Deja el teclado apuntando a una ventana que no sea la nuestra, o explica por qué no pudo.
    /// </summary>
    /// <remarks>
    /// Antes no existía: <c>type_text</c> y <c>press_key</c> tecleaban sin enfocar nada, y como el
    /// foco lo tenía la caja de texto de Viernes —el usuario acababa de escribir ahí— el texto entraba
    /// en la propia aplicación mientras la acción informaba «Escribí …». Fallar acá es la mitad
    /// importante: teclear a ciegas no es un éxito parcial, es escribir en el lugar equivocado.
    /// </remarks>
    private static (string Title, string? Problem) AcquireKeyboardTarget()
    {
        var (window, title) = FrontForeignWindow();
        if (window == nint.Zero)
        {
            return (
                string.Empty,
                "No hay ninguna ventana ajena adelante y no voy a teclear sobre la mía. " +
                "Abrí o traé al frente la aplicación donde querés que escriba.");
        }

        ForceForeground(window);
        if (!WaitFor(() => GetForegroundWindow() == window, TimeSpan.FromSeconds(2)))
        {
            return (
                title,
                $"No pude poner «{title}» al frente, así que no tecleé nada: " +
                "el texto habría terminado en mi propia ventana.");
        }

        // Tener el foco de ventana no es tener el foco de control: la aplicación todavía está
        // decidiendo qué campo lo recibe. Sin esta pausa, los primeros caracteres se pierden.
        Thread.Sleep(120);
        return (title, null);
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

        string? image;
        string what;
        if (activeOnly)
        {
            // No la del foreground: ésa es la nuestra. Ver el comentario de FrontForeignWindow.
            var (window, title) = FrontForeignWindow();
            if (window == nint.Zero)
            {
                return new PcActionOutcome(
                    false,
                    "No hay ninguna ventana adelante que no sea la mía, y sacarme una foto a mí " +
                    "mismo no te sirve. Pedime la pantalla entera o abrí la aplicación primero.");
            }

            image = ScreenCapture.CaptureWindow(window);
            what = $"Miré «{title}»";
        }
        else
        {
            image = ScreenCapture.CaptureScreen();
            what = "Miré la pantalla completa, con todos los monitores";
        }

        if (image is null)
        {
            return new PcActionOutcome(false, "Windows no dejó capturar la pantalla.");
        }

        var (width, height) = ScreenCapture.LastImageSize;
        return new PcActionOutcome(
            true,
            $"{what}. " +
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

    /// <summary>
    /// Lleva el cursor a un punto y confirma que llegó.
    /// </summary>
    /// <remarks>
    /// <c>SetCursorPos</c> puede recortar el destino a la pantalla virtual, y una aplicación a
    /// pantalla completa puede tener el cursor confinado a su rectángulo: en los dos casos la llamada
    /// dice que sí y el cursor queda en otro lado. Preguntar dónde quedó es lo único que distingue
    /// «lo moví» de «lo pedí». La tolerancia de dos píxeles es por el redondeo del escalado de la
    /// captura, no por resignación.
    /// </remarks>
    private static bool MoveCursorTo(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            return false;
        }

        return WaitFor(
            () => GetCursorPos(out var point) && Math.Abs(point.X - x) <= 2 && Math.Abs(point.Y - y) <= 2,
            TimeSpan.FromMilliseconds(500));
    }

    private static PcActionOutcome MoveCursor(string? target)
    {
        if (!TryParsePoint(target, out var x, out var y))
        {
            return new PcActionOutcome(false, "Necesito las coordenadas como «x,y».");
        }

        return MoveCursorTo(x, y)
            ? new PcActionOutcome(true, $"Moví el cursor a {x}, {y}.")
            : new PcActionOutcome(false, $"No pude llevar el cursor a {x}, {y}: Windows no lo dejó ir ahí.");
    }

    /// <summary>
    /// Hace clic, o dice por qué no.
    /// </summary>
    /// <remarks>
    /// Devolvía éxito pasara lo que pasara. Peor: si el destino no se entendía como coordenadas, el
    /// <c>if</c> sin <c>else</c> seguía de largo y clickeaba donde el cursor estuviera parado, que
    /// puede ser cualquier cosa —el escritorio, otra aplicación, un botón de cerrar—. Un clic a
    /// ciegas informado como clic acertado es la peor combinación posible: ni salió bien ni se sabe
    /// qué pasó. Ahora, sin coordenadas legibles no hay clic.
    /// </remarks>
    private static PcActionOutcome ClickAt(string? target, uint down, uint up, int times, string verb)
    {
        var where = string.Empty;

        // Sin destino se hace clic donde ya está el cursor: «hacé clic» después de moverlo.
        if (!string.IsNullOrWhiteSpace(target))
        {
            if (!TryParsePoint(target, out var x, out var y))
            {
                return new PcActionOutcome(
                    false,
                    $"No entendí «{target}» como coordenadas y no pienso hacer clic a ciegas. " +
                    "Necesito «x,y» leídas de la última captura.");
            }

            if (!MoveCursorTo(x, y))
            {
                return new PcActionOutcome(
                    false,
                    $"No pude llevar el cursor a {x}, {y}, así que no hice clic: habría caído en otro lado.");
            }

            where = $" en {x}, {y}";
        }

        for (var press = 0; press < times; press++)
        {
            if (!Send(MouseEvent(down, 0), MouseEvent(up, 0)))
            {
                return new PcActionOutcome(
                    false,
                    press == 0
                        ? "Windows no aceptó el clic; puede haber una ventana con más privilegios adelante."
                        : "El clic quedó a medias: Windows dejó de aceptar la entrada.");
            }
        }

        return new PcActionOutcome(true, $"{verb}{where}.");
    }

    /// <summary>
    /// Escribe texto como si lo tipearas, en la ventana que el usuario tiene delante.
    /// </summary>
    /// <remarks>
    /// Va por unidades de UTF-16 y no por códigos de tecla, así que las tildes y la ñ salen bien sin
    /// depender de la distribución del teclado. Los saltos de línea son la excepción: mandados como
    /// carácter Unicode no los toma casi ninguna aplicación, hay que mandar la tecla Enter.
    /// </remarks>
    private static PcActionOutcome TypeText(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué escribir.");
        }

        var (title, problem) = AcquireKeyboardTarget();
        if (problem is not null)
        {
            return new PcActionOutcome(false, problem);
        }

        // «\r\n» son dos caracteres y un solo salto: sin unificarlos, Enter se apretaría dos veces.
        var text = target.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var sent = character is '\n' or '\r'
                ? PressKey(0x0D)
                : Send(
                    KeyboardEvent(0, character, KeyEventUnicode),
                    KeyboardEvent(0, character, KeyEventUnicode | KeyEventKeyUp));

            if (!sent)
            {
                return new PcActionOutcome(
                    false,
                    index == 0
                        ? $"Windows no dejó escribir en «{title}»."
                        : $"Escribí sólo «{text[..index]}» en «{title}»: Windows cortó la entrada ahí.");
            }
        }

        return new PcActionOutcome(true, $"Escribí «{target}» en «{title}».");
    }

    private static PcActionOutcome PressNamedKey(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !NamedKeys.TryGetValue(target.Trim(), out var key))
        {
            return new PcActionOutcome(false, $"No conozco la tecla «{target}».");
        }

        var (title, problem) = AcquireKeyboardTarget();
        if (problem is not null)
        {
            return new PcActionOutcome(false, problem);
        }

        return PressKey(key)
            ? new PcActionOutcome(true, $"Apreté {target.Trim()} en «{title}».")
            : new PcActionOutcome(false, $"Windows no dejó mandarle {target.Trim()} a «{title}».");
    }

    /// <summary>
    /// Desplaza la vista de la ventana que está bajo el cursor, llevándolo ahí si hace falta.
    /// </summary>
    /// <remarks>
    /// La rueda se entrega a la ventana que el cursor tiene encima. Sin mover nada, el cursor suele
    /// quedar sobre el orbe de Viernes —que está siempre arriba de todo— y la rueda no llega a
    /// ninguna parte, mientras la acción informaba «Bajé la vista» con un <c>true</c> fijo. Sólo se
    /// mueve cuando el cursor está sobre una ventana nuestra o sobre nada: si el modelo lo posicionó
    /// a propósito con <c>move_cursor</c>, ese punto se respeta.
    /// </remarks>
    private static PcActionOutcome Scroll(string? target)
    {
        var text = target?.Trim() ?? string.Empty;
        var up = text.Contains("arriba", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("subir", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("up", StringComparison.OrdinalIgnoreCase);
        var down = text.Length == 0 ||
            text.Contains("abajo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bajar", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("down", StringComparison.OrdinalIgnoreCase);

        if (up == down)
        {
            return new PcActionOutcome(false, $"No entendí «{target}»: puedo desplazar «arriba» o «abajo».");
        }

        var (title, problem) = PlaceCursorForScroll();
        if (problem is not null)
        {
            return new PcActionOutcome(false, problem);
        }

        // Tres muescas por pedido: una sola casi no mueve nada y el modelo termina pidiendo diez.
        const int NotchesPerScroll = 3;
        const int WheelDelta = 120;
        if (!Send(MouseEvent(MouseWheel, (up ? 1 : -1) * WheelDelta * NotchesPerScroll)))
        {
            return new PcActionOutcome(false, $"Windows no aceptó el desplazamiento sobre «{title}».");
        }

        return new PcActionOutcome(true, up ? $"Subí la vista en «{title}»." : $"Bajé la vista en «{title}».");
    }

    /// <summary>Deja el cursor sobre una ventana ajena, o explica por qué no hay ninguna.</summary>
    private static (string Title, string? Problem) PlaceCursorForScroll()
    {
        if (GetCursorPos(out var cursor))
        {
            var under = WindowFromPoint(cursor);
            var root = under == nint.Zero ? nint.Zero : GetAncestor(under, AncestorRoot);
            if (root != nint.Zero && !IsOwnWindow(root))
            {
                var underTitle = TitleOf(root);
                return (underTitle.Length > 0 ? underTitle : "la ventana bajo el cursor", null);
            }
        }

        var (window, title) = FrontForeignWindow();
        if (window == nint.Zero)
        {
            return (
                string.Empty,
                "El cursor está sobre mi propia ventana y no hay ninguna otra adelante para desplazar.");
        }

        if (!GetWindowRect(window, out var bounds))
        {
            return (title, $"No pude ubicar «{title}» en pantalla, así que no desplacé nada.");
        }

        var centerX = bounds.Left + ((bounds.Right - bounds.Left) / 2);
        var centerY = bounds.Top + ((bounds.Bottom - bounds.Top) / 2);
        return MoveCursorTo(centerX, centerY)
            ? (title, null)
            : (title, $"No pude llevar el cursor sobre «{title}», así que no desplacé nada.");
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

    /// <summary>
    /// Cuántos nombres reales se le muestran al modelo cuando no sabe qué pedir.
    /// </summary>
    /// <remarks>
    /// El catálogo tiene cientos de entradas y mandarlo entero costaría más tokens que la charla.
    /// Cuarenta alcanzan para que se entienda que hay un catálogo de verdad y para que reconozca las
    /// que usa todos los días.
    /// </remarks>
    private const int NamesToOfferWhenLost = 40;

    private PcActionOutcome OpenApplication(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            // Enumerar es la respuesta útil. Decir sólo «necesito saber qué abrir» deja al modelo
            // adivinando nombres, que era exactamente lo que pasaba: el catálogo real estaba
            // calculado y no lo consumía nadie.
            var catalogue = _installed.Names.Take(NamesToOfferWhenLost).ToArray();
            return new PcActionOutcome(
                false,
                catalogue.Length == 0
                    ? "Necesito saber qué aplicación abrir."
                    : "Necesito saber qué aplicación abrir. Tenés instaladas, entre otras: " +
                      $"{string.Join(", ", catalogue)}.");
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

            var similar = _installed.Suggest(name);
            return new PcActionOutcome(
                false,
                similar.Count == 0
                    ? $"No encontré ninguna aplicación instalada que se llame «{name}»."
                    : $"No encontré «{name}». Lo más parecido que tenés instalado es: {string.Join(", ", similar)}. " +
                      "Si alguna es la que querías, pedímela con ese nombre.");
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
    /// <para>
    /// Después se la trae al frente, y no es un adorno. Lo que arranca un proceso en segundo plano no
    /// recibe el foco: al abrir el Bloc de notas desde acá, la ventana aparecía detrás y adelante
    /// seguía el navegador. Como las acciones de teclado escriben en la ventana de adelante, «abrí el
    /// bloc y escribí esto» terminaba tecleando dentro de una página web. Si Windows no deja hacer el
    /// cambio, se abre igual pero se dice, porque lo que viene después depende de eso.
    /// </para>
    /// </remarks>
    private static PcActionOutcome VerifyWindowAppeared(string needle, string label, HashSet<nint> before)
    {
        var appeared = nint.Zero;
        var stable = WaitForStable(
            () =>
            {
                appeared = FindWindows(needle).Except(before).FirstOrDefault();
                return appeared != nint.Zero;
            },
            TimeSpan.FromSeconds(10));

        if (!stable)
        {
            return new PcActionOutcome(
                false,
                $"Lancé {label} pero no llegué a ver su ventana. Puede estar tardando en cargar.");
        }

        ShowWindow(appeared, ShowRestore);
        ForceForeground(appeared);
        return WaitFor(() => GetForegroundWindow() == appeared, TimeSpan.FromSeconds(3))
            ? new PcActionOutcome(true, $"Abrí {label} y la puse adelante.")
            : new PcActionOutcome(
                true,
                $"Abrí {label}, pero quedó detrás: Windows no me dejó traerla al frente. " +
                "Si le vas a escribir, primero usá focus_application.");
    }

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
