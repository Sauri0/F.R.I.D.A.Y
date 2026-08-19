using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Viernes.App.Controls;
using Viernes.App.ViewModels;

// El proyecto referencia WPF y WinForms a la vez: los alias evitan la ambigüedad de nombres.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Renderiza la gota a una hoja de contactos PNG sin abrir nada en pantalla. Existe para poder
/// juzgar la forma real que dibuja WPF en vez de ajustarla a ciegas.
/// </summary>
/// <remarks>
/// Se invoca con <c>Viernes.exe --render-orb &lt;carpeta&gt;</c> y termina el proceso al guardar.
/// No toca preferencias, ni voz, ni red.
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

    public static async Task<string> RunAsync(string outputDirectory, OrbShape shape = OrbShape.Gota)
    {
        Directory.CreateDirectory(outputDirectory);

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

        var gota = shape == OrbShape.Gota ? new LiquidOrb { Width = Cell, Height = Cell } : null;
        var nube = shape == OrbShape.Nube ? new NubeOrb { Width = Cell, Height = Cell } : null;
        FrameworkElement orb = (FrameworkElement?)gota ?? nube!;
        host.Content = orb;
        host.Show();

        var shots = new List<(BitmapSource Image, string Caption)>();
        var index = 0;
        foreach (var (state, seconds) in Frames)
        {
            // Los dos cuerpos cambian de dibujo con el escritorio, así que los dos tienen que
            // enterarse de en qué fila caen.
            if (gota is not null)
            {
                gota.State = state;
                gota.IsLightDesktop = IsLightRow(index);
            }

            if (nube is not null)
            {
                nube.State = state;
                nube.IsLightDesktop = IsLightRow(index);
            }

            index++;

            // El reloj de las animaciones corre en tiempo real, así que se espera de verdad.
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            host.UpdateLayout();

            var target = new RenderTargetBitmap(Cell * Zoom, Cell * Zoom, Dpi, Dpi, PixelFormats.Pbgra32);
            target.Render(orb);
            target.Freeze();
            // El nombre de la tabla y no el del enum: la hoja se mira para decidir si dos estados se
            // distinguen, y para eso hace falta leer «un proyecto te espera», no «ProjectWaiting».
            shots.Add((target, $"{OrbPalette.For(state).Name} · {seconds:0.0}s"));
        }

        host.Close();

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
