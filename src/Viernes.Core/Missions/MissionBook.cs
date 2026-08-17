using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viernes.Core.Missions;

/// <summary>
/// Las misiones abiertas, en disco.
/// </summary>
/// <remarks>
/// Es la memoria de <em>lo que está haciendo</em>, distinta de la de hechos sobre el usuario y de la
/// de cómo se hacen las cosas. Sin ella, cada charla arranca sin saber que quedó algo a medias: el
/// asistente puede acordarse de que te gusta el mate y no de que hace dos días te prometió revisar
/// un proyecto y te preguntó algo que nunca contestaste.
/// </remarks>
public sealed class MissionBook
{
    private const int MaximumMissions = 200;
    private const int MaximumLogEntries = 40;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Mission>? _missions;

    public MissionBook(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "misiones.json");

    public string FilePath => _path;

    /// <summary>Anota una misión nueva y devuelve su identificador corto.</summary>
    public async Task<Mission> CreateAsync(
        string title,
        string goal,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var missions = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var mission = new Mission
            {
                Id = NextId(missions),
                Title = Shorten(title, 120) ?? title,
                Goal = Shorten(string.IsNullOrWhiteSpace(goal) ? title : goal, 400) ?? title,
                Context = Shorten(context, 400),
                State = MissionState.Abierta
            };

            missions.Add(mission);
            await SaveUnsafeAsync(missions, cancellationToken).ConfigureAwait(false);
            return mission;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Suma una línea a la bitácora y marca que sigue viva.</summary>
    public Task<Mission?> AdvanceAsync(string id, string what, CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission =>
        {
            mission.Log.Add(new MissionStep(DateTimeOffset.Now, Shorten(what, 300) ?? string.Empty));
            if (mission.Log.Count > MaximumLogEntries)
            {
                // Se cae lo más viejo: la bitácora es para saber en qué anda, no un archivo histórico.
                mission.Log.RemoveRange(0, mission.Log.Count - MaximumLogEntries);
            }

            mission.LastProgress = DateTimeOffset.Now;
            if (mission.State == MissionState.Abierta)
            {
                mission.State = MissionState.EnCurso;
            }
        }, cancellationToken);

    /// <summary>Deja una pregunta pendiente y frena la misión hasta que la contesten.</summary>
    public Task<Mission?> AskAsync(string id, string question, CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission =>
        {
            mission.Question = Shorten(question, 300);
            mission.AskedAt = DateTimeOffset.Now;
            mission.State = MissionState.Esperando;
        }, cancellationToken);

    /// <summary>Registra la respuesta del usuario y la destraba.</summary>
    public Task<Mission?> AnswerAsync(string id, string answer, CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission =>
        {
            var pregunta = mission.Question;
            mission.Question = null;
            mission.AskedAt = null;
            mission.State = MissionState.EnCurso;
            mission.LastProgress = DateTimeOffset.Now;
            mission.Log.Add(new MissionStep(
                DateTimeOffset.Now,
                pregunta is null
                    ? $"Me dijiste: {Shorten(answer, 200)}"
                    : $"Te pregunté «{pregunta}» y me dijiste: {Shorten(answer, 200)}"));
        }, cancellationToken);

    public Task<Mission?> CloseAsync(string id, string? how, CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission =>
        {
            mission.State = MissionState.Terminada;
            mission.Question = null;
            mission.AskedAt = null;
            mission.LastProgress = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(how))
            {
                mission.Log.Add(new MissionStep(DateTimeOffset.Now, Shorten(how, 300)!));
            }
        }, cancellationToken);

