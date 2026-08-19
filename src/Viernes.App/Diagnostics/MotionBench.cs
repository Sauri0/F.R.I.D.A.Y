using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Viernes.App.Controls;
using Viernes.App.Shell;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace Viernes.App.Diagnostics;

/// <summary>Cómo llega la posición del orbe a la pantalla, en cada una de las formas que se midieron.</summary>
internal enum MotionBenchMode
{
    /// <summary>Lo de hoy: <c>Window.Left</c> y <c>Window.Top</c>, uno por propiedad, por cuadro.</summary>
    WindowProperties,

    /// <summary>Un solo <c>SetWindowPos</c> por cuadro, y sólo si la posición cambió de píxel entero.</summary>
    WindowSetPos,

    /// <summary>La ventana no se mueve: mide el escritorio virtual entero y el orbe viaja adentro.</summary>
    WideWindow
}

/// <summary>
/// El banco de fluidez: mide cuánto tarda un cuadro de verdad y cuánto salta el orbe entre uno y
/// otro, con el mismo movimiento corrido de las tres maneras posibles.
/// </summary>
/// <remarks>
/// Existe porque «se siente áspero» no se arregla adivinando y porque la hipótesis que había para
/// arreglarlo —dejar de mover la ventana y mover el contenido adentro de una ventana quieta del
/// tamaño del escritorio virtual— es un cambio grande que toca el hit-testing, la detección de
/// pantalla completa y el DPI por monitor. Antes de pagar eso hay que ver el número.
/// <para>
/// Los tres modos corren <b>exactamente la misma física</b>, con el mismo guion de arrastre y de
/// vuelo, sobre la misma clase de ventana por capas. La única diferencia es a dónde va la posición.
/// </para>
/// <para>
/// Se mide con la ventana <b>a la vista</b> y no en −4000 como hace <see cref="OrbSnapshot"/>: lo
/// que se está midiendo es justamente lo que cuesta recomponer una ventana por capas contra el
/// escritorio, y una ventana fuera de pantalla no lo paga.
/// </para>
/// </remarks>
internal static class MotionBench
{
    /// <summary>Cuánto dura el tramo de arrastre de cada pasada.</summary>
    private static readonly TimeSpan DragSpan = TimeSpan.FromSeconds(1.6);

    /// <summary>Cuánto se lo deja volar después de soltarlo, para que rebote contra los bordes.</summary>
    private static readonly TimeSpan FlightSpan = TimeSpan.FromSeconds(1.8);

    /// <summary>Cuadros de calentamiento que se descartan: los primeros de una ventana nueva mienten.</summary>
    /// <remarks>
    /// Los primeros cuadros incluyen la creación de la superficie de la ventana y la primera
    /// composición del cuerpo. Meterlos en la mediana arruina justamente la medición del caso
    /// estable, que es el que el usuario ve durante horas.
    /// </remarks>
    private const int WarmupFrames = 20;

    /// <summary>
    /// Corre todas las pasadas y devuelve el informe. Una medición por línea, todas con su método.
    /// </summary>
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();

        report.AppendLine("BANCO DE FLUIDEZ DEL ORBE");
        report.AppendLine(new string('=', 78));
        report.AppendLine();
        report.AppendLine($"Fecha              {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Nivel de render    tier {RenderCapability.Tier >> 16}");
        report.AppendLine($"Escritorio virtual {SystemParameters.VirtualScreenWidth:0}×" +
            $"{SystemParameters.VirtualScreenHeight:0} desde " +
            $"({SystemParameters.VirtualScreenLeft:0};{SystemParameters.VirtualScreenTop:0})");

        foreach (var each in System.Windows.Forms.Screen.AllScreens)
        {
            report.AppendLine(
                $"Pantalla           {each.DeviceName} · {each.Bounds.Width}×{each.Bounds.Height} " +
                $"en ({each.Bounds.Left};{each.Bounds.Top}) · {RefreshHz(each.DeviceName)} Hz nominales" +
                $"{(each.Primary ? " · primaria" : string.Empty)}");
        }

        report.AppendLine();
        report.AppendLine("Método: se corre la MISMA física (OrbMotion) con el mismo guion —arrastre de");
        report.AppendLine($"{DragSpan.TotalSeconds:0.0} s siguiendo un objetivo que barre la pantalla, después soltar y");
        report.AppendLine($"{FlightSpan.TotalSeconds:0.0} s de vuelo con rebotes— sobre una ventana por capas");
        report.AppendLine("(AllowsTransparency) igual a la de la aplicación. dt es el intervalo real entre dos");
        report.AppendLine($"RenderingTime consecutivos. Se descartan los primeros {WarmupFrames} cuadros.");
        report.AppendLine();

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            report.AppendLine();
            report.AppendLine($"### {screen.DeviceName} · {RefreshHz(screen.DeviceName)} Hz");
            report.AppendLine();

