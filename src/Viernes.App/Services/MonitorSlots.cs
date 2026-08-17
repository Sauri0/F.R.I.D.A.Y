using System.IO;
using System.Text.Json;
using System.Windows;

// Este archivo toca WinForms —para enumerar monitores— y WPF —para posicionar la ventana—, y los dos
// definen Point, Size y Rect. Los alias fijan que acá siempre se habla en coordenadas de WPF.
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace Viernes.App.Services;

/// <summary>
/// Recuerda dónde va el orbe <b>en cada monitor</b>, y decide en cuál aparecer cuando lo llamás.
/// </summary>
/// <remarks>
/// Aparece donde está el cursor, no donde quedó él. El cursor es la mejor aproximación disponible a
/// dónde estás mirando, y hacerte buscar el orbe en la otra pantalla es pedirte trabajo por una
/// decisión que el código puede tomar solo. Para un asistente de voz, contestar en un monitor que no
/// estás mirando es no contestar.
/// <para>
/// La clave del slot es el nombre del dispositivo más su resolución: si cambiás de resolución o
/// desenchufás una pantalla, la posición guardada dejó de significar lo mismo y conviene recalcular
/// antes que restaurar algo que quedaría fuera de vista.
/// </para>
/// </remarks>
internal sealed class MonitorSlots
{
    /// <summary>Margen contra el borde. Va sobre el área de trabajo, no sobre los límites físicos.</summary>
    private const double Margin = 24;

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "monitores.json");

    private Dictionary<string, Point> _slots = new(StringComparer.Ordinal);

    public MonitorSlots() => Load();

    /// <summary>Monitor donde está el cursor, con su área de trabajo y su clave de slot.</summary>
    public static (string Key, Rect WorkArea) MonitorUnderCursor()
    {
        var position = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(position);
        return (KeyFor(screen), ToRect(screen.WorkingArea));
    }

    /// <summary>Monitor que contiene un punto dado, para saber en cuál está el orbe ahora.</summary>
    public static (string Key, Rect WorkArea) MonitorAt(Point point)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)point.X, (int)point.Y));
        return (KeyFor(screen), ToRect(screen.WorkingArea));
    }

    /// <summary>
    /// Dónde poner el orbe en ese monitor. Sin historial, esquina inferior derecha.
    /// </summary>
    /// <remarks>
    /// El margen se mide contra el <em>área de trabajo</em> y no contra los límites del monitor: si
    /// se midiera contra los límites, en la pantalla con la barra de tareas el orbe quedaría debajo
    /// de ella.
    /// </remarks>
    public Point SlotFor(string key, Rect workArea, Size orbSize)
    {
        if (_slots.TryGetValue(key, out var stored) && Contains(workArea, stored, orbSize))
        {
            return stored;
        }

        return new Point(
            workArea.Right - orbSize.Width - Margin,
            workArea.Bottom - orbSize.Height - Margin);
    }

    /// <summary>Guarda dónde lo dejaste, para ese monitor y esa resolución.</summary>
    public void Remember(string key, Point position)
    {
        _slots[key] = position;
        Save();
    }

    /// <summary>
    /// Imanta al borde si lo soltaste cerca. Devuelve la posición final, ya recortada al área útil.
    /// </summary>
    public static Point Magnetize(Point position, Rect workArea, Size orbSize)
    {
        const double SnapDistance = 32;
        var left = position.X;
        var top = position.Y;

        if (Math.Abs(left - workArea.Left) < SnapDistance)
        {
            left = workArea.Left + Margin;
        }
        else if (Math.Abs(workArea.Right - (left + orbSize.Width)) < SnapDistance)
        {
            left = workArea.Right - orbSize.Width - Margin;
        }

        if (Math.Abs(top - workArea.Top) < SnapDistance)
        {
            top = workArea.Top + Margin;
        }
        else if (Math.Abs(workArea.Bottom - (top + orbSize.Height)) < SnapDistance)
        {
            top = workArea.Bottom - orbSize.Height - Margin;
        }

        // Nunca fuera de pantalla ni a medias entre dos: se recorta siempre, aunque no haya imantado.
        return new Point(
            Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - orbSize.Width)),
            Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - orbSize.Height)));
    }

    private static bool Contains(Rect workArea, Point position, Size orbSize) =>
        position.X >= workArea.Left - 1 &&
        position.Y >= workArea.Top - 1 &&
        position.X + orbSize.Width <= workArea.Right + 1 &&
        position.Y + orbSize.Height <= workArea.Bottom + 1;

    private static string KeyFor(System.Windows.Forms.Screen screen) =>
        $"{screen.DeviceName}@{screen.Bounds.Width}x{screen.Bounds.Height}";

    private static Rect ToRect(System.Drawing.Rectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _slots = JsonSerializer.Deserialize<Dictionary<string, Point>>(File.ReadAllText(_path))
                    ?? new Dictionary<string, Point>(StringComparer.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _slots = new Dictionary<string, Point>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_slots));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Recordar la posición es comodidad: si no se puede escribir, el orbe sigue funcionando.
        }
    }
}