    public Task<Mission?> CancelAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission =>
        {
            mission.State = MissionState.Cancelada;
            mission.Question = null;
            mission.AskedAt = null;
        }, cancellationToken);

    /// <summary>Fija cuándo conviene volver a mirarla sola.</summary>
    public Task<Mission?> ScheduleReviewAsync(
        string id,
        DateTimeOffset when,
        CancellationToken cancellationToken = default) =>
        MutateAsync(id, mission => mission.NextReview = when, cancellationToken);

    public async Task<IReadOnlyList<Mission>> ListAsync(
        bool onlyOpen = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var missions = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return missions
                .Where(mission => !onlyOpen || mission.IsOpen)
                .OrderByDescending(mission => mission.LastProgress)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Lo que hay que saber de las misiones para el turno actual, o <c>null</c> si no hay nada.
    /// </summary>
    /// <remarks>
    /// Va entero y en cada turno, como las reglas: son pocas, las abrió el usuario, y el costo en
    /// tokens es despreciable comparado con retomar una charla sin saber que quedó algo a medias.
    /// Las preguntas pendientes van primero porque son lo único que puede destrabarse ahora mismo.
    /// </remarks>
    public async Task<string?> DescribeOpenAsync(CancellationToken cancellationToken = default)
    {
        var missions = await ListAsync(onlyOpen: true, cancellationToken).ConfigureAwait(false);
        if (missions.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder("Misiones abiertas. Son tuyas y siguen vivas entre charlas:");

        foreach (var mission in missions.OrderBy(mission => mission.State == MissionState.Esperando ? 0 : 1))
        {
            builder.AppendLine();
            builder.Append($"[{mission.Id}] {mission.Title} — {Describe(mission.State)}");

            if (mission.State == MissionState.Esperando && mission.Question is not null)
            {
                builder.Append($". LE PREGUNTASTE: «{mission.Question}» y todavía no te contestó");
            }

            var last = mission.Log.LastOrDefault();
            if (last is not null)
            {
                builder.Append($". Último avance: {last.Text}");
            }
        }

        builder.AppendLine();
        builder.Append(
            "Si el usuario contesta algo que destraba una misión que está esperando, usá la " +
            "herramienta mision con accion=responder antes de seguir. Si avanzás en algo, anotalo " +
            "con accion=avanzar: es lo único que va a quedar cuando esta charla termine.");

        return builder.ToString();
    }

    /// <summary>
    /// Misiones que piden atención ahora: las que tienen pregunta sin contestar y las vencidas.
    /// </summary>
    /// <remarks>
    /// Es lo que consume el barrido de fondo. Deliberadamente no incluye las que sólo están «en
    /// curso»: que algo esté avanzando no es noticia, y avisar de eso es exactamente cómo un
    /// asistente proactivo se vuelve ruido de fondo.
    /// </remarks>
    public async Task<IReadOnlyList<Mission>> NeedingAttentionAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var missions = await ListAsync(onlyOpen: true, cancellationToken).ConfigureAwait(false);
        return missions
            .Where(mission =>
                (mission.State == MissionState.Esperando && mission.Question is not null) ||
                (mission.NextReview is { } review && review <= now))
            .ToArray();
    }

    private async Task<Mission?> MutateAsync(
        string id,
        Action<Mission> change,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var missions = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var mission = Resolve(missions, id);
            if (mission is null)
            {
                return null;
            }

            change(mission);
            await SaveUnsafeAsync(missions, cancellationToken).ConfigureAwait(false);
            return mission;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Encuentra una misión por identificador, o por parecido del título.
    /// </summary>
    /// <remarks>
    /// Hablando nadie dice «m3»: dice «lo del informe». Si no hay nada parecido y hay una sola
    /// misión esperando respuesta, es ésa —es la única que puede estar destrabándose—.
    /// </remarks>
    private static Mission? Resolve(List<Mission> missions, string? id)
    {
        var open = missions.Where(mission => mission.IsOpen).ToArray();

        if (!string.IsNullOrWhiteSpace(id))
        {
            var wanted = id.Trim();
            var exact = open.FirstOrDefault(mission =>
                mission.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var byTitle = open.FirstOrDefault(mission =>
                mission.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (byTitle is not null)
            {
                return byTitle;
            }
        }

        var waiting = open.Where(mission => mission.State == MissionState.Esperando).ToArray();
        if (waiting.Length == 1)
        {
            return waiting[0];
        }

        return open.Length == 1 ? open[0] : null;
    }

    private static string NextId(List<Mission> missions)
    {
        for (var number = 1; number <= MaximumMissions; number++)
        {
            var candidate = $"m{number}";
            if (!missions.Any(mission => mission.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"m{missions.Count + 1}";
    }

    private static string Describe(MissionState state) => state switch
    {
        MissionState.Abierta => "anotada, sin empezar",
        MissionState.EnCurso => "en curso",
        MissionState.Esperando => "frenada esperándote",
        MissionState.Terminada => "terminada",
        MissionState.Cancelada => "cancelada",
        _ => state.ToString()
    };

    private static string? Shorten(string? value, int maximum)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    private async Task<List<Mission>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_missions is not null)
        {
            return _missions;
        }

        if (!File.Exists(_path))
        {
            return _missions = [];
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            _missions = await JsonSerializer
                .DeserializeAsync<List<Mission>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // Un archivo ilegible no puede impedir abrir misiones nuevas. Se arranca vacío y el
            // archivo se reescribe entero en el próximo guardado.
            _missions = [];
        }

        return _missions;
    }

    /// <summary>
    /// Guarda, y <b>lanza si no pudo</b>.
    /// </summary>
    /// <remarks>
    /// Deliberadamente ruidoso. Otro libro de este proyecto se tragaba los errores de disco y
    /// contestaba «Aprendido» igual: lo guardado vivía en memoria y moría al reiniciar, sin que
    /// nadie se enterara nunca. Acá el que llama decide qué decirle al usuario, pero se entera.
    /// </remarks>
    private async Task SaveUnsafeAsync(List<Mission> missions, CancellationToken cancellationToken)
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
                .SerializeAsync(stream, missions, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}
