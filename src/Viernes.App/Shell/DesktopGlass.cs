using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Viernes.App.Shell;

/// <summary>
/// Decide con qué receta se dibuja el vidrio en esta máquina, y pide el acrílico del sistema cuando
/// se lo puede aprovechar.
/// </summary>
/// <remarks>
/// El acrílico nativo de Windows 11 sólo existe sobre una ventana con marco compuesto por DWM. Una
/// ventana con <see cref="Window.AllowsTransparency"/> es una <em>layered window</em>: DWM no le
/// compone nada detrás porque el que compone es el propio proceso, píxel por píxel. Viernes necesita
/// transparencia por píxel —el orbe es una gota, no un rectángulo—, así que en esta ventana el
/// desenfoque del sistema no está disponible ni en Windows 11.
/// <para>
/// Por eso la receta que se usa hoy es la opaca, que es la que estaba pensada para el peor de los dos
/// casos. Las variantes clara y oscura no son adorno: son las que corresponden apenas exista un
/// alojamiento sin capa —una ventana con marco DWM para el desplegable— y por eso viven medidas y
/// listas en <see cref="GlassPalette"/> en vez de tener que volver a deducirse.
/// </para>
/// </remarks>
internal static class DesktopGlass
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtTransientWindow = 3;

    /// <summary>Si esta build de Windows sabe de <c>SystemBackdropType</c>.</summary>
    public static bool SupportsSystemBackdrop =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Si el escritorio está en claro. Cambia la variante y el peso de las sombras.</summary>
    public static bool IsLightDesktop
    {
        get
        {
            try
            {
                var value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    1);
                return value is int flag && flag != 0;
            }
            catch (Exception)
            {
                // Una política de grupo puede bloquear la lectura. Oscuro es el caso más común y el
                // que mejor tolera equivocarse: un panel de más contraste sobre un fondo claro se lee.
                return false;
            }
        }
    }

    /// <summary>
    /// Qué receta corresponde para una ventana concreta.
    /// </summary>
    public static GlassVariant Resolve(Window window)
    {
        if (window.AllowsTransparency || !SupportsSystemBackdrop)
        {
            return GlassVariant.Opaco;
        }

        return IsLightDesktop ? GlassVariant.AcrilicoClaro : GlassVariant.AcrilicoOscuro;
    }

    /// <summary>
    /// Pide el acrílico del sistema. Devuelve si quedó puesto de verdad.
    /// </summary>
    /// <remarks>
    /// Sobre una ventana con capa la llamada puede devolver éxito y no dibujar nada, así que ni se
    /// intenta: se responde que no y el vidrio se dibuja completo. Prometer un desenfoque que no está
    /// es peor que no tenerlo, porque el cuerpo se aclara confiando en algo que no llega.
    /// </remarks>
    public static bool TryApplySystemBackdrop(Window window)
    {
        if (window.AllowsTransparency || !SupportsSystemBackdrop)
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return false;
        }

        var backdrop = DwmsbtTransientWindow;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
