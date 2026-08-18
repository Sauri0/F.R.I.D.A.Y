using System.Text;
using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Acceso real al disco: leer, escribir, crear, listar, mover, buscar y borrar.
/// </summary>
/// <remarks>
/// Sin esto el asistente podía abrir aplicaciones pero no producir nada. Escribir un archivo es la
/// diferencia entre uno que comenta y uno que entrega.
/// <para>
/// Borrar <b>nunca destruye</b>: mueve a una papelera propia dentro de la carpeta de Viernes, y el
/// deshacer puede traerlo de vuelta. No es paternalismo, es la única forma de que dar acceso total
/// sea una decisión reversible: un borrado real convierte cualquier malentendido —del modelo o del
/// dictado— en pérdida definitiva.
/// </para>
/// </remarks>
public sealed class FileSystemTool : IAssistantTool
{
    public const string ToolName = "archivo";

    /// <summary>Techo de lectura. Volcar un archivo enorme al contexto lo agota sin dar nada útil.</summary>
    private const int MaximumReadBytes = 120_000;

    private const int MaximumListed = 200;

    /// <summary>Cuántos resultados de búsqueda se muestran; el total informado es el real.</summary>
    private const int MaximumShownInSearch = 40;

    /// <summary>
    /// Tope de coincidencias que se cuentan antes de contestar «más de N».
    /// </summary>
    /// <remarks>
    /// Contar el total exige recorrer todo el árbol. Bajo una carpeta enorme eso puede tardar
    /// minutos, y una respuesta lenta es tan inútil como una mentirosa: pasado este tope se dice
    /// «más de N» —que es cierto— en vez de seguir contando.
    /// </remarks>
    private const int MaximumCountedInSearch = 5_000;

    /// <summary>Cuántos bytes se miran para decidir si un archivo es binario.</summary>
    private const int BinarySniffBytes = 4_096;

    /// <summary>Días que algo sobrevive en la papelera propia antes de que se lo pode.</summary>
    private const int RecycleRetentionDays = 30;

