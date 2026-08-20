using System.Globalization;
using System.Text;
using Viernes.Memory.Privacy;

namespace Viernes.Memory.Brain;

/// <summary>
/// Lo que sabe, en una carpeta de archivos de texto que se puede abrir y leer.
/// </summary>
/// <remarks>
/// <b>Reemplaza a una libreta.</b> Lo que había era un solo <c>.json</c> con tope de quinientas
/// notas de quinientos caracteres, con las observaciones borrándose solas a los treinta días. Servía
/// para acordarse de un par de cosas; no para saber.
/// <para>
/// La forma es la que pidió el usuario y la que describe la skill que escribió: un índice arriba,
/// las charlas como evidencia, y lo destilado en notas sueltas dentro de carpetas que ella misma
/// organiza.
/// </para>
/// <code>
///   cerebro\
///     CEREBRO.md        el índice: una línea por nota, con enlaces
///     charlas\          cada conversación, escrita mientras pasa
///     saber\            lo destilado, en carpetas
/// </code>
/// <para>
/// <b>El índice existe porque el cerebro entero no entra en el contexto.</b> Lo que se le arma al
/// modelo en cada turno es el índice —una línea por nota— y recién después las notas que parecen
/// venir al caso. Por eso el título de cada nota importa tanto como su cuerpo: es lo único que se ve
/// cuando hay que decidir qué leer.
/// </para>
/// <para>
/// <b>Nada se borra al corregir.</b> Cuando algo resulta estar mal, la nota vieja queda marcada como
/// reemplazada y la nueva la nombra. Borrarla dejaría una corrección sin causa, y entender por qué
/// se equivocó es la mitad de lo que hace que no se vuelva a equivocar.
/// </para>
/// </remarks>
public sealed class Brain
{
    private readonly string _root;
    private readonly TimeProvider _time;

    /// <param name="folder">La carpeta del cerebro. Se crea si no está.</param>
    /// <param name="timeProvider">El reloj. En las pruebas se le pasa uno de mentira.</param>
    public Brain(string folder, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _root = folder;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>La carpeta del cerebro.</summary>
    public string Root => _root;

    /// <summary>Dónde van las charlas.</summary>
    public string ChatsFolder => System.IO.Path.Combine(_root, "charlas");

    /// <summary>Dónde va lo destilado.</summary>
    public string KnowledgeFolder => System.IO.Path.Combine(_root, "saber");

    /// <summary>El archivo del índice.</summary>
    public string IndexPath => System.IO.Path.Combine(_root, "CEREBRO.md");

    /// <summary>
    /// Convierte un título en un nombre de archivo.
    /// </summary>
    /// <remarks>
    /// Sin acentos ni eñes, porque el nombre viaja: entra en enlaces del índice, en rutas, y
    /// eventualmente en un repositorio o un respaldo. El título con sus acentos vive adentro del
    /// archivo, que es donde se lee.
    /// </remarks>
    public static string Slug(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var plano = title.Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(plano.Length);
        var guion = false;

        foreach (var letra in plano)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(letra) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(letra))
            {
                limpio.Append(char.ToLowerInvariant(letra));
                guion = false;
                continue;
            }

            if (!guion && limpio.Length > 0)
            {
                limpio.Append('-');
                guion = true;
            }
        }

        var nombre = limpio.ToString().Trim('-');
        if (nombre.Length > 60)
        {
            nombre = nombre[..60].TrimEnd('-');
        }

