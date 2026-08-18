using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Saca una foto de la pantalla y la deja lista para mandársela al modelo.
/// </summary>
/// <remarks>
/// Es lo que separa «contame qué ves» de «mirá». Sin esto, para actuar sobre algo que está en
/// pantalla hay que describírselo con palabras, y describir una interfaz con palabras es exactamente
/// lo que nadie quiere hacer.
/// <para>
/// Se reduce a lo ancho antes de codificar, y no por prolijidad: una pantalla 4K son varios millones
/// de píxeles que se cobran como tokens en cada turno y se pagan en espera. A 1280 de ancho el
/// modelo sigue leyendo botones y texto de interfaz, que es para lo que se la manda.
/// </para>
/// <para>
/// La imagen nunca toca el disco: se arma en memoria, se manda y se descarta.
/// </para>
/// </remarks>
internal static class ScreenCapture
{
    private const int MaximumWidth = 1280;
    private const uint SourceCopy = 0x00CC0020;

    // Métricas de la pantalla virtual: el rectángulo que abarca TODOS los monitores, con su origen
    // real —que es negativo cuando hay un monitor a la izquierda del primario—.
    private const int MetricVirtualLeft = 76;
    private const int MetricVirtualTop = 77;
    private const int MetricVirtualWidth = 78;
    private const int MetricVirtualHeight = 79;
    private const int MetricPrimaryWidth = 0;
    private const int MetricPrimaryHeight = 1;

    /// <summary>
    /// Cuántos píxeles de pantalla vale cada píxel de la última captura entregada al modelo.
    /// </summary>
    /// <remarks>
    /// El modelo mira una imagen reducida y de ahí saca las coordenadas, pero el cursor se mueve
    /// sobre la pantalla real. Sin este factor, en un monitor 2560×1440 cada clic caería a la mitad
    /// de camino del objetivo. Se corrige acá y no pidiéndole al modelo que multiplique: hacer
    /// aritmética de escala es justo el tipo de paso donde un modelo chico se equivoca, y además
    /// tendría que saber una resolución que no ve.
    /// </remarks>
    public static double LastScale { get; private set; } = 1;

    /// <summary>Esquina superior izquierda de lo último capturado, en coordenadas de pantalla.</summary>
    public static (int X, int Y) LastOrigin { get; private set; }

    /// <summary>Tamaño de la imagen entregada. Se le dice al modelo para que ubique sobre ella.</summary>
    public static (int Width, int Height) LastImageSize { get; private set; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint operation);

    /// <summary>
    /// Captura todos los monitores. Devuelve un <c>data:</c> URL con PNG, o <c>null</c> si Windows no
    /// dejó capturar.
    /// </summary>
    /// <remarks>
    /// Antes tomaba el rectángulo de <c>GetDesktopWindow()</c>, que mide sólo el monitor primario:
    /// con dos pantallas, la segunda directamente no existía y el modelo daba coordenadas sobre una
    /// mitad del escritorio creyendo que era todo. La pantalla virtual —origen incluido, que es
    /// negativo cuando hay un monitor a la izquierda— es el único rectángulo que las contiene a todas.
    /// El origen se guarda en <see cref="LastOrigin"/> porque los clics se calculan sumándolo: sin él,
    /// un punto leído sobre el monitor izquierdo caería en el derecho.
    /// </remarks>
    public static string? CaptureScreen()
    {
        var left = GetSystemMetrics(MetricVirtualLeft);
        var top = GetSystemMetrics(MetricVirtualTop);
        var width = GetSystemMetrics(MetricVirtualWidth);
        var height = GetSystemMetrics(MetricVirtualHeight);

        // Si el sistema no reporta la pantalla virtual queda el primario, que es peor pero no es nada.
        if (width <= 0 || height <= 0)
        {
            left = 0;
            top = 0;
            width = GetSystemMetrics(MetricPrimaryWidth);
            height = GetSystemMetrics(MetricPrimaryHeight);
        }

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // El DC de pantalla —GetDC(NULL)— abarca la pantalla virtual entera y admite coordenadas
        // negativas; el de la ventana escritorio se recorta al primario.
        return Capture(nint.Zero, GetDC(nint.Zero), left, top, left, top, width, height);
    }

    /// <summary>
    /// Captura una ventana concreta, por su identificador. Devuelve <c>null</c> si no se pudo.
    /// </summary>
    public static string? CaptureWindow(nint window)
    {
        if (window == nint.Zero || !GetWindowRect(window, out var bounds))
        {
            return null;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return Capture(window, GetWindowDC(window), bounds.Left, bounds.Top, 0, 0, width, height);
    }

    /// <summary>
    /// Copia un rectángulo del <paramref name="sourceDc"/> y lo codifica.
    /// </summary>
    /// <remarks>
    /// <paramref name="originX"/> y <paramref name="originY"/> son coordenadas de pantalla —lo que
    /// hay que sumarle a un punto leído sobre la imagen—, mientras que <paramref name="sourceX"/> y
    /// <paramref name="sourceY"/> son el desplazamiento dentro del DC de origen. Coinciden para la
    /// pantalla y difieren para una ventana, cuyo DC ya arranca en su propia esquina.
    /// </remarks>
    private static string? Capture(
        nint dcOwner,
        nint sourceDc,
        int originX,
        int originY,
        int sourceX,
        int sourceY,
        int width,
        int height)
    {
        if (sourceDc == nint.Zero)
        {
            return null;
        }

        var memoryDc = nint.Zero;
        var bitmap = nint.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(sourceDc);
            bitmap = CreateCompatibleBitmap(sourceDc, width, height);
            if (memoryDc == nint.Zero || bitmap == nint.Zero)
            {
                return null;
            }

            var previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, width, height, sourceDc, sourceX, sourceY, SourceCopy))
            {
                return null;
            }

            SelectObject(memoryDc, previous);
            LastOrigin = (originX, originY);
            LastScale = width > MaximumWidth ? (double)width / MaximumWidth : 1;
            return Encode(bitmap, width, height);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (bitmap != nint.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != nint.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(dcOwner, sourceDc);
        }
    }

    private static string Encode(nint bitmap, int width, int height)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap,
            nint.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        if (width > MaximumWidth)
        {
            var scale = (double)MaximumWidth / width;
            var scaledWidth = (int)(width * scale);
            var scaledHeight = (int)(height * scale);

            var visual = new DrawingVisual();
            // Alta calidad y no vecino más cercano: la interfaz tiene texto chico y se destruye.
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(0, 0, scaledWidth, scaledHeight));
            }

            var target = new RenderTargetBitmap(scaledWidth, scaledHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            source = target;
            LastImageSize = (scaledWidth, scaledHeight);
        }
        else
        {
            LastImageSize = (width, height);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
    }
}
