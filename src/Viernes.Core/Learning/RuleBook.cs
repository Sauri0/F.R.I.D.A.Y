using System.Text;
using System.Text.Json;

namespace Viernes.Core.Learning;

/// <summary>Una instrucción que el usuario dio y que vale para siempre, no para un pedido.</summary>
public sealed record LearnedRule(string Text, DateTimeOffset LearnedAt, int TimesRecalled);

/// <summary>
/// Lo que el usuario le enseñó explícitamente: cómo quiere que trabaje, qué prefiere, qué no repetir.
/// </summary>
/// <remarks>
/// Es la pieza que faltaba y que hacía que no pareciera aprender nunca. Había dos memorias —el
/// recetario de procedimientos y los hechos destilados al cerrar una charla— y ninguna de las dos
/// servía para lo más directo: que le digas «acordate que cuando te pido una canción la ponés en
/// Spotify» y que eso valga desde ese momento y para siempre. Medido en el registro real, el intento
/// del usuario de enseñarle algo terminó resolviéndose como una búsqueda web.
/// <para>
/// A diferencia del recetario, esto <b>no se recupera por parecido</b>: se inyecta entero en cada
/// turno. Son pocas reglas y son las que el usuario eligió a mano; filtrarlas por similitud con lo
/// que acaba de decir es exactamente cómo una regla se olvida justo cuando hacía falta.
/// </para>
/// </remarks>
public sealed class RuleBook
{
    /// <summary>
    /// Tope de reglas. No es una limitación técnica: pasado cierto punto, un conjunto de reglas deja
    /// de ser una guía y se vuelve un reglamento que el modelo aplica mal y el usuario no recuerda
    /// haber escrito.
    /// </summary>
    private const int MaximumRules = 40;

    private const int MaximumLength = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<LearnedRule>? _rules;

    public RuleBook(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "reglas.json");

    public string FilePath => _path;

    /// <summary>Guarda una regla. Si ya existe una igual, no la duplica.</summary>
    public async Task<bool> RememberAsync(string rule, CancellationToken cancellationToken = default)
    {
        var text = Normalize(rule);
        if (text.Length < 6)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (rules.Any(existing => string.Equals(existing.Text, text, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            rules.Add(new LearnedRule(text, DateTimeOffset.Now, 0));
            if (rules.Count > MaximumRules)
            {
                // Se cae la más vieja: si nunca se usó y hay cuarenta encima, dejó de ser una guía.
                rules.RemoveAt(0);
            }

            await SaveUnsafeAsync(rules, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Olvida una regla por coincidencia parcial. Poder desaprender es parte de aprender.</summary>
    public async Task<string?> ForgetAsync(string fragment, CancellationToken cancellationToken = default)
    {
        var needle = Normalize(fragment);
        if (needle.Length < 3)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var match = rules.FirstOrDefault(rule =>
                rule.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            rules.Remove(match);
            await SaveUnsafeAsync(rules, cancellationToken).ConfigureAwait(false);
            return match.Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Todas las reglas, listas para inyectar. <c>null</c> si todavía no hay ninguna.</summary>
    public async Task<string?> RecallAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (rules.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder(
                "Lo que el usuario te enseñó y vale siempre. Respetalo aunque no lo repita:");
            foreach (var rule in rules)
            {
                builder.AppendLine().Append("- ").Append(rule.Text);
            }

            return builder.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LearnedRule>> ListAsync(CancellationToken cancellationToken = default)
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

    private static string Normalize(string value)
    {
        var trimmed = value.Trim().TrimEnd('.', ' ');
        return trimmed.Length <= MaximumLength ? trimmed : trimmed[..MaximumLength];
    }

    private async Task<List<LearnedRule>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_rules is not null)
        {
            return _rules;
        }

        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                _rules = await JsonSerializer
                    .DeserializeAsync<List<LearnedRule>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? [];
                return _rules;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un archivo ilegible no puede impedir usar el asistente: se arranca sin reglas.
        }

        _rules = [];
        return _rules;
    }

    private async Task SaveUnsafeAsync(List<LearnedRule> rules, CancellationToken cancellationToken)
    {
        _rules = rules;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, rules, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Que no se pueda escribir es una pérdida real, pero no puede tumbar el turno.
        }
    }
}
