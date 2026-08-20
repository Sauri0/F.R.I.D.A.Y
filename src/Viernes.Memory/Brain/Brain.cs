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
/// <b>El índice existe porque el cerebro entero no va a entrar siempre en el contexto.</b> Hoy lo
/// que se le arma al modelo son todas las notas vigentes enteras mientras entren en el presupuesto,
/// y recién cuando no entran pasa a ser el índice: los títulos con su alcance, sin los cuerpos. Con
/// veinte notas conviene tenerlas enteras; con doscientas conviene saber que existen todas antes que
/// conocer bien treinta y ninguna de las otras.
/// <para>
/// Leer sólo las que vienen al caso sería mejor que las dos cosas y <b>no está hecho</b>. Cuando
/// esté, el título de cada nota va a pesar tanto como su cuerpo, porque va a ser lo único que se vea
/// al decidir qué leer.
/// </para>
/// </para>
/// <para>
/// <b>Nada se borra al corregir.</b> Cuando algo resulta estar mal, la nota vieja queda marcada como
/// reemplazada y la nueva la nombra. Borrarla dejaría una corrección sin causa, y entender por qué
/// se equivocó es la mitad de lo que hace que no se vuelva a equivocar.
/// </para>
/// </remarks>
public sealed class Brain
{
    /// <summary>
    /// Guardar y reindexar son una sola cosa, y dos charlas pueden cerrar a la vez.
    /// </summary>
    /// <remarks>
    /// Cerrar una conversación dispara la destilación en una tarea aparte. Dos charlas que cierran
    /// juntas —hablando y escribiendo, o dos ventanas— entran acá al mismo tiempo, y el índice se
    /// rehace leyendo el disco entero: sin candado, una puede estar escribiendo el índice mientras
    /// la otra escribe una nota, y el índice queda sin ella hasta el próximo aprendizaje.
    /// </remarks>
    private readonly System.Threading.Lock _gate = new();

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

        lock (_gate)
        {
            GuardarSinCandado(note);
        }

