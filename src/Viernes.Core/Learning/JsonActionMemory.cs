using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Viernes.Core.Learning;

/// <summary>
/// Recetario en disco: cada intento de acción deja anotado qué se pidió, con qué se resolvió y si
/// salió bien. Lo aprendido vuelve al modelo como contexto en los pedidos parecidos.
/// </summary>
/// <remarks>
/// Es lo que hace que mejore con el uso sin cambiar el modelo ni el código. La primera vez que le
/// pedís una canción tiene que elegir la herramienta razonando; la segunda ya sabe que
/// <c>play_music</c> anduvo, y la tercera sabe también qué nombres <em>no</em> resuelven. Nada de
/// esto sale del equipo ni requiere entrenar nada.
/// </remarks>
public sealed class JsonActionMemory : IActionMemory
{
    private const int MaximumEntries = 400;
    private const int MaximumRecalled = 6;
    private const int MaximumRequestLength = 160;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Palabras que aparecen en todo pedido y no distinguen nada al comparar.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "un", "una", "de", "del", "en", "a", "por", "para", "con", "que",
        "me", "te", "se", "lo", "mi", "tu", "y", "o", "al", "es", "esta", "este", "eso", "esto",
        "porfavor", "favor", "dale", "che", "viernes"
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Entry>? _entries;

    public JsonActionMemory(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Viernes",
            "aprendido.json");

    public string FilePath => _path;

    public async Task<string?> RecallAsync(string request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return null;
        }

        var wanted = Tokenize(request);
        if (wanted.Count == 0)
        {
            return null;
        }

        var verb = LeadingVerb(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var relevant = entries
                .Select(entry => (Entry: entry, Score: Score(wanted, verb, entry.Request)))
                .Where(candidate => candidate.Score >= 0.34)
                // Lo que más se parece primero y, a igual parecido, lo que más veces funcionó.
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Entry.Times)
                .Take(MaximumRecalled)
                .ToArray();

            // Si nada se parece, se muestran igual los métodos que más funcionaron.
            //
            // Medido sobre el uso real: de 49 recetas guardadas, 47 se habían usado una sola vez.
            // O sea que el recuerdo por parecido casi nunca disparaba, y la razón es que la clave es
            // la transcripción cruda —«Notify y pone Crip de Radiohead» contra «Quiero que abra
            // Spotify y me pongas la canción de Radiohead de Crip»—: dos formas del mismo pedido que
            // no comparten casi ninguna palabra. Exigir parecido entre transcripciones ruidosas es
            // pedirle al azar que coincida dos veces.
            if (relevant.Length == 0)
            {
                relevant = entries
                    .Where(entry => entry.Succeeded)
                    .OrderByDescending(entry => entry.Times)
                    .ThenByDescending(entry => entry.LastUsed)
                    .Take(5)
                    .Select(entry => (Entry: entry, Score: 0d))
                    .ToArray();
            }

            if (relevant.Length == 0)
            {
                return null;
            }

            var builder = new StringBuilder("Lo que ya aprendiste haciendo esto antes:");
            foreach (var (entry, _) in relevant)
            {
                builder.AppendLine();
                builder.Append(entry.Succeeded ? "- Funcionó: «" : "- NO funcionó: «");
                builder.Append(entry.Request);
                builder.Append("» → ");
                builder.Append(entry.Action);
                if (!string.IsNullOrWhiteSpace(entry.Target))
                {
                    builder.Append(" con target «").Append(entry.Target).Append('»');
                }

                if (!entry.Succeeded && !string.IsNullOrWhiteSpace(entry.Outcome))
                {
                    builder.Append(" (").Append(entry.Outcome).Append("). No lo repitas igual");
                }
            }

            builder.AppendLine();
            builder.Append("Repetí lo que funcionó y evitá lo que falló.");
            return builder.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(
        string request,
        string action,
        string? target,
        bool succeeded,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request) || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var trimmedRequest = Shorten(request);
            var trimmedTarget = string.IsNullOrWhiteSpace(target) ? null : Shorten(target);

