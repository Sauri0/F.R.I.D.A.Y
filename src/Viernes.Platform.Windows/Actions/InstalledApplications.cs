using System.IO;

namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Descubre las aplicaciones del menú Inicio del usuario y de la máquina.
/// </summary>
/// <remarks>
/// Es el punto medio entre una lista escrita a mano —que nunca tiene la app que querés— y ejecutar
/// lo que sea. El modelo elige de un catálogo real de lo que tenés instalado: nunca compone una
/// ruta ni una línea de comandos, así que un texto malicioso leído de la web no puede convertirse
/// en un ejecutable arbitrario.
/// </remarks>
public sealed class InstalledApplications
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _catalog;

    public InstalledApplications() => _catalog = new Lazy<IReadOnlyDictionary<string, string>>(Discover);

    /// <summary>
    /// Construye el catálogo por adelantado, en segundo plano.
    /// </summary>
    /// <remarks>
    /// Enumerar <c>AppsFolder</c> cuesta ~580 ms medidos, y sin esto los pagaba la primera acción
    /// del usuario —justo la que ya venía detrás de transcribir y pensar—. Al arrancar sobra tiempo
    /// y no hay nadie esperando; es el momento correcto para pagarlos.
    /// </remarks>
    public void Warm() => Task.Run(() =>
    {
        try
        {
            _ = _catalog.Value;
        }
        catch (Exception)
        {
            // Precalentar es una optimización: si falla, la primera resolución lo vuelve a intentar.
        }
    });

    public IReadOnlyCollection<string> Names => _catalog.Value.Keys.ToArray();

    /// <summary>
    /// Resuelve un nombre hablado al acceso directo instalado. Tolera acentos y coincidencias
    /// parciales porque nadie dice el nombre exacto del ejecutable en voz alta.
    /// </summary>
    public string? Resolve(string spokenName)
    {
        if (string.IsNullOrWhiteSpace(spokenName))
        {
            return null;
        }

        var needle = Normalize(spokenName);
        var catalog = _catalog.Value;

        if (catalog.TryGetValue(needle, out var exact))
        {
            return exact;
        }

        // Primero lo que empieza igual, después lo que lo contiene: «word» antes que «wordpad».
        var byPrefix = catalog
            .Where(entry => entry.Key.StartsWith(needle, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key.Length)
            .Select(entry => entry.Value)
            .FirstOrDefault();
        if (byPrefix is not null)
        {
            return byPrefix;
        }

        return catalog
            .Where(entry => entry.Key.Contains(needle, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key.Length)
            .Select(entry => entry.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Distingue lo que se puede arrancar como archivo de lo que hay que lanzar por el shell.
    /// </summary>
    /// <remarks>
    /// No se adivina por la forma del texto, porque no hay una sola forma. <c>AppsFolder</c> mezcla
    /// rutas reales (<c>C:\…\stremio.exe</c>) con identificadores de aplicación de al menos tres
    /// pintas distintas: <c>SpotifyAB.SpotifyMusic_…!Spotify</c> para la Store,
    /// <c>com.squirrel.Discord.Discord</c> heredados, <c>Chrome</c> a secas, y hasta
    /// <c>{GUID}\Steam\Steam.exe</c>, que parece una ruta y no lo es. La única pregunta que responde
    /// bien a todos los casos es la más simple: ¿existe ese archivo?
    /// </remarks>
    public static bool IsLaunchableFile(string target) =>
        Path.IsPathRooted(target) && File.Exists(target);

    private static Dictionary<string, string> Discover()
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);

        // Primero el catálogo real de Windows. Escanear accesos directos deja afuera todo lo que
        // venga de la Store —Spotify, WhatsApp, Netflix—, porque esas aplicaciones no instalan
        // ningún .lnk: viven en el espacio de nombres AppsFolder y en ningún lado más.
        foreach (var (name, target) in DiscoverFromAppsFolder())
        {
            AddCandidate(catalog, name, target);
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    AddCandidate(catalog, Path.GetFileNameWithoutExtension(shortcut), shortcut);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Una carpeta ilegible no puede impedir descubrir el resto.
            }
        }

        return catalog;
    }

    private static void AddCandidate(Dictionary<string, string> catalog, string displayName, string target)
    {
        var name = Normalize(displayName);

        // Los desinstaladores comparten prefijo con la app y no son lo que nadie pide.
        if (name.Length < 2 ||
            name.StartsWith("uninstall", StringComparison.Ordinal) ||
            name.StartsWith("desinstalar", StringComparison.Ordinal))
        {
            return;
        }

        catalog.TryAdd(name, target);
    }

    /// <summary>
    /// Enumera <c>shell:AppsFolder</c>, que es exactamente lo que muestra el menú Inicio: aplicaciones
    /// de escritorio y de la Store en una sola lista, cada una con el identificador que Windows usa
    /// para lanzarla. Se hace por enlace tardío para no tomar una dependencia de interoperabilidad.
    /// </summary>
    private static List<(string Name, string Target)> DiscoverFromAppsFolder()
    {
        var found = new List<(string, string)>();

        // El shell de Windows exige apartamento STA. La caché se construye por demanda desde
        // cualquier hilo, así que la enumeración se corre en uno propio en vez de confiar en cuál
        // tocó: en un hilo MTA la llamada falla y el catálogo quedaría vacío sin decir por qué.
        var worker = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null)
                {
                    return;
                }

                var shell = Activator.CreateInstance(shellType);
                var folder = Invoke(shell, "NameSpace", "shell:AppsFolder");
                var items = Invoke(folder, "Items");
                if (items is null || Invoke(items, "Count") is not int count)
                {
                    return;
                }

                for (var index = 0; index < count; index++)
                {
                    var item = Invoke(items, "Item", index);
                    if (Invoke(item, "Name") is string name &&
                        Invoke(item, "Path") is string target &&
                        !string.IsNullOrWhiteSpace(name) &&
                        !string.IsNullOrWhiteSpace(target))
                    {
                        found.Add((name, target));
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Si el shell no coopera queda el escaneo de accesos directos, que cubre lo clásico.
            }
        });

        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        // El techo evita que un shell trabado deje colgada la primera acción del usuario.
        worker.Join(TimeSpan.FromSeconds(10));
        return found;
    }

    private static object? Invoke(object? target, string member, params object[] arguments)
    {
        if (target is null)
        {
            return null;
        }

        return target.GetType().InvokeMember(
            member,
            System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty,
            binder: null,
            target,
            arguments);
    }

    /// <summary>Minúsculas sin acentos: «Configuración» y «configuracion» tienen que coincidir.</summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