        Reindex();
    }

    private void GuardarSinCandado(BrainNote note)
    {
        note = note with { Name = NombreLibre(note) };

        if (Ubicacion(note.Name) is { } donde && !string.Equals(donde, note.Folder, StringComparison.OrdinalIgnoreCase))
        {
            note = note with { Folder = donde };
        }

        var destino = System.IO.Path.Combine(_root, note.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destino)!);
        File.WriteAllText(destino, Render(note), Encoding.UTF8);
    }

    /// <summary>
    /// Un nombre que no pise una nota que no es ésta.
    /// </summary>
    /// <remarks>
    /// <b>Dos formas de pisar en silencio, y las dos costaban algo que no se recupera.</b>
    /// <list type="number">
    ///   <item>
    ///     <see cref="Slug"/> colapsa títulos distintos en el mismo nombre —recorta a sesenta
    ///     caracteres y tira todo lo que no sea letra o número—, así que una nota nueva podía
    ///     escribirse encima de una vieja de otra charla que no tenía nada que ver.
    ///   </item>
    ///   <item>
    ///     Volver a aprender un título que ya había sido reemplazado <em>resucitaba</em> la nota
    ///     vieja: se pisaba el archivo, quedaba vigente otra vez, y se perdía la evidencia de lo que
    ///     creía antes. Eso es exactamente lo que <see cref="Supersede"/> promete que no puede
    ///     pasar, y lo que la clase promete cuando dice que nada se borra al corregir.
    ///   </item>
    /// </list>
    /// El título de adentro sigue siendo el que se lee, así que un sufijo en el nombre del archivo
    /// no se nota en ningún lado salvo en el explorador.
    /// </remarks>
    private string NombreLibre(BrainNote note)
    {
        var candidato = note.Name;
        for (var i = 2; i < 100; i++)
        {
            if (Archivo(candidato) is not { } existente)
            {
                return candidato;
            }

            var actual = Parse(existente);
            if (actual is null)
            {
                return candidato;
            }

            // Es la misma nota si se llama igual, y sólo entonces se la pisa. Una que ya fue
            // reemplazada no se pisa ni siendo la misma: eso sería resucitarla.
            var esLaMisma = string.Equals(actual.Title, note.Title, StringComparison.OrdinalIgnoreCase);
            var resucitaria = actual.Status == BrainStatus.Reemplazada && note.Status == BrainStatus.Vigente;

            if (esLaMisma && !resucitaria)
            {
                return candidato;
            }

            candidato = $"{note.Name}-{i}";
        }

        return candidato;
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

        lock (_gate)
        {
            // Las dos escrituras bajo el mismo candado: si entre una y otra se colara otro cierre de
            // charla, el índice podría quedar con la vieja ya vencida y la nueva todavía sin
            // escribir, o sea sin nada vigente sobre el tema.
            if (Read(oldName) is { } vieja)
            {
                GuardarSinCandado(vieja with { Status = BrainStatus.Reemplazada });
            }

            GuardarSinCandado(replacement with { Supersedes = oldName });
        }

        Reindex();
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
        lock (_gate)
        {
            ReindexSinCandado();
        }
    }

    private void ReindexSinCandado()
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

        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(IndexPath, texto.ToString(), Encoding.UTF8);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            // El índice se puede rehacer en cualquier momento desde el disco; las notas no. Dejar
            // que esto tire se llevaba puestas las que faltaban de la misma charla, porque quien
            // aprende recorre las notas una por una y la primera excepción aborta el resto. Perder
            // el índice cuesta un aprendizaje de nada; perder una nota cuesta algo que no vuelve.
        }
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
        Note(
            kind,
            CredentialLikeText.Redact(title),
            body,
            CredentialLikeText.Redact(scope),
            confidence,
            evidence,
            folder,
            tapado: true);

    /// <summary>
    /// Lo mismo, con el título y el alcance ya tapados.
    /// </summary>
    /// <remarks>
    /// <b>El tapado cubría el cuerpo y nada más.</b> El título viaja mucho más lejos que el cuerpo:
    /// va adentro del archivo, va en el NOMBRE del archivo, va al índice, y el índice es lo que se le
    /// arma al modelo en cada turno. Una clave en el título quedaba en los cuatro lugares.
    /// </remarks>
    private BrainNote Note(
        BrainNoteKind kind,
        string title,
        string body,
        string scope,
        BrainConfidence confidence,
        IReadOnlyList<string>? evidence,
        string? folder,
        bool tapado) =>
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

    /// <summary>
    /// Aplasta a una línea lo que va en la cabecera.
    /// </summary>
    /// <remarks>
    /// La cabecera es «clave: valor» por renglón y no escapa nada. Un título con un salto de línea
    /// adentro escribía renglones sueltos que al leerse se tomaban por campos —y como al leer gana
    /// el último valor, podía cambiar campos escritos ANTES, como el tipo o la versión del formato—.
    /// Además el título quedaba truncado a su primera línea. El cuerpo puede tener los saltos que
    /// quiera; la cabecera, no.
    /// </remarks>
    private static string UnaLinea(string valor) =>
        string.Join(' ', valor.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Render(BrainNote note)
    {
        var texto = new StringBuilder();
        texto.AppendLine("---");
        texto.AppendLine($"esquema: {BrainNote.Schema}");
        texto.AppendLine($"tipo: {note.Kind.ToString().ToLowerInvariant()}");
        texto.AppendLine($"titulo: {UnaLinea(note.Title)}");
        texto.AppendLine($"alcance: {UnaLinea(note.Scope)}");
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

        // Si el archivo abre cabecera y nunca la cierra —lo edita el usuario a mano, así que va a
        // pasar— todas las líneas de abajo se leían como campos y el cuerpo quedaba vacío: la nota
        // seguía apareciendo en el índice como si supiera algo, y no sabía nada. Se mira si el cierre
        // existe antes de entrar, y si no existe se trata todo como cuerpo.
        var enCabecera = lineas.Length > 0 &&
            lineas[0].Trim() == "---" &&
            lineas.Skip(1).Any(linea => linea.Trim() == "---");

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
