using System.Diagnostics;
using Viernes.Core.Tools;

namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Ejecuta el puñado de acciones de PC que Viernes tiene permitidas. Todo lo que hace es abrir algo:
/// no borra, no cierra, no eleva, no cambia configuración y no ejecuta comandos arbitrarios.
/// </summary>
/// <remarks>
/// La lista de aplicaciones es una allowlist cerrada y escrita a mano. El destino que llega desde el
/// modelo <b>nunca</b> se usa como ruta ni como línea de comandos: sólo se busca en esta tabla, y si
/// no está, no pasa nada. Ese es el punto: el modelo elige de un menú, no escribe la orden.
/// </remarks>
public sealed class WindowsPcActionExecutor : IPcActionExecutor
{
    /// <summary>Páginas de Configuración permitidas, por su URI oficial <c>ms-settings:</c>.</summary>
    private static readonly Dictionary<string, (string Uri, string Label)> SettingsPages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sonido"] = ("ms-settings:sound", "Sonido"),
            ["audio"] = ("ms-settings:sound", "Sonido"),
            ["pantalla"] = ("ms-settings:display", "Pantalla"),
            ["bluetooth"] = ("ms-settings:bluetooth", "Bluetooth"),
            ["red"] = ("ms-settings:network", "Red e internet"),
            ["wifi"] = ("ms-settings:network-wifi", "Wi-Fi"),
            ["bateria"] = ("ms-settings:batterysaver", "Batería"),
            ["batería"] = ("ms-settings:batterysaver", "Batería"),
            ["micrófono"] = ("ms-settings:privacy-microphone", "Privacidad del micrófono"),
            ["microfono"] = ("ms-settings:privacy-microphone", "Privacidad del micrófono"),
            ["privacidad"] = ("ms-settings:privacy", "Privacidad"),
            ["aplicaciones"] = ("ms-settings:appsfeatures", "Aplicaciones instaladas"),
            ["notificaciones"] = ("ms-settings:notifications", "Notificaciones"),
            ["inicio"] = ("ms-settings:startupapps", "Aplicaciones de inicio")
        };

    /// <summary>
    /// Aplicaciones permitidas. Se abren por nombre de ejecutable resuelto por Windows, no por ruta
    /// suministrada desde afuera.
    /// </summary>
    private static readonly Dictionary<string, (string Command, string Label)> Applications =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["calculadora"] = ("calc.exe", "Calculadora"),
            ["bloc de notas"] = ("notepad.exe", "Bloc de notas"),
            ["notepad"] = ("notepad.exe", "Bloc de notas"),
            ["explorador"] = ("explorer.exe", "Explorador de archivos"),
            ["archivos"] = ("explorer.exe", "Explorador de archivos"),
            ["terminal"] = ("wt.exe", "Terminal"),
            ["configuración"] = ("ms-settings:", "Configuración"),
            ["configuracion"] = ("ms-settings:", "Configuración")
        };

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "open_settings",
        "open_application",
        "show_desktop"
    };

    private readonly InstalledApplications _installed = new();

    public IReadOnlySet<string> SupportedActions { get; } = Supported;

    /// <summary>Nombres instalados, para que la herramienta le diga al modelo qué puede abrir.</summary>
    public IReadOnlyCollection<string> InstalledApplicationNames => _installed.Names;

    public Task<PcActionOutcome> ExecuteAsync(
        string action,
        string? target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(action.ToLowerInvariant() switch
        {
            "open_settings" => OpenSettings(target),
            "open_application" => OpenApplication(target),
            "show_desktop" => ShowDesktop(),
            _ => new PcActionOutcome(false, "Esa acción no está habilitada.")
        });
    }

    private static PcActionOutcome OpenSettings(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Launch("ms-settings:", "Abrí Configuración.");
        }

        if (!SettingsPages.TryGetValue(target.Trim(), out var page))
        {
            var available = string.Join(", ", SettingsPages.Values.Select(item => item.Label).Distinct());
            return new PcActionOutcome(
                false,
                $"No tengo habilitada esa página de Configuración. Puedo abrir: {available}.");
        }

        return Launch(page.Uri, $"Abrí Configuración en {page.Label}.");
    }

    private PcActionOutcome OpenApplication(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new PcActionOutcome(false, "Necesito saber qué aplicación abrir.");
        }

        var name = target.Trim();

        // Primero el puñado escrito a mano, que cubre las de sistema sin acceso directo.
        if (Applications.TryGetValue(name, out var known))
        {
            return Launch(known.Command, $"Abrí {known.Label}.");
        }

        // Después, cualquier cosa instalada en el menú Inicio. El modelo elige del catálogo real;
        // nunca compone una ruta, así que un texto leído de la web no se vuelve un ejecutable.
        var shortcut = _installed.Resolve(name);
        return shortcut is null
            ? new PcActionOutcome(false, $"No encontré ninguna aplicación instalada que se llame «{name}».")
            : Launch(shortcut, $"Abrí {System.IO.Path.GetFileNameWithoutExtension(shortcut)}.");
    }

    /// <summary>Mostrar el escritorio se hace por el Shell de Windows, sin simular teclas.</summary>
    private static PcActionOutcome ShowDesktop()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return new PcActionOutcome(false, "Windows no expuso el Shell para minimizar todo.");
            }

            var shell = Activator.CreateInstance(shellType);
            shellType.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
            return new PcActionOutcome(true, "Mostré el escritorio.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PcActionOutcome(false, "No pude mostrar el escritorio.");
        }
    }

    private static PcActionOutcome Launch(string command, string successMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = true
            });
            return new PcActionOutcome(true, successMessage);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PcActionOutcome(false, "Windows no dejó abrir eso.");
        }
    }
}
