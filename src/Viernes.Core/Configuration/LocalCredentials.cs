using System.Text.Json;

namespace Viernes.Core.Configuration;

/// <summary>
/// Las credenciales que el usuario dejó en su equipo.
/// </summary>
/// <remarks>
/// La de OpenRouter vive en las variables de entorno de la cuenta de Windows y así se queda: es más
/// difícil de filtrar por accidente que un archivo. La de Google entra por archivo porque el usuario
/// lo pidió así —abrirlo, pegar y guardar es más simple que aprender <c>setx</c>—, y el archivo vive
/// fuera del repositorio, en la carpeta de datos, donde ningún <c>git add</c> lo alcanza.
/// <para>
/// Se lee una vez y se cachea: releer un archivo en cada frase que se dice sería trabajo de disco
/// en el camino más sensible a la latencia que tiene el programa.
/// </para>
/// </remarks>
public static class LocalCredentials
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, string>? _cache;

    /// <summary>Dónde vive el archivo de claves.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "claves.json");

    /// <summary>
    /// Devuelve una credencial: primero la del archivo, después la del entorno.
    /// </summary>
    /// <remarks>
    /// El archivo gana, y no es lo que uno haría por costumbre. La razón es concreta: el archivo es
    /// donde el usuario acaba de pegar la clave a propósito, hace un minuto, en un archivo hecho
    /// para eso. El entorno puede tener una credencial vieja de hace meses que nadie recuerda haber
    /// puesto —y así pasó: había una <c>AIza…</c> vencida que le ganaba a la nueva y devolvía 400,
    /// con la clave correcta ahí al lado sin usarse—.
    /// <para>
    /// Entre dos configuraciones que se contradicen, gana la más reciente y la más deliberada. El
    /// entorno queda como respaldo para quien prefiera no tener claves en archivos.
    /// </para>
    /// </remarks>
    public static string? Get(string name)
    {
        string? fromFile;
        lock (Gate)
        {
            _cache ??= Load();
            fromFile = _cache.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        if (fromFile is not null)
        {
            return fromFile;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment.Trim();
    }

    /// <summary>
    /// Dice si la misma credencial está en los dos lugares con valores distintos.
    /// </summary>
    /// <remarks>
    /// Para poder avisarlo en vez de que quede una clave sin usar que el usuario cree activa. Nunca
    /// devuelve ni compara los valores en claro más allá de saber si difieren.
    /// </remarks>
    public static bool IsShadowed(string name)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return false;
        }

        lock (Gate)
        {
            _cache ??= Load();
            return _cache.TryGetValue(name, out var value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, fromEnvironment.Trim(), StringComparison.Ordinal);
        }
    }

    /// <summary>Vuelve a leer el archivo. Para cuando el usuario acaba de pegar una clave.</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    private static Dictionary<string, string> Load()
    {
        var claves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(FilePath))
        {
            return claves;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return claves;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Las claves que empiezan con guión bajo son las notas para el usuario que trae el
                // archivo: no son credenciales y no tienen por qué llegar a nadie.
                if (property.NameEquals(string.Empty) ||
                    property.Name.StartsWith('_') ||
                    property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    claves[property.Name] = value.Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // Un archivo roto no puede impedir arrancar: se sigue sin esa credencial, y quien la
            // necesite va a decir que falta en vez de fallar de forma incomprensible.
        }

        return claves;
    }
}
