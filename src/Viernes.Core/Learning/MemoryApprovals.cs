using System.Globalization;
using System.Text;
using Viernes.Memory;
using Viernes.Memory.Models;
using Viernes.Memory.Privacy;

namespace Viernes.Core.Learning;

/// <summary>
/// El mostrador donde lo que Viernes creyó notar se convierte —o no— en algo que sabe.
/// </summary>
/// <remarks>
/// Al cerrar una charla se destilan hechos sobre el usuario y se guardan como observaciones
/// temporales que vencen solas. La regla de que una suposición nunca se vuelva permanente sin
/// aprobación está bien y no se toca; el problema era que no existía ninguna forma de aprobar. Con
/// eso, Viernes aprendía todas las noches y se olvidaba todas las noches: siete días después la
/// observación vencía y el archivo quedaba igual que antes.
/// <para>
/// Acá está la salida. No construye nada nuevo: <see cref="IPersonalMemoryStore"/> ya tenía
/// <c>SuggestAsync</c>, <c>ApproveSuggestionAsync</c> y <c>RejectSuggestionAsync</c> escritos y sin
/// usar. Lo que faltaba era el camino desde una observación temporal hasta ahí, y una forma de
/// nombrar lo pendiente que sirva hablando: nadie va a dictar un identificador hexadecimal, así que
/// también se resuelve por lo que dice el recuerdo.
/// </para>
/// </remarks>
public sealed class MemoryApprovals
{
    private readonly IPersonalMemoryStore _store;

