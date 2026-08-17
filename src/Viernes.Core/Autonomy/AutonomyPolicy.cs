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

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<AutonomyRule>? _rules;

    public AutonomyPolicy(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "autonomia.json");

    public string FilePath => _path;

    /// <summary>Dice si puede hacerlo sola, tiene que preguntar, o no puede.</summary>
    public async Task<AutonomyLevel> DecideAsync(
        string action,
        string? subject,
        CancellationToken cancellationToken = default)
    {
        var rules = await LoadAsync(cancellationToken).ConfigureAwait(false);
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
        var rules = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return rules.OrderByDescending(rule => rule.LearnedAt).ToArray();
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

    private async Task<List<AutonomyRule>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AutonomyRule>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_rules is not null)
        {
            return _rules;
        }

        if (!File.Exists(_path))
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
    }
}
