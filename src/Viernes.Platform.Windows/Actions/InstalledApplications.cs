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
    /// <summary>Una aplicación del catálogo: cómo se llama en pantalla y cómo se lanza.</summary>
    /// <remarks>
    /// El nombre para mostrar se guarda aparte del normalizado porque el catálogo se indexa sin
    /// acentos y en minúsculas: proponerle al usuario «visual studio code» cuando la aplicación se
    /// llama «Visual Studio Code» hace que la sugerencia parezca inventada.
    /// </remarks>
    private sealed record CatalogEntry(string DisplayName, string Target);

    private readonly Lazy<IReadOnlyDictionary<string, CatalogEntry>> _catalog;

    public InstalledApplications() => _catalog = new Lazy<IReadOnlyDictionary<string, CatalogEntry>>(Discover);

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

    /// <summary>Nombres tal como se ven en el menú Inicio, en orden alfabético.</summary>
    public IReadOnlyCollection<string> Names => _catalog.Value.Values
        .Select(entry => entry.DisplayName)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

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
            return exact.Target;
        }

        // Primero lo que empieza igual, después lo que lo contiene: «word» antes que «wordpad».
        var byPrefix = catalog
            .Where(entry => entry.Key.StartsWith(needle, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key.Length)
            .Select(entry => entry.Value.Target)
            .FirstOrDefault();
        if (byPrefix is not null)
        {
            return byPrefix;
        }

        return catalog
            .Where(entry => entry.Key.Contains(needle, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key.Length)
            .Select(entry => entry.Value.Target)
            .FirstOrDefault();
    }

    /// <summary>
    /// Nombres instalados parecidos a uno que no se pudo resolver.
    /// </summary>
    /// <remarks>
    /// Existe porque «no encontré ninguna aplicación que se llame X» es una respuesta que no lleva a
    /// ningún lado: el modelo no ve el catálogo, así que reintenta con otra invención. Ofrecerle los
    /// nombres reales más parecidos convierte el fracaso en el dato que le faltaba. Se mezclan dos
    /// criterios porque fallan en casos distintos: la distancia de edición atrapa el error de tipeo
    /// —«escel» por «Excel»— y la coincidencia por palabra atrapa el nombre incompleto o cambiado de
    /// orden —«code» por «Visual Studio Code»—.
    /// </remarks>
    public IReadOnlyList<string> Suggest(string spokenName, int maximum = 6)
    {
        if (string.IsNullOrWhiteSpace(spokenName) || maximum <= 0)
        {
            return [];
        }

        var needle = Normalize(spokenName);
        var words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return _catalog.Value
            .Select(entry => (entry.Value.DisplayName, Score: Similarity(needle, words, entry.Key)))
            .Where(candidate => candidate.Score > 0.45)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayName.Length)
            .Take(maximum)
            .Select(candidate => candidate.DisplayName)
            .ToArray();
    }

    private static double Similarity(string needle, string[] needleWords, string candidate)
    {
        if (candidate.Contains(needle, StringComparison.Ordinal))
        {
            return 1;
        }

        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = needleWords.Count(word =>
            word.Length >= 3 &&
            candidateWords.Any(other => other.StartsWith(word, StringComparison.Ordinal)));
        var byWord = needleWords.Length == 0 ? 0 : (double)matched / needleWords.Length;

        var longest = Math.Max(needle.Length, candidate.Length);
        var byEdit = longest == 0 ? 0 : 1 - ((double)EditDistance(needle, candidate) / longest);

        return Math.Max(byWord, byEdit);
    }

    /// <summary>
    /// Distancia de Levenshtein, con dos filas en vez de la matriz entera.
    /// </summary>
    /// <remarks>
    /// Se recorre el catálogo completo —varios cientos de aplicaciones— y sólo cuando algo ya falló,
    /// así que el costo no se nota; guardar la matriz sí se notaría en memoria y no aporta nada,
    /// porque acá sólo interesa el número final y no el camino.
    /// </remarks>
    private static int EditDistance(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return Math.Max(left.Length, right.Length);
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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

    private static Dictionary<string, CatalogEntry> Discover()
    {
        var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

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

    private static void AddCandidate(Dictionary<string, CatalogEntry> catalog, string displayName, string target)
    {
        var name = Normalize(displayName);

        // Los desinstaladores comparten prefijo con la app y no son lo que nadie pide.
        if (name.Length < 2 ||
            name.StartsWith("uninstall", StringComparison.Ordinal) ||
            name.StartsWith("desinstalar", StringComparison.Ordinal))
        {
            return;
        }

        catalog.TryAdd(name, new CatalogEntry(displayName.Trim(), target));
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
