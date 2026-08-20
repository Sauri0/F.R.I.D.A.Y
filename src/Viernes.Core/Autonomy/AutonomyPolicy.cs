using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viernes.Core.Autonomy;

/// <summary>Hasta dónde puede llegar sola con una acción sobre alguien.</summary>
public enum AutonomyLevel
{
    /// <summary>Preguntar antes. Es el valor de fábrica para todo lo que sale del equipo.</summary>
    Preguntar,

    /// <summary>Hacerlo sin consultar.</summary>
    Automatico,

    /// <summary>No hacerlo nunca, ni preguntando.</summary>
    Nunca
}

/// <summary>Una decisión aprendida: qué acción, sobre quién, con cuánta libertad.</summary>
public sealed class AutonomyRule
{
    /// <summary>Nombre de herramienta o acción, en minúscula. <c>*</c> vale para cualquiera.</summary>
    public string Action { get; set; } = "*";

    /// <summary>A quién o sobre qué. Un mail, un dominio, un nombre. <c>*</c> vale para cualquiera.</summary>
    public string Subject { get; set; } = "*";

    public AutonomyLevel Level { get; set; } = AutonomyLevel.Preguntar;

    /// <summary>Con qué palabras lo dijo el usuario. Sirve para poder mostrárselo después.</summary>
    public string? Because { get; set; }

