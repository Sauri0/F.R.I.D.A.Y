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

    private static readonly (AssistantVisualState State, double Seconds, bool Microphone)[] Frames =
    [
        (AssistantVisualState.Idle, 0.4, false),
        (AssistantVisualState.Idle, 1.7, false),
        (AssistantVisualState.Idle, 3.1, false),
        (AssistantVisualState.Idle, 1.0, true),
        (AssistantVisualState.Listening, 1.2, true),
        (AssistantVisualState.Thinking, 1.2, false),
        (AssistantVisualState.Speaking, 1.2, false),
        (AssistantVisualState.Attention, 1.2, false)
    ];

    public static async Task<string> RunAsync(string outputDirectory)
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

        var orb = new LiquidOrb { Width = Cell, Height = Cell };
        host.Content = orb;
        host.Show();

        var shots = new List<(BitmapSource Image, string Caption)>();
        foreach (var (state, seconds, microphone) in Frames)
        {
            orb.State = state;
            orb.IsMicrophoneActive = microphone;

            // El reloj de las animaciones corre en tiempo real, así que se espera de verdad.
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            host.UpdateLayout();

            var target = new RenderTargetBitmap(Cell * Zoom, Cell * Zoom, Dpi, Dpi, PixelFormats.Pbgra32);
            target.Render(orb);
            target.Freeze();
            shots.Add((target, $"{state}{(microphone ? " · mic" : string.Empty)} · {seconds:0.0}s"));
        }

        host.Close();

        var sheetPath = Path.Combine(outputDirectory, "orb-contact-sheet.png");
        SaveContactSheet(shots, sheetPath);
        return sheetPath;
    }

    private static void SaveContactSheet(
        IReadOnlyList<(BitmapSource Image, string Caption)> shots,
        string path)
    {
        const int columns = 4;
        var rows = (int)Math.Ceiling(shots.Count / (double)columns);
        var cell = Cell * Zoom;
        const int label = 28;
        var width = columns * cell;
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
                var column = index % columns;
                var row = index / columns;
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