    public MemoryApprovals(IPersonalMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Todo lo que está esperando una decisión: sugerencias y observaciones temporales.</summary>
    public async Task<IReadOnlyList<PendingMemory>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var review = await _store.ReviewAsync(cancellationToken).ConfigureAwait(false);

        // Una observación que ya fue propuesta no se cuenta dos veces. Si apareciera la sugerencia y
        // además la observación que la originó, cualquier referencia hablada al texto coincidiría
        // con las dos y la aprobación contestaría «eso coincide con más de una» para siempre.
        var proposed = review.Suggestions.Select(item => item.BasedOnObservationId).OfType<Guid>().ToHashSet();
        var proposedContent = review.Suggestions
            .Select(item => item.Content)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. review.Suggestions
                .Select(item => new PendingMemory(item.Id, item.Content, IsSuggestion: true, item.ExpiresAt)),
            .. review.TemporaryObservations
                .Where(item => !proposed.Contains(item.Id) && !proposedContent.Contains(item.Content))
                .Select(item => new PendingMemory(item.Id, item.Content, IsSuggestion: false, item.ExpiresAt))
        ];
    }

    /// <summary>
    /// Lo pendiente, escrito para que el modelo pueda mencionarlo cuando venga al caso.
    /// </summary>
    /// <remarks>
    /// Va aparte y avisado de que no está confirmado. Mezclarlo con lo que el usuario pidió recordar
    /// es cómo un asistente empieza a afirmar cosas que nadie le dijo; no mencionarlo nunca es cómo
    /// lo pendiente se muere sin que el usuario se entere de que existía.
    /// </remarks>
    public async Task<string?> DescribePendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await ListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(
            "Cosas que creés haber notado y que él NO confirmó. No las des por ciertas ni las " +
            "menciones como si las supieras; si viene al caso, preguntale, y si te dice que sí " +
            "usá la herramienta «memoria» con accion=aprobar:");
        foreach (var item in pending.Take(10))
        {
            builder.AppendLine();
            builder.Append($"- [{item.ShortId}] {item.Content}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// La línea de contexto completa: lo que el usuario pidió recordar y, aparte, lo pendiente.
    /// </summary>
    public async Task<string?> DescribeForPromptAsync(CancellationToken cancellationToken = default)
    {
        var review = await _store.ReviewAsync(cancellationToken).ConfigureAwait(false);
        var sections = new List<string>();

        if (review.Explicit.Count > 0)
        {
            var known = review.Explicit
                .Take(20)
                .Select(item => $"- {item.Content}");
            sections.Add("Lo que sabés del usuario porque te lo pidió él:\n" + string.Join('\n', known));
        }

        var pending = await DescribePendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            sections.Add(pending);
        }

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    /// <summary>
    /// Convierte en permanente algo pendiente. Es el único camino por el que una suposición se
    /// vuelve un hecho, y siempre empieza en el usuario.
    /// </summary>
    /// <remarks>
    /// Una observación temporal no se puede aprobar de una: el store sólo sabe aprobar sugerencias.
    /// Se la propone y se la aprueba en el mismo movimiento, porque el consentimiento explícito del
    /// usuario ya es lo que la sugerencia estaba esperando; partirlo en dos pasos sería pedirle dos
    /// veces lo mismo.
    /// </remarks>
    public async Task<MemoryApprovalOutcome> ApproveAsync(
        string? reference,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        if (resolution.Item is not { } pending)
        {
            return new MemoryApprovalOutcome(false, resolution.Message);
        }

        try
        {
            var suggestionId = pending.IsSuggestion
                ? pending.Id
                : (await _store.SuggestAsync(pending.Content, pending.Id, cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).Id;

            var approved = await _store.ApproveSuggestionAsync(suggestionId, cancellationToken)
                .ConfigureAwait(false);
            return new MemoryApprovalOutcome(true, $"Listo, lo recuerdo: {approved.Content}");
        }
        catch (MemoryItemNotFoundException)
        {
            return new MemoryApprovalOutcome(false, "Ese dato ya no está pendiente; puede haber vencido.");
        }
        catch (MemoryCapacityExceededException)
        {
            // Va antes que InvalidOperationException: hereda de ella, y al revés nunca se alcanzaría.
            return new MemoryApprovalOutcome(
                false,
                "La memoria está llena. Borrá algo con la acción olvidar y volvé a intentar.");
        }
        catch (MemoryContentRejectedException)
        {
            return new MemoryApprovalOutcome(
                false,
                "No lo guardé: la memoria rechaza secretos, credenciales y conversaciones enteras.");
        }
        catch (InvalidOperationException)
        {
            // El store rechaza proponer algo que ya es explícito. Para el usuario no es un error.
            return new MemoryApprovalOutcome(true, $"Eso ya lo tenía guardado: {pending.Content}");
        }
    }

    /// <summary>Descarta algo pendiente para que no vuelva a aparecer.</summary>
    public async Task<MemoryApprovalOutcome> RejectAsync(
        string? reference,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        if (resolution.Item is not { } pending)
        {
            return new MemoryApprovalOutcome(false, resolution.Message);
        }

        var removed = pending.IsSuggestion
            ? await _store.RejectSuggestionAsync(pending.Id, cancellationToken).ConfigureAwait(false)
            : await _store.ForgetAsync(pending.Id, cancellationToken).ConfigureAwait(false);

        return new MemoryApprovalOutcome(
            removed,
            removed ? $"Lo descarto: {pending.Content}" : "Eso ya no estaba pendiente.");
    }

    /// <summary>
    /// Guarda un hecho como permanente porque el usuario lo pidió con todas las letras.
    /// </summary>
    public async Task<MemoryApprovalOutcome> RememberAsync(
        string fact,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fact))
        {
            return new MemoryApprovalOutcome(false, "Necesito saber qué guardar.");
        }

        try
        {
            var memory = await _store.AddExplicitAsync(fact, cancellationToken).ConfigureAwait(false);
            return new MemoryApprovalOutcome(true, $"Guardado: {memory.Content}");
        }
        catch (MemoryContentRejectedException)
        {
            return new MemoryApprovalOutcome(
                false,
                "No lo guardé: la memoria rechaza secretos, credenciales y conversaciones enteras.");
        }
        catch (MemoryCapacityExceededException)
        {
            return new MemoryApprovalOutcome(false, "La memoria está llena.");
        }
    }

    /// <summary>Borra cualquier dato de la memoria, sea explícito, temporal o sugerido.</summary>
    public async Task<MemoryApprovalOutcome> ForgetAsync(
        string? reference,
        CancellationToken cancellationToken = default)
    {
        var items = await _store.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = items
            .Select(item => new PendingMemory(
                item.Id,
                item.Content,
                item.Kind == PersonalMemoryKind.Suggestion,
                item.UpdatedAt))
            .ToArray();

        var resolution = Resolve(candidates, reference);
        if (resolution.Item is not { } target)
        {
            return new MemoryApprovalOutcome(false, resolution.Message);
        }

        var forgotten = await _store.ForgetAsync(target.Id, cancellationToken).ConfigureAwait(false);
        return new MemoryApprovalOutcome(
            forgotten,
            forgotten ? $"Lo olvidé: {target.Content}" : "Ese dato ya no estaba en la memoria.");
    }

    /// <summary>El aviso de privacidad, para poder repetirlo cuando el usuario pregunte.</summary>
    public static string PrivacyNotice => MemoryPrivacy.Notice;

    private async Task<Resolution> ResolveAsync(string? reference, CancellationToken cancellationToken)
    {
        var pending = await ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(pending, reference);
    }

    /// <summary>
    /// Encuentra a cuál se refiere el usuario.
    /// </summary>
    /// <remarks>
    /// Por identificador corto, por lo que dice el recuerdo, o por descarte cuando hay uno solo.
    /// Los tres caminos existen porque esto llega hablando: «sí, acordate de eso» no trae ningún
    /// identificador, y exigirlo convertiría la aprobación en algo que sólo se puede hacer tecleando
    /// —que es exactamente el estado del que venimos.
    /// </remarks>
    private static Resolution Resolve(IReadOnlyList<PendingMemory> candidates, string? reference)
    {
        if (candidates.Count == 0)
        {
            return new Resolution(null, "No hay nada pendiente de aprobar.");
        }

        var needle = reference?.Trim() ?? string.Empty;
        if (needle.Length == 0)
        {
            return candidates.Count == 1
                ? new Resolution(candidates[0], string.Empty)
                : new Resolution(null, "Hay varias cosas pendientes; decime cuál.");
        }

        var byId = candidates
            .Where(item => needle.Length >= 4 &&
                item.Id.ToString("N").StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (byId.Length == 1)
        {
            return new Resolution(byId[0], string.Empty);
        }

        var folded = Fold(needle);
        var byContent = candidates
            .Where(item => Fold(item.Content).Contains(folded, StringComparison.Ordinal))
            .ToArray();

        return byContent.Length switch
        {
            1 => new Resolution(byContent[0], string.Empty),
            0 => new Resolution(null, "No encontré nada pendiente que diga eso."),
            _ => new Resolution(null, "Eso coincide con más de una; decime cuál con más precisión.")
        };
    }

    /// <summary>Sin acentos y en minúsculas: hablando, los acentos los pone el transcriptor.</summary>
    private static string Fold(string value)
    {
        var normalized = value.ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record Resolution(PendingMemory? Item, string Message);
}

/// <summary>
/// Algo que Viernes cree haber notado y que todavía no es un hecho.
/// </summary>
/// <param name="Id">Identificador completo, el que usa el store.</param>
/// <param name="Content">El hecho destilado, en una línea.</param>
/// <param name="IsSuggestion">
/// Si ya fue propuesto formalmente; si no, es una observación temporal que vence sola.
/// </param>
/// <param name="ExpiresAt">Cuándo se muere si nadie decide nada.</param>
public sealed record PendingMemory(
    Guid Id,
    string Content,
    bool IsSuggestion,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Los ocho primeros dígitos alcanzan para nombrarlo sin dictar treinta y dos.</summary>
    public string ShortId => Id.ToString("N")[..8];
}

/// <summary>
/// Qué pasó con la decisión y qué contarle al usuario.
/// </summary>
/// <param name="Succeeded">Si la memoria quedó como se pidió.</param>
/// <param name="Message">Una línea lista para decir en voz alta.</param>
public sealed record MemoryApprovalOutcome(bool Succeeded, string Message);
