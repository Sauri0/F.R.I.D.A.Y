using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viernes.Core.Goals;

/// <summary>En qué anda un objetivo.</summary>
public enum GoalStatus
{
    Active,
    Done,
    Abandoned
}

/// <summary>
/// Algo que el usuario quiere lograr, y que dura más que la conversación donde lo pidió.
/// </summary>
/// <remarks>
/// El identificador es corto y decible a propósito: hay que poder nombrarlo hablando, porque la
/// mayoría de las veces se lo va a mencionar en voz alta y no escribiéndolo.
/// </remarks>
public sealed record Goal(
    string Id,
    string Description,
    GoalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset TouchedAt,
    string? NextStep,
    IReadOnlyList<string> Progress);

/// <summary>
/// Objetivos que sobreviven a que cierres la charla.
/// </summary>
/// <remarks>
/// Es la diferencia entre un asistente que responde y uno que te acompaña. Hasta acá, cada
/// conversación arrancaba de cero: decir «seguí con eso» al día siguiente no significaba nada porque
/// no quedaba nada de lo anterior. Un objetivo persistente es lo que hace que esa frase tenga
/// referente.
/// <para>
/// Se guardan pocos y sólo los que el usuario declaró. No se infieren de la charla: un asistente que
/// se inventa objetivos que nadie le pidió termina persiguiendo cosas que a nadie le importan, y
/// peor, recordándotelas.
/// </para>
/// </remarks>
public sealed class GoalBook
{
    /// <summary>Tope de objetivos activos. Más que esto no es una lista de metas: es una carpeta.</summary>
    private const int MaximumActive = 12;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Goal>? _goals;

    public GoalBook(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "objetivos.json");

    public string FilePath => _path;

    public async Task<Goal> OpenAsync(
        string description,
        string? nextStep = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var goals = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var goal = new Goal(
                NextId(goals),
                Shorten(description, 160),
                GoalStatus.Active,
                DateTimeOffset.Now,
                DateTimeOffset.Now,
                nextStep is null ? null : Shorten(nextStep, 160),
                []);

            goals.Add(goal);

            // Si hay demasiados activos, el más viejo sin tocar se abandona solo. Un objetivo que
            // lleva semanas sin novedades ya no es un objetivo: es una deuda que nadie reconoce.
            var active = goals.Where(item => item.Status == GoalStatus.Active).ToList();
            if (active.Count > MaximumActive)
            {
                var stale = active.OrderBy(item => item.TouchedAt).First();
                goals[goals.IndexOf(stale)] = stale with { Status = GoalStatus.Abandoned };
            }

            await SaveUnsafeAsync(goals, cancellationToken).ConfigureAwait(false);
            return goal;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Anota un avance. Sin identificador, cae sobre el último que se tocó — que es a lo que se
    /// refiere «seguí con eso» cuando nadie aclara nada.
    /// </summary>
    public async Task<Goal?> RecordProgressAsync(
        string note,
        string? id = null,
        string? nextStep = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var goals = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var target = Resolve(goals, id);
            if (target is null)
            {
                return null;
            }

            var progress = target.Progress.Append(Shorten(note, 140)).TakeLast(8).ToArray();
            var updated = target with
            {
                Progress = progress,
                NextStep = nextStep is null ? target.NextStep : Shorten(nextStep, 160),
                TouchedAt = DateTimeOffset.Now
            };

            goals[goals.IndexOf(target)] = updated;
            await SaveUnsafeAsync(goals, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Goal?> CloseAsync(
        string? id = null,
        bool abandoned = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var goals = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var target = Resolve(goals, id);
            if (target is null)
            {
                return null;
            }

            var closed = target with
            {
                Status = abandoned ? GoalStatus.Abandoned : GoalStatus.Done,
                TouchedAt = DateTimeOffset.Now
            };

            goals[goals.IndexOf(target)] = closed;
            await SaveUnsafeAsync(goals, cancellationToken).ConfigureAwait(false);
            return closed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resumen de lo abierto, para inyectar en el turno. <c>null</c> si no hay nada pendiente.
    /// </summary>
    /// <remarks>
    /// Va sólo lo activo y con el próximo paso adelante, porque es lo único que hace falta para que
    /// «seguí con eso» funcione. Volcar el historial entero de cada objetivo en cada turno costaría
    /// tokens en todos los pedidos para servir a unos pocos.
    /// </remarks>
    public async Task<string?> DescribeOpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var goals = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var active = goals
                .Where(goal => goal.Status == GoalStatus.Active)
                .OrderByDescending(goal => goal.TouchedAt)
                .ToArray();

            if (active.Length == 0)
            {
                return null;
            }

            var builder = new StringBuilder("Objetivos abiertos del usuario, del más reciente al más viejo:");
            foreach (var goal in active)
            {
                builder.AppendLine().Append("- [").Append(goal.Id).Append("] ").Append(goal.Description);
                if (!string.IsNullOrWhiteSpace(goal.NextStep))
                {
                    builder.Append(" · próximo: ").Append(goal.NextStep);
                }

                builder.Append(" · ").Append(Ago(goal.TouchedAt));
            }

            builder.AppendLine();
            builder.Append(
                "Cuando diga «seguí con eso», «dónde quedamos» o algo así sin aclarar cuál, es el primero de la lista.");
            return builder.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Goal>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Goal? Resolve(List<Goal> goals, string? id)
    {
        var active = goals.Where(goal => goal.Status == GoalStatus.Active).ToList();
        if (active.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return active.OrderByDescending(goal => goal.TouchedAt).First();
        }

        var needle = id.Trim();
        return active.FirstOrDefault(goal =>
                string.Equals(goal.Id, needle, StringComparison.OrdinalIgnoreCase)) ??
            active.FirstOrDefault(goal =>
                goal.Description.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Identificadores cortos y decibles: hay que poder nombrarlos hablando.</summary>
    private static string NextId(List<Goal> goals)
    {
        var used = goals.Select(goal => goal.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; number < 1000; number++)
        {
            var candidate = $"o{number}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        return $"o{goals.Count + 1}";
    }

    private static string Ago(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.Now - moment;
        return elapsed.TotalMinutes < 60
            ? "recién"
            : elapsed.TotalHours < 24
                ? $"hace {(int)elapsed.TotalHours} h"
                : $"hace {(int)elapsed.TotalDays} días";
    }

    private static string Shorten(string value, int maximum)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    private async Task<List<Goal>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_goals is not null)
        {
            return _goals;
        }

        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                _goals = await JsonSerializer
                    .DeserializeAsync<List<Goal>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? [];
                return _goals;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un archivo ilegible no puede impedir usar el asistente.
        }

        _goals = [];
        return _goals;
    }

    private async Task SaveUnsafeAsync(List<Goal> goals, CancellationToken cancellationToken)
    {
        _goals = goals;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, goals, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Perder un objetivo es grave, pero tumbar el turno lo es más.
        }
    }
}
