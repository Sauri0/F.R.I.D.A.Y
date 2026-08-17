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

    private static Dictionary<string, string> Discover()
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);

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
                    var name = Normalize(Path.GetFileNameWithoutExtension(shortcut));

                    // Los desinstaladores comparten prefijo con la app y no son lo que nadie pide.
                    if (name.Length < 2 ||
                        name.StartsWith("uninstall", StringComparison.Ordinal) ||
                        name.StartsWith("desinstalar", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    catalog.TryAdd(name, shortcut);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Una carpeta ilegible no puede impedir descubrir el resto.
            }
        }

        return catalog;
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
