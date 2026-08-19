using System.Runtime.InteropServices;
using System.Text;

namespace Viernes.App.Shell;

/// <summary>
/// Si lo que está adelante ocupa el monitor entero.
/// </summary>
/// <remarks>
/// Un video, un juego, una presentación. En ese momento el usuario está mirando eso y nada más, así
/// que el orbe se esconde entero y sólo deja un filete si hay algo urgente.
/// <para>
/// La única forma que anda en Windows es comparar el rectángulo de la ventana en primer plano contra
/// el <b>área del monitor</b> —no el área de trabajo—: una ventana maximizada llega hasta el borde
/// del área de trabajo y deja la barra de tareas afuera, y eso <em>no</em> es pantalla completa. Si
/// se comparara contra el área de trabajo, cualquier ventana maximizada escondería el orbe y Viernes
/// desaparecería la mitad del día.
/// </para>
/// <para>
/// <b>Comparar contra el monitor no alcanza, y esto ya falló una vez.</b> Con la barra de tareas en
/// ocultamiento automático, Windows informa el área de trabajo <em>igual</em> al monitor entero, así
/// que una ventana maximizada cualquiera lo cubre y Viernes desaparecía con el navegador maximizado.
/// Medido en la máquina donde pasó: Chrome maximizado da el rectángulo (−8,−8)−(1928,1088) contra un
/// monitor de (0,0)−(1920,1080), con <c>IsZoomed</c> verdadero y <c>WS_CAPTION</c> puesto; la ventana
/// sin bordes a pantalla completa da (0,0)−(1920,1080), <c>IsZoomed</c> falso y sin barra de título.
/// De ahí sale la segunda condición: <b>una ventana maximizada que todavía tiene barra de título no
/// es pantalla completa</b>, por más que cubra todo. Sacarla vuelve a traer el bug, que no se ve como
/// un error sino como que Viernes se apagó solo.
/// </para>
/// <para>
/// Los dos rectángulos se comparan en píxeles físicos y sin convertir: <c>GetWindowRect</c> y
/// <c>Screen.Bounds</c> hablan los dos ese idioma. Meter el DPI en el medio sólo agrega una división
/// que puede redondear mal y decidir mal.
/// </para>
/// <para>
/// Se buscó en el repo antes de escribir esto. <c>DesktopSignals</c> mira memoria y ventanas
/// perdidas, no tamaños; <c>WindowsPcActionExecutor</c> y <c>WindowsEnvironmentObserver</c> tienen
/// <c>GetForegroundWindow</c> y <c>GetWindowRect</c> declarados <c>private</c> y para otra cosa. No
/// había nada que reusar sin abrir <c>Viernes.Platform.Windows</c>.
/// </para>
/// </remarks>
internal static class FullScreenWatch
{
    /// <summary>
    /// Cuánto puede faltarle a una ventana para que igual cuente como pantalla completa.
    /// </summary>
    /// <remarks>
    /// Un píxel. Hay reproductores que dejan el borde justo adentro y juegos que informan un
    /// rectángulo corrido por el redondeo del escalado. Más tolerancia que ésta empieza a contar
    /// ventanas maximizadas en pantallas sin barra de tareas visible.
    /// </remarks>
    private const int Slack = 1;

    private const int GwlStyle = -16;

    /// <summary>WS_CAPTION. Es <c>WS_BORDER | WS_DLGFRAME</c>, así que se compara la máscara entera.</summary>
    private const long WsCaption = 0x00C00000;

    /// <summary>
    /// Clases de ventana que son el escritorio o la shell, y nunca son «pantalla completa».
    /// </summary>
    /// <remarks>
    /// El escritorio ocupa el monitor entero por definición. Sin descartarlo, minimizar todo dejaba
    /// a Viernes escondido para siempre: exactamente cuando más se lo ve.
    /// </remarks>
    private static readonly string[] ShellClasses =
    [
        "Progman",          // el escritorio
        "WorkerW",          // el escritorio con fondo animado
        "Shell_TrayWnd",    // la barra de tareas
        "Windows.UI.Core.CoreWindow" // el menú inicio y el centro de notificaciones
    ];

    /// <summary>
    /// Si hay algo en pantalla completa adelante, en el mismo monitor donde vive el orbe.
    /// </summary>
    /// <param name="own">La ventana de Viernes, para no contarse a sí misma.</param>
    /// <param name="orbScreen">
    /// El monitor donde está el orbe. Con dos pantallas, un video a pantalla completa en una no es
    /// razón para desaparecer de la otra: ahí no le tapa nada a nadie, y esconderse igual sería
    /// perder al asistente por algo que pasa en otro lado.
    /// </param>
    public static bool IsForegroundFullScreen(nint own, System.Windows.Forms.Screen? orbScreen)
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == nint.Zero || foreground == own)
            {
                return false;
            }

            // El escritorio mide lo que mide el monitor. Contarlo sería esconderse al minimizar todo.
            if (foreground == GetShellWindow() || foreground == GetDesktopWindow())
            {
                return false;
            }

            if (IsShellClass(foreground))
            {
                return false;
            }

            // Maximizada con barra de título es maximizada, no pantalla completa. Con la barra de
            // tareas oculta automáticamente, el área de trabajo mide lo mismo que el monitor y sin
            // esta línea cualquier ventana maximizada esconde a Viernes.
            if (IsZoomed(foreground) && HasCaption(foreground))
            {
                return false;
            }

            if (!GetWindowRect(foreground, out var window))
            {
                return false;
            }

            var screen = System.Windows.Forms.Screen.FromHandle(foreground);
            if (orbScreen is not null &&
                !string.Equals(screen.DeviceName, orbScreen.DeviceName, StringComparison.Ordinal))
            {
                return false;
            }

            var monitor = screen.Bounds;

            return window.Left <= monitor.Left + Slack &&
                window.Top <= monitor.Top + Slack &&
                window.Right >= monitor.Right - Slack &&
                window.Bottom >= monitor.Bottom - Slack;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Mirar el escritorio no puede tumbar el asistente. Si no se sabe, no está en pantalla
            // completa: equivocarse hacia «se ve» es mucho más barato que hacia «desaparecí».
            return false;
        }
    }

    /// <summary>Si la ventana conserva su barra de título. Las de pantalla completa la sueltan.</summary>
    private static bool HasCaption(nint window) => (GetWindowLong(window, GwlStyle) & WsCaption) == WsCaption;

    private static bool IsShellClass(nint window)
    {
        var name = new StringBuilder(64);
        if (GetClassName(window, name, name.Capacity) == 0)
        {
            return false;
        }

        var text = name.ToString();
        return ShellClasses.Any(shell => string.Equals(shell, text, StringComparison.Ordinal));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLong(nint window, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
