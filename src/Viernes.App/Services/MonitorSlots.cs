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
/// Recuerda dónde va el orbe <b>en cada monitor</b>, para cuando vuelve a ése.
/// </summary>
/// <remarks>
/// <b>Esto no decide cuándo mudarse</b>, y antes el comentario de acá decía que sí: «aparece donde
/// está el cursor». Eso valía cuando el orbe seguía al mouse solo, que es la queja que originó la
/// preferencia <c>FollowActiveMonitor</c>. Ahora quien decide es <c>MainWindow</c>, y por omisión
/// sólo se muda cuando el usuario actúa: cuando lo llama —ahí el cursor sí es la mejor aproximación
/// disponible a dónde está mirando, porque acaba de hablarle— y cuando lo arrastra.
/// <para>
/// Lo que sigue valiendo es por qué existe este archivo: si al volver a una pantalla el orbe
/// apareciera siempre en el mismo rincón, mudarse tendría costo y volver no sería volver.
/// </para>
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

    /// <summary>
    /// La clave de slot de un monitor: su nombre de dispositivo y su resolución.
    /// </summary>
    /// <remarks>
    /// Acá vivían además <c>MonitorUnderCursor()</c> y <c>MonitorAt(Point)</c>, que devolvían el
    /// área útil en <b>píxeles físicos</b> y, la segunda, recibía un punto en unidades de WPF y se
    /// lo pasaba tal cual a <c>Screen.FromPoint</c>. Las dos escalas mezcladas en la misma línea: en
    /// un escritorio al 100 % da igual y al 150 % contesta el monitor equivocado. Se fueron las dos.
    /// Ahora quien tiene que resolver «qué monitor es éste» convierte primero —la ventana es la
    /// única que sabe con qué escala interpreta WPF su <c>Left</c>— y después pregunta acá nada más
    /// que por la clave, que es lo único de este archivo que no tiene unidades.
    /// </remarks>
    internal static string KeyFor(System.Windows.Forms.Screen screen) =>
        $"{screen.DeviceName}@{screen.Bounds.Width}x{screen.Bounds.Height}";

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

    // Acá había un Magnetize(position, workArea, orbSize) con su propia distancia de imantado de 32
    // px y su propio margen. No lo llamaba nadie: el imán del orbe es OrbMotion.Magnetize, con
    // MagnetRange = 58, y corre dentro del bucle de física. Dos imanes con dos números distintos
    // para la misma cosa es la clase de cosa que después alguien "arregla" tocando el que no se usa.

    private static bool Contains(Rect workArea, Point position, Size orbSize) =>
        position.X >= workArea.Left - 1 &&
        position.Y >= workArea.Top - 1 &&
        position.X + orbSize.Width <= workArea.Right + 1 &&
        position.Y + orbSize.Height <= workArea.Bottom + 1;

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
