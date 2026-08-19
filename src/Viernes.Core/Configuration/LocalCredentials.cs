using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// La carpeta donde vive el archivo. <b>Sólo las pruebas la cambian.</b>
    /// </summary>
    /// <remarks>
    /// Existe porque <c>Environment.GetFolderPath</c> le pregunta a Windows y no mira la
    /// variable <c>LOCALAPPDATA</c>: desde una prueba no hay forma de redirigirla. Sin esta costura,
    /// probar que guardar una clave conserva el resto del archivo obliga a escribir en el archivo
    /// real de quien está compilando, o a saltearse la prueba —que fue lo que pasó: ocho pruebas en
    /// verde que no corrían ninguna línea—.
    /// </remarks>
    internal static string? DirectoryOverride { get; set; }

    /// <summary>Dónde vive el archivo de claves.</summary>
    public static string FilePath => Path.Combine(
        DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes"),
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

    /// <summary>
    /// Deja una credencial en el archivo, conservando todo lo demás que haya adentro.
    /// </summary>
    /// <remarks>
    /// Escribe el archivo entero porque es la única forma de cambiar una clave de un JSON, pero
    /// <b>lo reconstruye a partir de lo que había</b>: las otras credenciales, y también las notas
    /// que empiezan con guión bajo, que son las instrucciones que el archivo trae para el usuario y
    /// que se perderían si se escribiera sólo el diccionario cacheado —el caché las descarta a
    /// propósito—.
    /// <para>
    /// Un valor vacío borra la credencial en vez de guardar una cadena vacía: «no tengo clave» y
    /// «tengo una clave que es la nada» son estados distintos y el segundo no sirve para nada.
    /// </para>
    /// <para>
    /// El valor no aparece en ningún mensaje de error. Si esto falla, lo que se devuelve dice qué
    /// pasó con el archivo y nunca qué se estaba escribiendo.
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> si se guardó; si no, el motivo, sin la clave adentro.</returns>
    public static string? Set(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var contenido = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

            if (File.Exists(FilePath))
            {
                // Lo que hay se conserva; lo que no se puede leer no puede impedir guardar.
                //
                // Antes, un claves.json roto —editado a mano y con una coma de más— hacía que esto
                // devolviera un error, así que la única persona que de verdad necesitaba la ventana
                // de claves era justamente la que no podía usarla. Ahora el archivo ilegible se
                // guarda a un costado y se empieza uno nuevo: no se pierde nada y se puede seguir.
                var texto = File.ReadAllText(FilePath);
                if (!string.IsNullOrWhiteSpace(texto))
                {
                    try
                    {
                        if (JsonNode.Parse(texto) is JsonObject anterior)
                        {
                            foreach (var par in anterior)
                            {
                                contenido[par.Key] = par.Value?.DeepClone();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        GuardarAUnCostado(texto);
                    }
                }
            }

            var limpio = value?.Trim();
            if (string.IsNullOrEmpty(limpio))
            {
                contenido.Remove(name);
            }
            else
            {
                contenido[name] = JsonValue.Create(limpio);
            }

            var salida = new JsonObject();
            foreach (var par in contenido)
            {
                salida[par.Key] = par.Value;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Se escribe al lado y se cambia de lugar: si el proceso muere a mitad, el archivo
            // anterior sigue entero en vez de quedar truncado y sin ninguna de las dos claves.
            var temporal = FilePath + ".nuevo";
            File.WriteAllText(temporal, salida.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporal, FilePath, overwrite: true);

            Reload();
            return null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return $"No se pudo escribir el archivo de claves: {exception.Message}";
        }
    }

    /// <summary>
    /// Deja una copia del archivo ilegible al lado antes de empezar uno nuevo.
    /// </summary>
    /// <remarks>
    /// Adentro puede haber una clave que el usuario pegó y que nadie más tiene. Que el archivo no se
    /// pueda interpretar no lo hace desechable.
    /// </remarks>
    private static void GuardarAUnCostado(string texto)
    {
        try
        {
            var destino = $"{FilePath}.roto-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (!File.Exists(destino))
            {
                File.WriteAllText(destino, texto);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Si esa credencial está puesta. <b>Nunca devuelve el valor.</b></summary>
    public static bool Has(string name) => !string.IsNullOrWhiteSpace(Get(name));

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