        return nombre.Length == 0 ? "nota" : nombre;
    }

    /// <summary>
    /// Guarda una nota y deja el índice al día.
    /// </summary>
    /// <remarks>
    /// El índice se rehace entero en vez de agregarle un renglón. Es más caro y es lo correcto: el
    /// usuario puede haber borrado una nota a mano —se lo invita a hacerlo— y un índice que sólo
    /// crece terminaría lleno de enlaces a archivos que ya no están.
    /// <para>
    /// <b>Y si esa nota ya existe en otra carpeta, se guarda donde está y no donde le tocaría.</b>
    /// Se invita al usuario a reorganizar moviendo archivos; sin esto, la primera vez que ella
    /// volviera a aprender algo sobre ese tema escribiría una copia en la carpeta por omisión, y
    /// quedarían dos archivos con el mismo nombre diciendo cosas distintas — y cuál de los dos se lee
    /// después depende del orden en que el sistema devuelva los archivos. Lo encontró una prueba que
    /// buscaba otra cosa.
    /// </para>
    /// </remarks>
    public void Save(BrainNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        if (Ubicacion(note.Name) is { } donde && !string.Equals(donde, note.Folder, StringComparison.OrdinalIgnoreCase))
        {
            note = note with { Folder = donde };
        }

        var destino = System.IO.Path.Combine(_root, note.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destino)!);
        File.WriteAllText(destino, Render(note), Encoding.UTF8);
        Reindex();
    }

    /// <summary>
    /// Da por reemplazada una nota y guarda la que la reemplaza.
    /// </summary>
    /// <remarks>
    /// Las dos cosas juntas y no sueltas: reemplazar sin guardar la nueva deja el cerebro sin la
    /// respuesta, y guardar la nueva sin marcar la vieja deja las dos vigentes diciendo cosas
    /// distintas, que es peor que cualquiera de las dos sola.
    /// </remarks>
    /// <param name="oldName">La nota que dejó de valer. Si no está, se guarda la nueva igual.</param>
    /// <param name="replacement">La que la reemplaza.</param>
    public void Supersede(string oldName, BrainNote replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (Read(oldName) is { } vieja)
        {
            Save(vieja with { Status = BrainStatus.Reemplazada });
        }

        Save(replacement with { Supersedes = oldName });
    }

    /// <summary>En qué carpeta vive ya una nota, o nulo si todavía no existe.</summary>
    private string? Ubicacion(string name)
    {
        var archivo = Archivo(name);
        if (archivo is null)
        {
            return null;
        }

        var carpeta = System.IO.Path.GetRelativePath(
            KnowledgeFolder,
            System.IO.Path.GetDirectoryName(archivo) ?? KnowledgeFolder);

        return carpeta == "." ? string.Empty : carpeta.Replace('\\', '/');
    }

    private string? Archivo(string name) =>
        string.IsNullOrWhiteSpace(name) || !Directory.Exists(KnowledgeFolder)
            ? null
            : Directory
                .EnumerateFiles(KnowledgeFolder, name + ".md", SearchOption.AllDirectories)
                .FirstOrDefault();

    /// <summary>Lee una nota por su nombre, la busque donde la busque.</summary>
    public BrainNote? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(KnowledgeFolder))
        {
            return null;
        }

        return Archivo(name) is { } archivo ? Parse(archivo) : null;
    }

    /// <summary>
    /// Todo lo que sabe, ordenado por carpeta y título.
    /// </summary>
    /// <remarks>
    /// Incluye lo reemplazado. Quien quiera sólo lo que vale filtra por
    /// <see cref="BrainStatus.Vigente"/> — y quien quiera entender por qué creía algo necesita
    /// justamente lo otro.
    /// </remarks>
    public IReadOnlyList<BrainNote> All()
    {
        if (!Directory.Exists(KnowledgeFolder))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(KnowledgeFolder, "*.md", SearchOption.AllDirectories)
            .Select(Parse)
            .OfType<BrainNote>()
            .OrderBy(nota => nota.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(nota => nota.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Rehace el índice desde lo que hay en disco.
    /// </summary>
    /// <remarks>
    /// Desde el disco y no desde lo que el programa cree tener: el usuario puede editar y borrar
    /// notas con cualquier editor —eso es la mitad de por qué esto es Markdown— y un índice que se
    /// arma de memoria dejaría de coincidir con la carpeta apenas lo hiciera.
    /// </remarks>
    public void Reindex()
    {
        var notas = All();
        var texto = new StringBuilder();

        texto.AppendLine("# Lo que sé");
        texto.AppendLine();
        texto.AppendLine(
            "Esto se rehace solo cada vez que aprendo algo. Podés borrar o corregir cualquier nota " +
            "con el editor que quieras: la próxima vez que aprenda algo, el índice se acomoda a lo " +
            "que quede.");
        texto.AppendLine();

        if (notas.Count == 0)
        {
            texto.AppendLine("_Todavía no sé nada._");
        }

        foreach (var grupo in notas
            .Where(nota => nota.Status == BrainStatus.Vigente)
            .GroupBy(nota => string.IsNullOrEmpty(nota.Folder) ? "sin carpeta" : nota.Folder))
        {
            texto.AppendLine($"## {grupo.Key}");
            texto.AppendLine();
            foreach (var nota in grupo)
            {
                texto.AppendLine(
                    $"- [{nota.Title}]({nota.RelativePath}) — {nota.Scope} · " +
                    $"{nota.Confidence.ToString().ToLowerInvariant()}");
            }

            texto.AppendLine();
        }

        var reemplazadas = notas.Count(nota => nota.Status == BrainStatus.Reemplazada);
        if (reemplazadas > 0)
        {
            // Se cuentan y no se listan: ocupan lugar en el índice y no hay que actuar según ellas.
            // Pero decir cuántas hay es lo que evita que alguien crea que se borraron.
            texto.AppendLine(
                $"_Y {reemplazadas} nota(s) que dejaron de valer, guardadas en la carpeta para poder " +
                "entender después por qué creía eso._");
        }

        Directory.CreateDirectory(_root);
        File.WriteAllText(IndexPath, texto.ToString(), Encoding.UTF8);
    }

    /// <summary>Arma una nota nueva con lo mínimo, poniéndole fecha y nombre.</summary>
    public BrainNote Note(
        BrainNoteKind kind,
        string title,
        string body,
        string scope = "siempre",
        BrainConfidence confidence = BrainConfidence.Media,
        IReadOnlyList<string>? evidence = null,
        string? folder = null) =>
        new(
            Slug(title),
            kind,
            title.Trim(),
            string.IsNullOrWhiteSpace(scope) ? "siempre" : scope.Trim(),
            confidence,
            BrainStatus.Vigente,
            evidence ?? [],
            null,
            _time.GetLocalNow(),
            folder ?? Carpeta(kind),
            MemoryContentPolicy.Redact(body).Trim());

    /// <summary>Dónde va cada tipo por omisión.</summary>
    /// <remarks>
    /// Es sólo el punto de partida: la nota lleva su carpeta adentro, así que mover un archivo a otra
    /// carpeta con el explorador alcanza para reorganizar, y el índice lo va a reflejar solo. Que
    /// pueda organizarse sola —y que el usuario pueda reorganizarla a mano— era el pedido.
    /// </remarks>
    private static string Carpeta(BrainNoteKind kind) => kind switch
    {
        BrainNoteKind.Preferencia => "vos",
        BrainNoteKind.Aplicacion => "programas",
        BrainNoteKind.Procedimiento => "como-se-hace",
        BrainNoteKind.Correccion => "correcciones",
        _ => "yo"
    };

    private static string Render(BrainNote note)
    {
        var texto = new StringBuilder();
        texto.AppendLine("---");
        texto.AppendLine($"esquema: {BrainNote.Schema}");
        texto.AppendLine($"tipo: {note.Kind.ToString().ToLowerInvariant()}");
        texto.AppendLine($"titulo: {note.Title}");
        texto.AppendLine($"alcance: {note.Scope}");
        texto.AppendLine($"confianza: {note.Confidence.ToString().ToLowerInvariant()}");
        texto.AppendLine($"estado: {note.Status.ToString().ToLowerInvariant()}");
        texto.AppendLine($"cuando: {note.When:yyyy-MM-dd HH:mm}");

        if (note.Evidence.Count > 0)
        {
            texto.AppendLine($"evidencia: {string.Join(", ", note.Evidence)}");
        }

        if (!string.IsNullOrWhiteSpace(note.Supersedes))
        {
            texto.AppendLine($"reemplaza: {note.Supersedes}");
        }

        texto.AppendLine("---");
        texto.AppendLine();
        texto.AppendLine(note.Body);

        return texto.ToString();
    }

    /// <summary>
    /// Lee una nota tolerando que esté rota.
    /// </summary>
    /// <remarks>
    /// Campo por campo, y lo que no se entiende toma su valor por omisión en vez de tirar el archivo.
    /// Es la misma decisión que se tomó con las preferencias después de que un campo ilegible se
    /// llevara puesto el archivo entero: acá el archivo lo edita el usuario a mano, así que va a
    /// haber archivos rotos, y perder una nota por una coma es perder algo que no se puede recuperar.
    /// </remarks>
    private BrainNote? Parse(string path)
    {
        string texto;
        try
        {
            texto = File.ReadAllText(path);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var lineas = texto.Replace("\r\n", "\n").Split('\n');
        var campos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cuerpo = new StringBuilder();
        var enCabecera = lineas.Length > 0 && lineas[0].Trim() == "---";
        var empezoElCuerpo = !enCabecera;

        for (var i = enCabecera ? 1 : 0; i < lineas.Length; i++)
        {
            var linea = lineas[i];
            if (enCabecera && !empezoElCuerpo)
            {
                if (linea.Trim() == "---")
                {
                    empezoElCuerpo = true;
                    continue;
                }

                var corte = linea.IndexOf(':');
                if (corte > 0)
                {
                    campos[linea[..corte].Trim()] = linea[(corte + 1)..].Trim();
                }

                continue;
            }

            cuerpo.AppendLine(linea);
        }

        var nombre = System.IO.Path.GetFileNameWithoutExtension(path);
        var carpeta = System.IO.Path.GetRelativePath(
            KnowledgeFolder,
            System.IO.Path.GetDirectoryName(path) ?? KnowledgeFolder);

        return new BrainNote(
            nombre,
            Leer(campos, "tipo", BrainNoteKind.Preferencia),
            campos.TryGetValue("titulo", out var titulo) && titulo.Length > 0 ? titulo : nombre,
            campos.TryGetValue("alcance", out var alcance) && alcance.Length > 0 ? alcance : "siempre",
            Leer(campos, "confianza", BrainConfidence.Media),
            Leer(campos, "estado", BrainStatus.Vigente),
            campos.TryGetValue("evidencia", out var evidencia)
                ? [.. evidencia.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
                : [],
            campos.TryGetValue("reemplaza", out var reemplaza) && reemplaza.Length > 0 ? reemplaza : null,
            campos.TryGetValue("cuando", out var cuando) &&
            DateTimeOffset.TryParse(cuando, CultureInfo.InvariantCulture, out var fecha)
                ? fecha
                : _time.GetLocalNow(),
            carpeta == "." ? string.Empty : carpeta.Replace('\\', '/'),
            cuerpo.ToString().Trim());
    }

    private static T Leer<T>(Dictionary<string, string> campos, string campo, T porOmision)
        where T : struct, Enum =>
        campos.TryGetValue(campo, out var valor) && Enum.TryParse<T>(valor, ignoreCase: true, out var leido)
            ? leido
            : porOmision;
}