            // Repetir el mismo pedido no agrega una fila: sube el contador, que es lo que después
            // ordena qué método se recuerda primero.
            var existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Request, trimmedRequest, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Action, action, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Target, trimmedTarget, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Times++;
                existing.Succeeded = succeeded;
                existing.Outcome = Shorten(outcome);
                existing.LastUsed = DateTimeOffset.Now;
            }
            else
            {
                entries.Add(new Entry
                {
                    Request = trimmedRequest,
                    Action = action,
                    Target = trimmedTarget,
                    Succeeded = succeeded,
                    Outcome = Shorten(outcome),
                    Times = 1,
                    LastUsed = DateTimeOffset.Now
                });
            }

            if (entries.Count > MaximumEntries)
            {
                // Se cae lo menos usado y más viejo: el recetario tiene que envejecer, no crecer.
                entries = entries
                    .OrderByDescending(entry => entry.Times)
                    .ThenByDescending(entry => entry.LastUsed)
                    .Take(MaximumEntries)
                    .ToList();
                _entries = entries;
            }

            await SaveUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<Entry>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                _entries = await JsonSerializer
                    .DeserializeAsync<List<Entry>>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false) ?? [];
                return _entries;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Un recetario ilegible no puede impedir usar el asistente: se arranca de cero.
        }

        _entries = [];
        return _entries;
    }

    private async Task SaveUnsafeAsync(List<Entry> entries, CancellationToken cancellationToken)
    {
        _entries = entries;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var stream = File.Create(_path);
            await JsonSerializer
                .SerializeAsync(stream, entries, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Aprender es una mejora, no un requisito: si no se puede escribir, sigue funcionando.
        }
    }

    private static string Shorten(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaximumRequestLength ? trimmed : trimmed[..MaximumRequestLength];
    }

    /// <summary>
    /// Parecido entre pedidos, con el verbo pesando más que el resto.
    /// </summary>
    /// <remarks>
    /// Sólo contar palabras compartidas no sirve para recordar métodos: «poné Bohemian Rhapsody» y
    /// «poné Creep de Radiohead» comparten únicamente «poné», y sin embargo se resuelven exactamente
    /// igual. El nombre de la canción es justo la parte que cambia, y el verbo es lo que identifica
    /// la receta. Por eso un verbo en común ya alcanza para recordar, y las palabras compartidas
    /// suman por encima de eso.
    /// </remarks>
    private static double Score(HashSet<string> wanted, string? verb, string candidate)
    {
        var other = Tokenize(candidate);
        var overlap = Overlap(wanted, other);
        var sameVerb = verb is not null &&
            string.Equals(verb, LeadingVerb(candidate), StringComparison.Ordinal);
        return sameVerb ? Math.Max(overlap, 0.5) : overlap;
    }

    /// <summary>Primera palabra con contenido: en un pedido hablado, es el verbo de la acción.</summary>
    private static string? LeadingVerb(string text) => Tokenize(text, keepOrder: true).FirstOrDefault();

    /// <summary>Parecido por palabras compartidas sobre el conjunto total: barato y suficiente.</summary>
    private static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var shared = left.Count(token => right.Contains(token));
        return (double)shared / Math.Max(left.Count, right.Count);
    }

    private static HashSet<string> Tokenize(string text) =>
        Tokenize(text, keepOrder: false).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Tokenize(string text, bool keepOrder)
    {
        _ = keepOrder;
        var folded = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        foreach (var character in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1 && !StopWords.Contains(token));
    }

    private sealed class Entry
    {
        public string Request { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string? Target { get; set; }

        public bool Succeeded { get; set; }

        public string Outcome { get; set; } = string.Empty;

        public int Times { get; set; }

        public DateTimeOffset LastUsed { get; set; }
    }
}