    private const string RecycleStampFormat = "yyyyMMdd-HHmmss-fff";

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Trabaja con archivos y carpetas del equipo. " +
        "accion=«leer» devuelve el contenido de ruta. " +
        "accion=«carpeta» CREA UNA CARPETA en ruta. Usala para «creame una carpeta»: escribir sirve " +
        "para archivos y si la usás para una carpeta vas a crear un archivo sin extensión. " +
        "accion=«abrir» abre ruta en el explorador si es carpeta, o con su programa si es archivo. " +
        "Usala para «abrí esa carpeta» — NO uses pc_action, que sirve para aplicaciones. " +
        "accion=«escribir» crea o reemplaza el ARCHIVO ruta con contenido —crea las carpetas que falten—. " +
        "accion=«agregar» suma contenido al final. " +
        "accion=«listar» enumera lo que hay en ruta. " +
        "accion=«buscar» busca archivos por nombre bajo ruta, usando contenido como patrón. " +
        "accion=«mover» lleva ruta a destino. accion=«copiar» la duplica en destino. " +
        "accion=«borrar» manda ruta a la papelera propia. " +
        "accion=«recuperar» devuelve algo borrado: sin ruta lista lo que hay, con ruta —sólo el " +
        "nombre— lo restaura al escritorio. " +
        "accion=«existe» dice si ruta está. " +
        "Para las carpetas del usuario escribí la palabra y no una ruta: «escritorio», «documentos», " +
        "«descargas», «imágenes», «música», «videos» — yo las resuelvo a la ruta real de su equipo. " +
        "NUNCA inventes rutas tipo C:\\Users\\Usuario\\Desktop: no sabés cómo se llama su usuario y " +
        "vas a escribir en un lugar que no existe. Para todo lo demás, ruta completa. " +
        "Si no dice dónde, usá «escritorio».",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["accion"] = ToolSchemas.String(
                    "carpeta, abrir, leer, escribir, agregar, listar, buscar, mover, copiar, " +
                    "borrar, recuperar o existe."),
                ["ruta"] = ToolSchemas.String(
                    "Ruta completa del archivo o carpeta. Opcional sólo en recuperar."),
                ["contenido"] = ToolSchemas.String("Lo que se escribe, o el patrón al buscar."),
                ["destino"] = ToolSchemas.String("Ruta de destino al mover o copiar.")
            },
            ["accion"]),
        ToolRiskLevel.Safe);

    /// <summary>Papelera propia: borrar es reversible mientras el archivo siga acá.</summary>
    public static string RecycleRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "papelera");

    private readonly string _recycleRoot;

    /// <summary>
    /// Usa la papelera de siempre, o una propia para probar sin tocar la del usuario.
    /// </summary>
    /// <remarks>
    /// La poda borra definitivamente lo que pasó los treinta días. Una prueba que se apoyara en la
    /// papelera real destruiría archivos verdaderos del usuario al correr la suite: no hoy —hoy
    /// está toda reciente— sino dentro de unos meses, en silencio y sin que nadie relacione una cosa
    /// con la otra. Que la raíz se pueda inyectar es lo que saca esa mina del camino.
    /// </remarks>
    public FileSystemTool(string? recycleRoot = null) =>
        _recycleRoot = recycleRoot ?? RecycleRoot;

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = JsonToolArguments.RequiredString(arguments, "accion", 20).ToLowerInvariant();

        // «recuperá lo que borraste» es un pedido completo sin ruta: la gracia de la papelera es que
        // no tenés que acordarte dónde estaba. Por eso la ruta se lee opcional y se exige después,
        // sólo para las acciones que de verdad la necesitan.
        var rawPath = JsonToolArguments.OptionalString(arguments, "ruta", 400);
        var content = JsonToolArguments.OptionalString(arguments, "contenido", 200_000);
        var destination = JsonToolArguments.OptionalString(arguments, "destino", 400);

        if (action is "recuperar" or "restaurar")
        {
            return Task.FromResult(Restore(context, rawPath));
        }

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return Task.FromResult(Fail(context, "Necesito saber sobre qué archivo o carpeta."));
        }

        var path = Expand(rawPath);

        try
        {
            return Task.FromResult(action switch
            {
                "carpeta" or "crear_carpeta" or "crear" => CreateFolder(context, path),
                "abrir" => Open(context, path),
                "leer" => Read(context, path),
                "escribir" => Write(context, path, content ?? string.Empty, append: false),
                "agregar" => Write(context, path, content ?? string.Empty, append: true),
                "listar" => List(context, path),
                "buscar" => Search(context, path, content),
                "mover" => Transfer(context, path, destination, move: true),
                "copiar" => Transfer(context, path, destination, move: false),
                "borrar" => Recycle(context, path),
                "existe" => Ok(context, File.Exists(path) || Directory.Exists(path)
                    ? $"Sí, «{path}» existe."
                    : $"No hay nada en «{path}»."),
                _ => Fail(context, "No conozco esa acción sobre archivos.")
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(Fail(context, $"Windows no me dio permiso sobre «{path}»."));
        }
        catch (IOException exception)
        {
            return Task.FromResult(Fail(context, $"No pude: {exception.Message}"));
        }
    }

    /// <summary>
    /// Acepta rutas habladas: variables de entorno, <c>~</c>, y nombres sueltos de carpetas conocidas.
    /// </summary>
    /// <remarks>
    /// Hablando nadie dice <c>C:\Users\suNombre\Desktop</c>: dice «el escritorio». Resolverlo acá evita
    /// que el modelo tenga que inventar una ruta, que es donde se equivoca.
    /// </remarks>
    private static string Expand(string raw)
    {
        var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));

        if (path.StartsWith('~'))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('\\', '/'));
        }

        // También se aceptan rutas inventadas con un usuario que no existe: si el modelo escribió
        // C:\Users\Usuario\Desktop, lo que quiso decir es el escritorio de quien está usando el equipo.
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 &&
            segments[0].EndsWith(":", StringComparison.Ordinal) &&
            segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase) &&
            !segments[2].Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(Path.Combine($"{segments[0]}\\", segments[1], segments[2])))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                string.Join('\\', segments.Skip(3)));
        }

        // El nombre hablado también sirve de prefijo: «escritorio\Proyecto» es tan natural como
        // «escritorio» a secas, y si sólo se resolviera el nombre suelto la ruta quedaría relativa
        // —la carpeta terminaría creada junto al ejecutable, en un lugar donde nadie la busca.
        var rest = string.Empty;
        var head = path;
        var cut = path.IndexOfAny(['\\', '/']);
        if (cut > 0)
        {
            head = path[..cut];
            rest = path[(cut + 1)..].TrimStart('\\', '/');
        }

        var known = head.ToLowerInvariant() switch
        {
            "escritorio" or "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documentos" or "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "descargas" or "downloads" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "imagenes" or "imágenes" or "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "musica" or "música" or "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" or "vídeos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };

        if (known is null)
        {
            // Una ruta relativa se crearía junto al ejecutable. Cuando no hay nada mejor, el
            // escritorio es el único lugar donde el usuario va a encontrarla.
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), path);
        }

        return rest.Length == 0 ? known : Path.Combine(known, rest);
    }

    /// <summary>
    /// Crea una carpeta y <b>comprueba que quedó</b> antes de decir que la creó.
    /// </summary>
    /// <remarks>
    /// Faltaba, y su ausencia producía la peor clase de error: sin acción para carpetas, un pedido
    /// de «creame una carpeta» terminaba en <c>escribir</c>, que crea un <em>archivo</em> con ese
    /// nombre y sin extensión. El mensaje decía «Creé C:\…\MiCarpeta» —que se lee igual que si
    /// hubiera creado la carpeta— y recién se descubría el engaño al intentar abrirla.
    /// </remarks>
    private static ToolExecutionResult CreateFolder(ToolExecutionContext context, string path)
    {
        if (File.Exists(path))
        {
            return Fail(context, $"«{path}» ya existe pero es un archivo, no una carpeta.");
        }

        if (Directory.Exists(path))
        {
            return Ok(context, $"La carpeta «{path}» ya existía.");
        }

        Directory.CreateDirectory(path);

        return Directory.Exists(path)
            ? Ok(context, $"Creé la carpeta «{path}».")
            : Fail(context, $"Pedí crear «{path}» pero no aparece. Puede ser un permiso.");
    }

    /// <summary>
    /// Abre una ruta: la carpeta en el explorador, el archivo con su programa.
    /// </summary>
    /// <remarks>
    /// Abrir una carpeta es una operación de archivos, no de aplicaciones. Sin esto, «abrí esa
    /// carpeta» caía en la herramienta de aplicaciones, que busca por nombre en el catálogo del
    /// menú Inicio — y ahí «documentos» resuelve a la biblioteca Documentos en vez de a la carpeta
    /// que se acababa de crear. Abría algo, y no era lo pedido.
    /// </remarks>
    private static ToolExecutionResult Open(ToolExecutionContext context, string path)
    {
        var isFolder = Directory.Exists(path);
        if (!isFolder && !File.Exists(path))
        {
            return Fail(context, $"No encontré «{path}», así que no hay nada que abrir.");
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = isFolder ? "explorer.exe" : path,
                Arguments = isFolder ? $"\"{path}\"" : string.Empty,
                UseShellExecute = true
            });

            return Ok(context, isFolder
                ? $"Abrí la carpeta «{path}»."
                : $"Abrí «{Path.GetFileName(path)}».");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Fail(context, $"Windows no dejó abrir «{path}».");
        }
    }

    private static ToolExecutionResult Read(ToolExecutionContext context, string path)
    {
        if (!File.Exists(path))
        {
            return Fail(context, $"No encontré «{path}».");
        }

        var info = new FileInfo(path);

        // Antes se leía cualquier cosa con ReadAllText sin mirar qué era: un PDF o un .docx entraba
        // como texto, la respuesta salía Succeeded, y el modelo resumía el ruido del binario como si
        // fuera el contenido del documento. Un fallo claro es mucho mejor que un resumen inventado.
        if (LooksBinary(path))
        {
            return Fail(
                context,
                $"«{info.Name}» no es texto: parece un archivo binario (PDF, imagen, Word, un " +
                "ejecutable). Leerlo devolvería ruido. Si querés verlo, pedime accion=abrir.");
        }

        if (info.Length > MaximumReadBytes)
        {
            using var reader = new StreamReader(path);
            var buffer = new char[MaximumReadBytes];
            var read = reader.Read(buffer, 0, MaximumReadBytes);
            return Ok(context,
                $"«{info.Name}» pesa {info.Length / 1024} KB; te leo el principio:\n" +
                new string(buffer, 0, read));
        }

        return Ok(context, File.ReadAllText(path));
    }

    /// <summary>
    /// Decide si un archivo es binario mirando si hay bytes nulos en los primeros KB.
    /// </summary>
    /// <remarks>
    /// No hay forma exacta de saberlo, pero el byte cero no aparece en texto real y sí en casi todo
    /// binario: PDF, imágenes, Office, ejecutables. La excepción es UTF-16, que está lleno de ceros
    /// y sí es texto, así que su BOM se reconoce antes de mirar nada más.
    /// </remarks>
    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[BinarySniffBytes];
            var read = stream.Read(buffer);
            if (read == 0)
            {
                return false;
            }

            var sample = buffer[..read];

            // UTF-16 con marca de orden: es texto aunque tenga ceros de sobra, y ReadAllText lo
            // decodifica bien porque detecta el BOM.
            if (read >= 2 &&
                ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF)))
            {
                return false;
            }

            return sample.IndexOf((byte)0) >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Si no se puede ni mirar, que falle más adelante la lectura de verdad y con su mensaje.
            return false;
        }
    }

    private ToolExecutionResult Write(
        ToolExecutionContext context,
        string path,
        string content,
        bool append)
    {
        // Escribir sobre algo que ya es una carpeta no puede terminar en un archivo homónimo.
        if (Directory.Exists(path))
        {
            return Fail(context, $"«{path}» es una carpeta. Para escribir dentro, dame el nombre del archivo.");
        }

        // Un destino sin extensión y sin contenido es, casi seguro, una carpeta mal pedida. Antes
        // esto creaba un archivo sin extensión y contestaba «Creé …», que se lee como si hubiera
        // creado la carpeta: el usuario sólo descubría el problema al intentar abrirla.
        if (string.IsNullOrEmpty(Path.GetExtension(path)) && string.IsNullOrWhiteSpace(content))
        {
            return Fail(
                context,
                $"«{path}» no tiene extensión y no me diste contenido. Si querías una carpeta, " +
                "usá accion=carpeta; si era un archivo, decime cómo se llama y qué va adentro.");
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        if (append)
        {
            // El modelo manda «\n- pan» para que el ítem caiga en un renglón nuevo, pero
            // JsonToolArguments le hace Trim() al contenido y se come exactamente ese salto; encima
            // AppendAllText no pone separador. La lista terminaba «3. leche- comprar pan», todo
            // pegado en la misma línea. El salto lo pone acá quien sabe cómo termina el archivo.
            var separator = File.Exists(path) && !EndsWithNewLine(path)
                ? Environment.NewLine
                : string.Empty;

            File.AppendAllText(path, separator + content);

            // Comprobar y no suponer: si el final del archivo no es lo que se pidió agregar, algo
            // salió mal —encoding raro, disco lleno— y decir «Agregué» sería mentir.
            var tail = ReadTail(path, content.Length);
            if (tail is null || !tail.EndsWith(content, StringComparison.Ordinal))
            {
                return Fail(
                    context,
                    $"Agregué a «{Path.GetFileName(path)}» pero el final del archivo no quedó como pediste.");
            }

            return Ok(context, $"Agregué al final de «{Path.GetFileName(path)}».");
        }

        // Si ya existía, la versión anterior va a la papelera antes de pisarla: reemplazar es una
        // forma de borrar, y borrar tiene que poder deshacerse.
        var replaced = File.Exists(path);
        if (replaced)
        {
            MoveToRecycle(path, copy: true);
        }

        File.WriteAllText(path, content);

        // Se comprueba antes de afirmar. Y se dice «el archivo», no «Creé X» a secas: la ambigüedad
        // entre archivo y carpeta es justo la que hizo que pareciera haber hecho algo que no hizo.
        if (!File.Exists(path))
        {
            return Fail(context, $"Escribí «{path}» pero no aparece en el disco.");
        }

        // Que el archivo exista no dice nada de lo que tiene adentro: podía quedar vacío o truncado
        // —disco lleno, un antivirus que lo corta— y la respuesta cantaba «Creé el archivo» igual.
        // Se relee y se compara con lo pedido: eso es comprobar, lo otro era suponer.
        var saved = SafeReadAllText(path);
        if (saved is null)
        {
            return Fail(context, $"Escribí «{Path.GetFileName(path)}» pero no pude releerlo para comprobarlo.");
        }

        if (!string.Equals(saved, content, StringComparison.Ordinal))
        {
            return Fail(
                context,
                $"Escribí «{Path.GetFileName(path)}» pero lo que quedó adentro no es lo que pediste " +
                $"({saved.Length} caracteres de {content.Length}).");
        }

        return Ok(context, replaced
            ? $"Reescribí el archivo «{Path.GetFileName(path)}». Guardé la versión anterior por si acaso."
            : $"Creé el archivo «{path}».");
    }

    /// <summary>Mira si el archivo ya termina en salto de línea, para no pegar el texto que sigue.</summary>
    private static bool EndsWithNewLine(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
        {
            // Un archivo vacío no necesita separador: el salto dejaría un renglón en blanco arriba.
            return true;
        }

        stream.Seek(-1, SeekOrigin.End);
        var last = stream.ReadByte();
        return last is '\n' or '\r';
    }

    /// <summary>
    /// Lee sólo el final del archivo, lo justo para comprobar qué se agregó.
    /// </summary>
    /// <remarks>
    /// Releer entero un archivo grande sólo para mirar su última línea es caro y no hace falta: con
    /// los últimos bytes alcanza. Se piden cuatro por carácter porque en UTF-8 uno puede ocupar
    /// hasta cuatro, y si el recorte parte un carácter al principio no molesta —lo que se compara
    /// es el final—.
    /// </remarks>
    private static string? ReadTail(string path, int characters)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var wanted = Math.Max(16L, ((long)characters * 4) + 8);
            if (stream.Length > wanted)
            {
                stream.Seek(-wanted, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Relee un archivo para comprobar lo escrito; devuelve <c>null</c> si no se puede.</summary>
    private static string? SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ToolExecutionResult List(ToolExecutionContext context, string path)
    {
        if (!Directory.Exists(path))
        {
            return Fail(context, $"«{path}» no es una carpeta.");
        }

        var directories = Directory.EnumerateDirectories(path).ToArray();
        var files = Directory.EnumerateFiles(path).ToArray();

        if (directories.Length == 0 && files.Length == 0)
        {
            return Ok(context, $"«{path}» está vacía.");
        }

        // El cupo se repartía por orden de llegada: Take(MaximumListed) para las carpetas y
        // Take(MaximumListed - count) para los archivos. Con 200 subcarpetas el segundo Take quedaba
        // en cero y la respuesta no mostraba ni un archivo —sin decir que había recortado—, así que
        // «no hay archivos acá» y «no entraban» se leían igual. Ahora cada mitad tiene su cupo, lo
        // que una no usa se lo lleva la otra, y lo que quedó afuera se dice siempre.
        var folderQuota = Math.Min(directories.Length, MaximumListed / 2);
        var fileQuota = Math.Min(files.Length, MaximumListed - folderQuota);
        folderQuota = Math.Min(directories.Length, MaximumListed - fileQuota);

        var builder = new StringBuilder($"En «{path}»:");

        foreach (var directory in directories.Take(folderQuota))
        {
            builder.AppendLine().Append("  [carpeta] ").Append(Path.GetFileName(directory));
        }

        foreach (var file in files.Take(fileQuota))
        {
            var info = new FileInfo(file);
            builder.AppendLine().Append("  ").Append(info.Name)
                .Append(" · ").Append(info.Length / 1024).Append(" KB");
        }

        var total = directories.Length + files.Length;
        var shown = folderQuota + fileQuota;
        if (shown < total)
        {
            builder.AppendLine()
                .Append("  … y ").Append(total - shown)
                .Append(" más que no entran: te muestro ").Append(shown)
                .Append(" de ").Append(total).Append('.');
        }

        return Ok(context, builder.ToString());
    }

    private static ToolExecutionResult Search(ToolExecutionContext context, string path, string? pattern)
    {
        if (!Directory.Exists(path))
        {
            return Fail(context, $"«{path}» no es una carpeta.");
        }

        var needle = string.IsNullOrWhiteSpace(pattern) ? "*" : $"*{pattern.Trim('*')}*";

        // EnumerateFiles y no EnumerateFileSystemEntries: la acción dice «busca archivos por nombre»
        // y la otra devolvía también carpetas, así que la lista traía cosas que no se podían leer.
        var matches = Directory.EnumerateFiles(path, needle, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 6
        });

        // Antes se cortaba con Take(40) y después se contaba lo cortado: con 300 coincidencias la
        // respuesta decía «Encontré 40» —falso, y encima el modelo lo repetía como si fuera el
        // total—. Ahora se cuenta lo que hay y se avisa que se muestran los primeros.
        var found = new List<string>(MaximumShownInSearch);
        var total = 0;
        var capped = false;
        foreach (var entry in matches)
        {
            total++;
            if (found.Count < MaximumShownInSearch)
            {
                found.Add(entry);
            }

            if (total >= MaximumCountedInSearch)
            {
                capped = true;
                break;
            }
        }

        if (total == 0)
        {
            return Ok(context, $"No encontré nada que coincida con «{pattern}» bajo «{path}».");
        }

        var header = total > found.Count
            ? capped
                ? $"Encontré más de {MaximumCountedInSearch} archivos; te muestro los primeros {found.Count}:"
                : $"Encontré {total} archivos; te muestro los primeros {found.Count}:"
            : $"Encontré {total}:";

        return Ok(context, header + "\n" + string.Join("\n", found));
    }

    private ToolExecutionResult Transfer(
        ToolExecutionContext context,
        string path,
        string? destination,
        bool move)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Fail(context, "Necesito saber a dónde.");
        }

        var target = Expand(destination);

        // Si el destino es una carpeta, se conserva el nombre: «mové esto a Documentos» no quiere
        // decir «renombrá esto a Documentos».
        if (Directory.Exists(target))
        {
            target = Path.Combine(target, Path.GetFileName(path));
        }
        else if (LooksLikeAFolder(target))
        {
            // La carpeta destino todavía no existe. Sin esto, «mové el informe a Trabajo» no creaba
            // «Trabajo»: renombraba informe.docx a un archivo llamado «Trabajo», sin extensión, y
            // contestaba que lo había movido. El archivo seguía ahí, con otro nombre y sin programa
            // que lo abriera, que es la forma más difícil de darse cuenta de que se perdió algo.
            Directory.CreateDirectory(target);
            target = Path.Combine(target, Path.GetFileName(path));
        }

        var folder = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        if (Directory.Exists(path))
        {
            if (move)
            {
                Directory.Move(path, target);
                return Ok(context, $"Moví la carpeta a «{target}».");
            }

            return Fail(context, "Copiar carpetas enteras todavía no lo hago; movelas o copiá archivos.");
        }

        if (!File.Exists(path))
        {
            return Fail(context, $"No encontré «{path}».");
        }

        // Pisar el destino sin guardarlo es una pérdida silenciosa: el archivo que estaba ahí
        // desaparece y nadie lo menciona. «escribir» ya hace esto mismo antes de reemplazar.
        var replaced = File.Exists(target);
        if (replaced)
        {
            MoveToRecycle(target, copy: true);
        }

        var aviso = replaced ? " Lo que había ahí quedó guardado en la papelera." : string.Empty;

        if (move)
        {
            File.Move(path, target, overwrite: true);
            return File.Exists(target)
                ? Ok(context, $"Moví «{Path.GetFileName(path)}» a «{target}».{aviso}")
                : Fail(context, $"Dije de mover a «{target}» pero no aparece ahí.");
        }

        File.Copy(path, target, overwrite: true);
        return File.Exists(target)
            ? Ok(context, $"Copié «{Path.GetFileName(path)}» a «{target}».{aviso}")
            : Fail(context, $"Dije de copiar a «{target}» pero no aparece ahí.");
    }

    /// <summary>
    /// Decide si un destino inexistente era una carpeta o un nombre de archivo nuevo.
    /// </summary>
    /// <remarks>
    /// No hay forma de saberlo con certeza, así que se elige el error barato. Si se acierta que era
    /// una carpeta, todo bien. Si se equivoca y era un renombre, queda un archivo dentro de una
    /// carpeta de más —molesto y visible—. Al revés, tratar una carpeta como renombre deja el
    /// archivo sin extensión y sin rastro de cuál era, que es una pérdida silenciosa.
    /// <para>
    /// La señal es la extensión: «mové esto a Trabajo» es una carpeta; «mové esto a copia.docx» es
    /// un renombre. Nada más decide, porque nada más se sabe.
    /// </para>
    /// </remarks>
    private static bool LooksLikeAFolder(string target) =>
        Path.GetExtension(target).Length == 0;

    private ToolExecutionResult Recycle(ToolExecutionContext context, string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Fail(context, $"No encontré «{path}».");
        }

        var moved = MoveToRecycle(path, copy: false);
        return moved is null
            ? Fail(context, $"No pude mover «{path}» a la papelera.")
            : Ok(context, $"Mandé «{Path.GetFileName(path)}» a la papelera. " +
                "Si te arrepentís, pedime que lo recupere.");
    }

    /// <summary>
    /// Mueve —o copia— a la papelera propia, con la fecha adelante para no pisar versiones.
    /// </summary>
    private string? MoveToRecycle(string path, bool copy)
    {
        var root = _recycleRoot;

        try
        {
            // La papelera crecía para siempre: cada borrado y cada reemplazo dejaban una copia y
            // nada las sacaba nunca. Se poda acá y no con un temporizador, porque un temporizador
            // borra archivos mientras nadie mira —y esto no debería correr si Viernes no está
            // haciendo nada—. Podar justo antes de guardar algo nuevo mantiene el costo donde se
            // genera y no requiere ningún proceso de fondo.
            PruneRecycle(root, DateTime.Now);

            // Milisegundos, no segundos. Con resolución de segundo, dos borrados del mismo nombre en
            // el mismo segundo caían en la misma carpeta y el segundo pisaba al primero con
            // overwrite —mientras las dos respuestas decían «se puede recuperar»—.
            var stamp = DateTime.Now.ToString(RecycleStampFormat);
            var folder = Path.Combine(root, stamp);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(path));

            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
                return target;
            }

            if (copy)
            {
                File.Copy(path, target, overwrite: true);
            }
            else
            {
                File.Move(path, target, overwrite: true);
            }

            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Borra definitivamente lo que lleva más de treinta días en la papelera propia y devuelve
    /// cuántas carpetas se fueron de verdad.
    /// </summary>
    /// <remarks>
    /// Nada la podaba: cada borrado —y cada reemplazo, que también guarda copia— dejaba una carpeta
    /// con fecha, y esas carpetas se acumulaban en <c>%LOCALAPPDATA%\Viernes</c> sin techo ni aviso.
    /// Después de un mes ya nadie va a pedir que se recupere aquello, y el disco no es infinito.
    /// <para>
    /// Sólo se tocan las carpetas cuyo nombre es una fecha en el formato que escribe la papelera: si
    /// alguien dejó otra cosa ahí adentro, no es nuestra y no se borra. El conteo se hace después de
    /// comprobar que la carpeta efectivamente ya no está, no después de pedir el borrado.
    /// </para>
    /// </remarks>
    public static int PruneRecycle(string root, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return 0;
        }

        var limit = TimeSpan.FromDays(RecycleRetentionDays);
        var removed = 0;

        foreach (var folder in Directory.EnumerateDirectories(root).ToArray())
        {
            if (!DateTime.TryParseExact(
                    Path.GetFileName(folder),
                    RecycleStampFormat,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var stamped))
            {
                continue;
            }

            if (now - stamped <= limit)
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Un archivo en uso no debería frenar la poda del resto.
                continue;
            }

            if (!Directory.Exists(folder))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Devuelve lo borrado desde la papelera propia, lo más reciente primero.
    /// </summary>
    /// <remarks>
    /// Existía la papelera y no existía la vuelta: cada borrado contestaba «se puede recuperar» y la
    /// única forma de hacerlo era abrir <c>%LOCALAPPDATA%\Viernes\papelera</c> a mano y adivinar cuál
    /// de las carpetas con fecha era. Una promesa que sólo puede cumplir quien conoce el código no
    /// es una promesa.
    /// <para>
    /// Sin nombre, lista lo que hay. Con nombre, restaura la copia más nueva que coincida.
    /// </para>
    /// </remarks>
    private ToolExecutionResult Restore(ToolExecutionContext context, string? name)
    {
        // Con papelera vacía, la respuesta depende de qué preguntaron. «¿Qué hay para recuperar?»
        // sobre una papelera vacía es un «nada», y eso es una respuesta válida. Pero «recuperá el
        // presupuesto» sobre una papelera vacía es un fallo: pidieron algo concreto que no está, y
        // contestarlo como éxito deja al modelo diciendo que lo recuperó.
        if (!Directory.Exists(_recycleRoot))
        {
            return string.IsNullOrWhiteSpace(name)
                ? Ok(context, "La papelera está vacía; no borré nada todavía.")
                : Fail(context, $"No tengo nada llamado «{name.Trim()}» en la papelera.");
        }

        var borrados = Directory.EnumerateDirectories(_recycleRoot)
            .OrderByDescending(folder => folder, StringComparer.Ordinal)
            .SelectMany(folder => Directory
                .EnumerateFileSystemEntries(folder)
                .Select(entry => (Folder: folder, Entry: entry)))
            .ToArray();

        if (borrados.Length == 0)
        {
            return string.IsNullOrWhiteSpace(name)
                ? Ok(context, "La papelera está vacía; no borré nada todavía.")
                : Fail(context, $"No tengo nada llamado «{name.Trim()}» en la papelera.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var listado = borrados
                .Take(MaximumListed)
                .Select(item => $"· {Path.GetFileName(item.Entry)}  (borrado {Stamp(item.Folder)})");
            return Ok(
                context,
                $"Tengo {borrados.Length} cosas en la papelera. Decime cuál devuelvo:\n" +
                string.Join('\n', listado));
        }

        var buscado = name.Trim();
        var match = borrados.FirstOrDefault(item =>
            Path.GetFileName(item.Entry).Equals(buscado, StringComparison.OrdinalIgnoreCase));

        if (match.Entry is null)
        {
            match = borrados.FirstOrDefault(item =>
                Path.GetFileName(item.Entry).Contains(buscado, StringComparison.OrdinalIgnoreCase));
        }

        if (match.Entry is null)
        {
            return Fail(context, $"No tengo nada llamado «{buscado}» en la papelera.");
        }

        // Vuelve al escritorio y no a su lugar original: la ruta de origen no se guarda, y adivinarla
        // sería restaurar en un lugar equivocado con cara de certeza.
        var destino = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.GetFileName(match.Entry));

        if (File.Exists(destino) || Directory.Exists(destino))
        {
            return Fail(context, $"Ya hay algo llamado «{Path.GetFileName(destino)}» en el escritorio.");
        }

        if (Directory.Exists(match.Entry))
        {
            Directory.Move(match.Entry, destino);
        }
        else
        {
            File.Move(match.Entry, destino);
        }

        return File.Exists(destino) || Directory.Exists(destino)
            ? Ok(context, $"Devolví «{Path.GetFileName(destino)}» al escritorio.")
            : Fail(context, $"No pude devolver «{buscado}».");
    }

    private static string Stamp(string folder)
    {
        var raw = Path.GetFileName(folder);
        return DateTime.TryParseExact(
            raw,
            RecycleStampFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
                ? parsed.ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : raw;
    }

    private static ToolExecutionResult Ok(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Success(context.ToolCallId, ToolName, message);

    private static ToolExecutionResult Fail(ToolExecutionContext context, string message) =>
        ToolExecutionResult.Failure(context.ToolCallId, ToolName, message);
}