            foreach (var shape in new[] { OrbShape.Gota, OrbShape.Nube })
            {
                foreach (var mode in new[]
                {
                    MotionBenchMode.WindowProperties,
                    MotionBenchMode.WindowSetPos,
                    MotionBenchMode.WideWindow
                })
                {
                    report.AppendLine(await RunPassAsync(shape, mode, screen).ConfigureAwait(true));
                    report.AppendLine();
                }
            }
        }

        return report.ToString();
    }

    private static async Task<string> RunPassAsync(
        OrbShape shape,
        MotionBenchMode mode,
        System.Windows.Forms.Screen screen)
    {
        // Todo el banco vive en el espacio de WPF. La escala sale de la pantalla que se está
        // midiendo y no de una ventana, que es lo que le falta al resto del proyecto.
        var scale = ScaleOf(screen);
        var workArea = new Rect(
            screen.WorkingArea.Left / scale,
            screen.WorkingArea.Top / scale,
            screen.WorkingArea.Width / scale,
            screen.WorkingArea.Height / scale);

        var bounds = ShellLayout.OrbBounds(workArea);
        var motion = new OrbMotion();
        motion.Teleport(new Point(bounds.Left, bounds.Top + (bounds.Height / 2)));

        var slide = new TranslateTransform();
        var host = new Canvas { Background = null, RenderTransform = slide };

        FrameworkElement body = shape == OrbShape.Nube
            ? new NubeOrb { Width = ShellLayout.OrbSize, Height = ShellLayout.OrbSize }
            : new LiquidOrb { Width = ShellLayout.OrbSize, Height = ShellLayout.OrbSize };
        var sink = (IOrbMotionSink)body;

        var wide = mode == MotionBenchMode.WideWindow;
        Canvas.SetLeft(body, wide ? 0 : ShellLayout.OrbLeftWhenOpeningRight);
        Canvas.SetTop(body, wide ? 0 : ShellLayout.OrbTop);
        host.Children.Add(body);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Content = host
        };

        if (wide)
        {
            window.Left = SystemParameters.VirtualScreenLeft;
            window.Top = SystemParameters.VirtualScreenTop;
            window.Width = SystemParameters.VirtualScreenWidth;
            window.Height = SystemParameters.VirtualScreenHeight;
        }
        else
        {
            window.Width = ShellLayout.WindowWidth;
            window.Height = ShellLayout.WindowHeight;
            var origin = ShellLayout.WindowOriginFor(motion.Position, opensRight: true);
            window.Left = origin.X;
            window.Top = origin.Y;
        }

        var deltas = new List<double>(8192);
        var jumps = new List<double>(8192);
        var frames = 0;
        var writes = 0;
        var lastRender = default(TimeSpan);
        var previous = motion.Position;
        var elapsed = 0.0;
        var measured = 0.0;
        var dropped = false;
        var writtenX = int.MinValue;
        var writtenY = int.MinValue;
        var handle = nint.Zero;
        var finished = new TaskCompletionSource();

        void OnRendering(object? sender, EventArgs e)
        {
            if (e is not RenderingEventArgs rendering)
            {
                return;
            }

            if (lastRender == default)
            {
                lastRender = rendering.RenderingTime;
                return;
            }

            var dt = (rendering.RenderingTime - lastRender).TotalSeconds;
            lastRender = rendering.RenderingTime;
            if (dt <= 0)
            {
                return;
            }

            elapsed += dt;

            // El guion. El objetivo del arrastre barre de un borde al otro y vuelve, que es el
            // movimiento del que se queja el usuario: no una recta corta, sino pasearlo.
            if (!dropped)
            {
                if (elapsed >= DragSpan.TotalSeconds)
                {
                    motion.Drop();
                    dropped = true;
                }
                else
                {
                    if (!motion.IsDragging)
                    {
                        motion.BeginDrag();
                    }

                    var phase = elapsed / DragSpan.TotalSeconds;
                    motion.DragTo(new Point(
                        bounds.Left + (bounds.Width * (0.5 - (0.5 * Math.Cos(phase * Math.PI * 2)))),
                        bounds.Top + (bounds.Height * (0.5 - (0.4 * Math.Cos(phase * Math.PI * 4))))));
                }
            }

            motion.Step(dt, bounds);

            switch (mode)
            {
                case MotionBenchMode.WideWindow:
                    slide.X = motion.Position.X - SystemParameters.VirtualScreenLeft;
                    slide.Y = motion.Position.Y - SystemParameters.VirtualScreenTop;
                    writes++;
                    break;

                case MotionBenchMode.WindowSetPos:
                {
                    var origin = ShellLayout.WindowOriginFor(motion.Position, opensRight: true);
                    var x = (int)Math.Round(origin.X * scale);
                    var y = (int)Math.Round(origin.Y * scale);
                    if (x != writtenX || y != writtenY)
                    {
                        writtenX = x;
                        writtenY = y;
                        handle = handle == nint.Zero ? new WindowInteropHelper(window).Handle : handle;
                        SetWindowPos(handle, nint.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                        writes++;
                    }

                    break;
                }

                default:
                {
                    var origin = ShellLayout.WindowOriginFor(motion.Position, opensRight: true);
                    window.Left = origin.X;
                    window.Top = origin.Y;
                    writes++;
                    break;
                }
            }

            sink.ReportMotion(motion.Sample);

            frames++;
            if (frames > WarmupFrames)
            {
                deltas.Add(dt * 1000);
                jumps.Add((motion.Position - previous).Length);
                measured += dt;
            }

            previous = motion.Position;

            if (elapsed >= DragSpan.TotalSeconds + FlightSpan.TotalSeconds)
            {
                finished.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        window.Show();

        try
        {
            await finished.Task.ConfigureAwait(true);
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            window.Close();
        }

        var drawn = body is NubeOrb nube ? nube.DrawnFrames : ((LiquidOrb)body).DrawnFrames;
        var head = $"{Label(mode)}  ·  cuerpo {shape}";

        return new StringBuilder()
            .AppendLine(head)
            .AppendLine(new string('-', head.Length))
            .AppendLine($"  cuadros medidos     {deltas.Count} en {measured:0.00} s")
            .AppendLine($"  cuadros por segundo {deltas.Count / Math.Max(0.001, measured):0.0}")
            .AppendLine($"  dt mediana          {Median(deltas):0.00} ms")
            .AppendLine($"  dt p95              {Percentile(deltas, 0.95):0.00} ms")
            .AppendLine($"  dt máximo           {Max(deltas):0.00} ms")
            .AppendLine($"  cuadros tardíos     {Late(deltas):0.0} %  (dt > 1,5 × la mediana)")
            .AppendLine($"  salto mediano       {Median(jumps):0.0} px")
            .AppendLine($"  salto p95           {Percentile(jumps, 0.95):0.0} px")
            .AppendLine($"  salto máximo        {Max(jumps):0.0} px")
            .AppendLine($"  escrituras          {writes} de {frames} cuadros")
            .AppendLine($"  el cuerpo se dibujó {drawn} veces  ({drawn / Math.Max(0.001, elapsed):0.0} por segundo)")
            .ToString();
    }

    private static string Label(MotionBenchMode mode) => mode switch
    {
        MotionBenchMode.WindowSetPos => "VENTANA·SETPOS · un SetWindowPos, sólo al cambiar de píxel",
        MotionBenchMode.WideWindow => "CONTENIDO      · ventana quieta del escritorio virtual",
        _ => "VENTANA·WPF    · Window.Left y Window.Top por cuadro"
    };

    /// <summary>Escala de una pantalla, de píxeles físicos a unidades de WPF.</summary>
    /// <remarks>
    /// Se pregunta por monitor con <c>GetDpiForMonitor</c> y no por la ventana: el banco corre en
    /// las dos pantallas y una de ellas puede estar a otra escala. Si el sistema no contesta, 96 —o
    /// sea escala 1— es la respuesta correcta para un escritorio sin escalar.
    /// </remarks>
    private static double ScaleOf(System.Windows.Forms.Screen screen)
    {
        try
        {
            var monitor = MonitorFromPoint(
                new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 },
                MonitorDefaultToNearest);
            return GetDpiForMonitor(monitor, MonitorDpiEffective, out var dpiX, out _) == 0 && dpiX > 0
                ? dpiX / 96.0
                : 1.0;
        }
        catch (DllNotFoundException)
        {
            return 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    private static double Median(List<double> values) => Percentile(values, 0.5);

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        var index = Math.Clamp((int)Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double Max(List<double> values)
    {
        var top = 0.0;
        foreach (var value in values)
        {
            top = Math.Max(top, value);
        }

        return top;
    }

    /// <summary>Porcentaje de cuadros que tardaron más de una vez y media la mediana.</summary>
    /// <remarks>
    /// Es la cifra que se corresponde con «a los tirones». Un promedio alto con todos los cuadros
    /// parejos se ve fluido y lento; un promedio bajo con un 20 % de cuadros al doble se ve áspero.
    /// </remarks>
    private static double Late(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var threshold = Median(values) * 1.5;
        var late = 0;
        foreach (var value in values)
        {
            if (value > threshold)
            {
                late++;
            }
        }

        return late * 100.0 / values.Count;
    }

    /// <summary>Frecuencia nominal de una pantalla, en Hz. Cero si Windows no la sabe decir.</summary>
    private static int RefreshHz(string deviceName)
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        return EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) ? mode.dmDisplayFrequency : 0;
    }

    private const int EnumCurrentSettings = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int MonitorDefaultToNearest = 2;
    private const int MonitorDpiEffective = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