    public DateTimeOffset LearnedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// Cuánta libertad tiene, aprendida caso por caso.
/// </summary>
/// <remarks>
/// No es un interruptor global. Un interruptor obliga a elegir entre un asistente que pregunta todo
/// —y molesta hasta que lo apagás— o uno que no pregunta nada —y algún día le manda algo equivocado
/// a un cliente—. Ninguna de las dos es la respuesta correcta, porque la respuesta correcta depende
/// del destinatario: a un proveedor de rutina se le confirma una entrega sola; a un cliente no.
/// <para>
/// Arranca preguntando para todo lo que sale del equipo, y afloja donde el usuario le va diciendo
/// que afloje. La dirección importa: se aprende a confiar de a poco, y se aprende a desconfiar de
/// golpe. Un «esto nunca» pesa más que cualquier permiso anterior.
/// </para>
/// </remarks>
public sealed class AutonomyPolicy
{
    /// <summary>
    /// Acciones que salen del equipo y no se pueden deshacer.
    /// </summary>
    /// <remarks>
    /// Sólo éstas arrancan preguntando. Todo lo demás —leer, buscar, clasificar, redactar un
    /// borrador— es libre desde el primer día: pedir permiso para leer un mail convierte al
    /// asistente en un trámite.
    /// </remarks>
    private static readonly string[] Consequential =
    [
        "send", "enviar", "mandar", "reply", "responder", "forward", "reenviar",
        "post", "publicar", "delete", "borrar", "eliminar", "pay", "pagar", "comprar"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Una compuerta por archivo, compartida por todas las instancias que lo miran.
    /// </summary>
    /// <remarks>
    /// Era de instancia y no alcanzaba. Los desplegables arman una <see cref="AutonomyPolicy"/>
    /// propia por lectura y por escritura, y el runtime tiene la suya viva desde que arranca: con
    /// una compuerta por instancia, dos leer-modificar-escribir sobre el mismo archivo no se ven, y
    /// el último que guarda pisa entero al otro. El archivo no se corrompe —el reemplazo es
    /// atómico— pero la regla del otro desaparece sin que nada avise. Es el mismo patrón que ya usa
    /// el store de memoria personal, y por la misma razón.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private List<AutonomyRule>? _rules;

    /// <summary>Cómo estaba el archivo cuando se cargó lo que hay en <see cref="_rules"/>.</summary>
    private (DateTime WriteUtc, long Length)? _stamp;

    public AutonomyPolicy(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "autonomia.json");

        _gate = FileGates.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public string FilePath => _path;

    /// <summary>Dice si puede hacerlo sola, tiene que preguntar, o no puede.</summary>
    public async Task<AutonomyLevel> DecideAsync(
        string action,
        string? subject,
        CancellationToken cancellationToken = default)
    {
        // Una foto, tomada adentro del candado. Ver ListAsync: lo que devuelve la carga es la lista
        // cacheada, y todo lo que sigue acá abajo la recorre tres veces. Es el mismo defecto y en el
        // camino más caliente de los dos — esto corre en cada llamada a una herramienta riesgosa.
        var rules = await FotoAsync(cancellationToken).ConfigureAwait(false);
        var wantedAction = Normalize(action);
        var wantedSubject = Normalize(subject);

        // Un «nunca» gana siempre, venga de donde venga. Es la única forma de que una prohibición
        // no se pierda debajo de un permiso más específico dado antes.
        if (rules.Any(rule => rule.Level == AutonomyLevel.Nunca && Matches(rule, wantedAction, wantedSubject)))
        {
            return AutonomyLevel.Nunca;
        }

        // De lo que queda, gana la regla más específica: la que nombra a la persona por encima de
        // la que habla de la acción en general.
        var best = rules
            .Where(rule => Matches(rule, wantedAction, wantedSubject))
            .OrderByDescending(Specificity)
            .FirstOrDefault();

        if (best is not null)
        {
            return best.Level;
        }

        return IsConsequential(wantedAction) ? AutonomyLevel.Preguntar : AutonomyLevel.Automatico;
    }

    /// <summary>Aprende una decisión del usuario, pisando la anterior sobre lo mismo.</summary>
    public async Task<AutonomyRule> LearnAsync(
        string action,
        string? subject,
        AutonomyLevel level,
        string? because = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var normalizedAction = Normalize(action);
            var normalizedSubject = Normalize(subject);

            rules.RemoveAll(rule =>
                rule.Action == normalizedAction && rule.Subject == normalizedSubject);

            var learned = new AutonomyRule
            {
                Action = normalizedAction,
                Subject = normalizedSubject,
                Level = level,
                Because = because
            };

            rules.Add(learned);
            await SaveUnsafeAsync(rules, cancellationToken).ConfigureAwait(false);
            return learned;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AutonomyRule>> ListAsync(CancellationToken cancellationToken = default)
    {
        // La copia se hace ADENTRO del candado, y no es prolijidad.
        //
        // Lo que devuelve la carga NO es una lista nueva: es la lista cacheada, la misma que
        // «aprender» recorre y modifica en el lugar. Ordenarla y copiarla después de soltar el
        // candado es recorrer una colección mientras otro hilo le saca y le agrega elementos, que en
        // .NET termina en una excepción a mitad de camino.
        //
        // No es teórico desde que el mismo libro lo comparten tres llamadores en hilos distintos: la
        // herramienta que enseña un permiso, el turno escrito que los lee, y el armado de la
        // instrucción hablada. Lo encontró una auditoría; el escéptico lo descartó porque no logró
        // reproducirlo, y una carrera que no reproduce sigue siendo una carrera.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return rules.OrderByDescending(rule => rule.LearnedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lo aprendido, para que el modelo sepa qué puede hacer sin preguntar.</summary>
    public async Task<string?> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var rules = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder("Permisos que te fue dando el usuario:");
        foreach (var rule in rules.Take(30))
        {
            builder.AppendLine();
            builder.Append("- ")
                .Append(rule.Action == "*" ? "cualquier acción" : rule.Action)
                .Append(rule.Subject == "*" ? " con cualquiera" : $" con «{rule.Subject}»")
                .Append(": ")
                .Append(rule.Level switch
                {
                    AutonomyLevel.Automatico => "hacelo sin preguntar",
                    AutonomyLevel.Nunca => "NO lo hagas nunca, ni preguntando",
                    _ => "preguntá antes"
                });
        }

        builder.AppendLine();
        builder.Append(
            "Lo que no está en esta lista y sale del equipo —mandar, publicar, borrar, pagar— se " +
            "pregunta. Leer, buscar, clasificar y dejar borradores no se pregunta nunca. " +
            "Si el usuario te dice cómo quiere que manejes algo de acá en adelante, guardalo con " +
            "la herramienta permiso.");

        return builder.ToString();
    }

    private static bool IsConsequential(string action) =>
        Consequential.Any(word => action.Contains(word, StringComparison.Ordinal));

    private static bool Matches(AutonomyRule rule, string action, string subject) =>
        (rule.Action == "*" || action.Contains(rule.Action, StringComparison.Ordinal)) &&
        (rule.Subject == "*" || subject.Contains(rule.Subject, StringComparison.Ordinal));

    /// <summary>Cuánto dice una regla. Nombrar a la persona vale más que nombrar la acción.</summary>
    private static int Specificity(AutonomyRule rule) =>
        (rule.Subject == "*" ? 0 : 2) + (rule.Action == "*" ? 0 : 1);

    private static string Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? "*" : trimmed;
    }

    /// <summary>
    /// Una copia de lo que hay, hecha adentro del candado.
    /// </summary>
    /// <remarks>
    /// <b>Reemplaza a una carga que devolvía la lista viva.</b> Quien la recibía la recorría ya
    /// afuera del candado, sobre la misma colección que <c>Aprender</c> modifica en el lugar. El
    /// nombre viejo —«cargar»— era parte del problema: no decía que lo que volvía seguía siendo de
    /// adentro.
    /// </remarks>
    private async Task<List<AutonomyRule>> FotoAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cómo está el archivo ahora. Nulo si no existe.</summary>
    private (DateTime WriteUtc, long Length)? ReadStamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Si no se puede mirar, se vuelve a leer. Equivocarse para este lado cuesta una lectura;
            // para el otro, servir una regla vieja.
            return null;
        }
    }

