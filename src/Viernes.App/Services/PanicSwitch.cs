using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Viernes.App.Services;

/// <summary>
/// Corte de emergencia por atajo global: <c>Ctrl + Shift + Alt + J</c>.
/// </summary>
/// <remarks>
/// Desde que Viernes mueve el cursor, escribe con el teclado y hace clic, hace falta una forma de
/// pararlo que <b>no dependa de él</b>. Pedírselo hablando no sirve: si está escribiendo en una
/// ventana, tu voz compite con su propia acción, y si algo salió mal lo último que querés es tener
/// que negociar con el sistema que se descontroló.
/// <para>
/// Por eso esto no pasa por el modelo, ni por la política, ni por la conversación. Es un atajo que
/// registra Windows y una llamada directa. La única inteligencia aceptable en un freno de emergencia
/// es ninguna.
/// </para>
/// <para>
/// El atajo lleva tres modificadores a propósito: tiene que ser imposible de apretar sin querer y
/// alcanzable con una sola mano.
/// </para>
/// </remarks>
internal sealed class PanicSwitch : IDisposable
{
    private const int HotkeyId = 0x5649;
    private const int WmHotkey = 0x0312;

    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierNoRepeat = 0x4000;
    private const uint KeyJ = 0x4A;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    private readonly Action _onPanic;
    private HwndSource? _source;
    private bool _registered;

    public PanicSwitch(Action onPanic) => _onPanic = onPanic;

    /// <summary>Describe el atajo para poder mostrarlo sin repetir la combinación en dos lados.</summary>
    public static string Shortcut => "Ctrl + Shift + Alt + J";

    /// <summary>
    /// Engancha el atajo a la ventana. Si Windows lo rechaza —porque otra aplicación ya lo tomó—
    /// se informa en vez de quedar en silencio: un freno que nadie sabe que no existe es peor que
    /// no tener freno.
    /// </summary>
    public bool TryAttach(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return false;
        }

        _source.AddHook(OnWindowMessage);
        _registered = RegisterHotKey(
            handle,
            HotkeyId,
            ModifierControl | ModifierShift | ModifierAlt | ModifierNoRepeat,
            KeyJ);
        return _registered;
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != HotkeyId)
        {
            return nint.Zero;
        }

        handled = true;
        try
        {
            _onPanic();
        }
        catch (Exception)
        {
            // Un freno de emergencia que lanza excepciones no frena nada.
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source.RemoveHook(OnWindowMessage);
        _source = null;
    }
}
