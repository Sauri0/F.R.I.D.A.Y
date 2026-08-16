using System.IO;
using System.Text.Json;
using System.Windows;

namespace Viernes.App.Services;

internal sealed class WindowPlacementStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "window.json");

    public void Restore(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        var fallbackLeft = workArea.Right - window.Width - 24;
        var fallbackTop = workArea.Bottom - window.Height - 24;

        try
        {
            if (!File.Exists(_settingsPath))
            {
                window.Left = fallbackLeft;
                window.Top = fallbackTop;
                return;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_settingsPath));
            if (placement is null || !IsVisibleOnAnyScreen(placement.Left, placement.Top, window.Width, window.Height))
            {
                window.Left = fallbackLeft;
                window.Top = fallbackTop;
                return;
            }

            window.Left = placement.Left;
            window.Top = placement.Top;
        }
        catch (IOException)
        {
            window.Left = fallbackLeft;
            window.Top = fallbackTop;
        }
        catch (JsonException)
        {
            window.Left = fallbackLeft;
            window.Top = fallbackTop;
        }
    }

    public void Save(Window window, double? anchorLeft = null, double? anchorTop = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(new WindowPlacement(
                anchorLeft ?? window.Left,
                anchorTop ?? window.Top));
            File.WriteAllText(_settingsPath, payload);
        }
        catch (IOException)
        {
            // Window placement is optional and must never prevent shutdown.
        }
        catch (UnauthorizedAccessException)
        {
            // A locked-down profile can still use the app without persistence.
        }
    }

    private static bool IsVisibleOnAnyScreen(double left, double top, double width, double height)
    {
        var proposed = new System.Drawing.Rectangle(
            (int)left,
            (int)top,
            Math.Max(1, (int)width),
            Math.Max(1, (int)height));

        return System.Windows.Forms.Screen.AllScreens.Any(screen =>
            System.Drawing.Rectangle.Intersect(screen.WorkingArea, proposed).Width >= Math.Min(40, proposed.Width) &&
            System.Drawing.Rectangle.Intersect(screen.WorkingArea, proposed).Height >= Math.Min(40, proposed.Height));
    }

    private sealed record WindowPlacement(double Left, double Top);
}