    /// <summary>
    /// Trae las reglas, releyendo el archivo si cambió desde la última vez.
    /// </summary>
    /// <remarks>
    /// La comprobación del sello no es una optimización al revés: es lo único que hace que revocar
    /// un permiso sirva de algo. Antes esto cacheaba <see cref="_rules"/> y no lo invalidaba nunca,
    /// así que un «esto nunca» puesto desde el desplegable —que escribe con otra instancia— no
    /// frenaba nada en el proceso en curso: la compuerta real seguía contestando con la lista
    /// vieja. Y el próximo aprendizaje del modelo escribía esa lista vieja completa encima del
    /// archivo y borraba la prohibición en silencio. El panel decía «anotado» y era mentira.
    /// <para>
    /// El sello lleva fecha <b>y</b> tamaño: dos escrituras dentro del mismo tic del reloj de
    /// archivos tienen la misma fecha, y el tamaño las separa salvo que además midan igual.
    /// </para>
    /// </remarks>
    private async Task<List<AutonomyRule>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        var stamp = ReadStamp();

        if (_rules is not null && _stamp == stamp)
        {
            return _rules;
        }

        _stamp = stamp;

        if (stamp is null)
        {
            return _rules = [];
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            _rules = await JsonSerializer
                .DeserializeAsync<List<AutonomyRule>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // Un archivo ilegible cae del lado seguro: sin reglas, todo lo que sale del equipo
            // vuelve a preguntarse. Nunca al revés.
            _rules = [];
        }

        return _rules;
    }

    /// <summary>Guarda, y lanza si no pudo. Un permiso que se cree guardado y no lo está miente dos veces.</summary>
    private async Task SaveUnsafeAsync(List<AutonomyRule> rules, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(stream, rules, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);

        // Lo recién guardado ES lo que hay en memoria, y el sello se toma DESPUÉS del reemplazo: si
        // se tomara antes, la próxima lectura vería que el archivo cambió y releería lo que acaba de
        // escribir. Sin actualizar la lista, en cambio, quedaría sirviendo la anterior.
        _rules = rules;
        _stamp = ReadStamp();
    }
}
