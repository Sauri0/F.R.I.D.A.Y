using System.Globalization;
using System.Text.Json;
using Viernes.Core.Conversation;
using Viernes.Core.Tools;
using Viernes.Memory;
using Viernes.Memory.Models;
using Viernes.Memory.Privacy;

namespace Viernes.App.Services;

/// <summary>Una fila de la burbuja: cuándo, qué, y una etiqueta corta opcional.</summary>
internal sealed record BubbleListItem(string When, string What, string? Tag = null);

/// <summary>
/// Qué clase de lista es, porque no todas piden la misma forma.
/// </summary>
/// <remarks>
/// Una agenda es una línea de tiempo: horizontal se lee de un vistazo y no crece, así que le alcanza
/// una tira baja. Una memoria son registros con identificador, y eso pide fila completa. Son datos
/// distintos y por eso llevan forma distinta — dar 176 px a una agenda de dos eventos deja un vacío
/// que se lee como error.
/// </remarks>
internal enum BubbleListKind
{
    Agenda,
    Memoria
}

internal sealed record LocalCommandOutcome(
    string Text,
    ToolExecutionResult? ToolResult = null,
    IReadOnlyList<BubbleListItem>? Items = null,
    BubbleListKind ListKind = BubbleListKind.Agenda);

/// <summary>
/// Small, deterministic command surface that remains useful without any cloud model.
/// It accepts explicit syntax only and still routes every side effect through the Core policy.
/// </summary>
internal sealed class LocalCommandRouter
{
    private static readonly CultureInfo ArgentineSpanish = CultureInfo.GetCultureInfo("es-AR");
    private readonly ConversationOrchestrator _orchestrator;
    private readonly IPersonalMemoryStore _memory;

    public LocalCommandRouter(ConversationOrchestrator orchestrator, IPersonalMemoryStore memory)
    {
        _orchestrator = orchestrator;
        _memory = memory;
    }

    public async Task<LocalCommandOutcome?> TryExecuteAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var text = input.Trim();
        var normalized = text.ToLowerInvariant();

        if (normalized is "/ayuda" or "/help")
        {
            return new LocalCommandOutcome(
                "Modo local: /recordatorios, /recordar FECHA | TEXTO, /agenda, " +
                "/evento FECHA | TÍTULO, /buscar CONSULTA, /pc ACCIÓN, /memoria, " +
                "/recordá que DATO, /olvidar ID o /pausar hábitos.");
        }

        if (normalized is "/memoria" or "qué recordás de mí" or "mostrame mi memoria")
        {
            return await ReviewMemoryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (normalized.StartsWith("/recordá que ", StringComparison.Ordinal) ||
            normalized.StartsWith("/recorda que ", StringComparison.Ordinal))
        {
            var separator = text.IndexOf(" que ", StringComparison.OrdinalIgnoreCase);
            var fact = separator >= 0 ? text[(separator + 5)..].Trim() : string.Empty;
            if (fact.Length == 0)
            {
                return new LocalCommandOutcome("Formato: /recordá que prefiero reuniones por la mañana");
            }

            try
            {
                var memory = await _memory.AddExplicitAsync(fact, cancellationToken).ConfigureAwait(false);
                return new LocalCommandOutcome($"Lo guardé como recuerdo explícito ({ShortId(memory.Id)}). Podés revisarlo o borrarlo cuando quieras.");
            }
            catch (MemoryContentRejectedException)
            {
                return new LocalCommandOutcome("No guardé ese dato: la memoria rechaza secretos, credenciales y contenido con forma de conversación.");
            }
        }

        if (normalized.StartsWith("/olvidar ", StringComparison.Ordinal))
        {
            var item = await ResolveMemoryItemAsync(text[9..], cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return new LocalCommandOutcome("No encontré un recuerdo único con ese ID. Usá /memoria para revisar los IDs.");
            }

            var forgotten = await _memory.ForgetAsync(item.Id, cancellationToken).ConfigureAwait(false);
            return new LocalCommandOutcome(forgotten
                ? $"Olvidé el dato {ShortId(item.Id)}."
                : "Ese dato ya no estaba en la memoria.");
        }

        if (normalized.StartsWith("/editar memoria ", StringComparison.Ordinal))
        {
            var pieces = text[16..].Split('|', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2)
            {
                return new LocalCommandOutcome("Formato: /editar memoria ID | dato corregido");
            }

            var item = await ResolveMemoryItemAsync(pieces[0], cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return new LocalCommandOutcome("No encontré un recuerdo único con ese ID.");
            }

            try
            {
                await _memory.EditAsync(item.Id, pieces[1], cancellationToken).ConfigureAwait(false);
                return new LocalCommandOutcome($"Actualicé el dato {ShortId(item.Id)}.");
            }
            catch (MemoryContentRejectedException)
            {
                return new LocalCommandOutcome("No guardé la edición: la memoria rechaza secretos y conversaciones completas.");
            }
        }

        if (normalized is "/pausar hábitos" or "/pausar habitos")
        {
            await _memory.PauseObservationAsync(cancellationToken).ConfigureAwait(false);
            return new LocalCommandOutcome("Las observaciones de hábitos están pausadas. Los recuerdos explícitos siguen bajo tu control.");
        }

        if (normalized is "/reanudar hábitos" or "/reanudar habitos")
        {
            await _memory.ResumeObservationAsync(cancellationToken).ConfigureAwait(false);
            return new LocalCommandOutcome("Las observaciones temporales están habilitadas; ninguna se volverá permanente sin tu aprobación.");
        }

        if (normalized is "/recordatorios" or "mis recordatorios" or "mostrame mis recordatorios")
        {
            return await ExecuteAsync("reminder_list", new { }, cancellationToken).ConfigureAwait(false);
        }

        if (normalized is "/agenda" or "mi agenda" or "mostrame mi agenda")
        {
            return await ExecuteAsync("agenda_list", new { }, cancellationToken).ConfigureAwait(false);
        }

        if (normalized.StartsWith("/buscar ", StringComparison.Ordinal))
        {
            var query = text[8..].Trim();
            return query.Length == 0
                ? new LocalCommandOutcome("Usá /buscar seguido de una consulta.")
                : await ExecuteAsync("web_search", new { query }, cancellationToken).ConfigureAwait(false);
        }

        if (normalized.StartsWith("/pc ", StringComparison.Ordinal))
        {
            var pieces = text[4..].Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length == 0)
            {
                return new LocalCommandOutcome("Usá /pc ACCIÓN y, opcionalmente, un destino.");
            }

            return await ExecuteAsync(
                "pc_action",
                new { action = pieces[0], target = pieces.Length > 1 ? pieces[1] : null },
                cancellationToken).ConfigureAwait(false);
        }

        if (normalized.StartsWith("/recordar ", StringComparison.Ordinal))
        {
            var pieces = text[10..].Split('|', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !TryParseLocalDate(pieces[0], out var dueAt) || string.IsNullOrWhiteSpace(pieces[1]))
            {
                return new LocalCommandOutcome(
                    "Formato: /recordar 2026-08-17 09:00 | llamar a Ana");
            }

            return await ExecuteAsync(
                "reminder_create",
                new { title = pieces[1], due_at = dueAt.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken).ConfigureAwait(false);
        }

        if (normalized.StartsWith("/evento ", StringComparison.Ordinal))
        {
            var pieces = text[8..].Split('|', 3, StringSplitOptions.TrimEntries);
            if (pieces.Length < 2 || !TryParseLocalDate(pieces[0], out var startsAt) || string.IsNullOrWhiteSpace(pieces[1]))
            {
                return new LocalCommandOutcome(
                    "Formato: /evento 2026-08-17 15:30 | Reunión | notas opcionales");
            }

            return await ExecuteAsync(
                "agenda_create",
                new
                {
                    title = pieces[1],
                    starts_at = startsAt.ToString("O", CultureInfo.InvariantCulture),
                    notes = pieces.Length > 2 ? pieces[2] : null
                },
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<LocalCommandOutcome> ExecuteAsync<T>(
        string toolName,
        T arguments,
        CancellationToken cancellationToken)
    {
        var result = await _orchestrator.ExecuteLocalToolAsync(
            toolName,
            JsonSerializer.SerializeToElement(arguments),
            confirmationGranted: false,
            cancellationToken).ConfigureAwait(false);
        return new LocalCommandOutcome(FormatResult(result), result, ExtractItems(result));
    }

    /// <summary>
    /// Convierte el arreglo devuelto por una tool de listado en filas. La burbuja alta las muestra
    /// completas en vez de recortarlas a tres en una sola línea de texto.
    /// </summary>
    private static IReadOnlyList<BubbleListItem>? ExtractItems(ToolExecutionResult result)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Array } data || data.GetArrayLength() == 0)
        {
            return null;
        }

        return data.EnumerateArray()
            .Take(5)
            .Select(item =>
            {
                var title = ReadString(item, "title") ?? "Sin título";
                var when = ReadString(item, "dueAt") ?? ReadString(item, "startsAt");
                return new BubbleListItem(when is null ? string.Empty : FormatDate(when), title);
            })
            .ToArray();
    }

    private async Task<LocalCommandOutcome> ReviewMemoryAsync(CancellationToken cancellationToken)
    {
        var review = await _memory.ReviewAsync(cancellationToken).ConfigureAwait(false);
        if (review.TotalCount == 0)
        {
            return new LocalCommandOutcome(review.IsObservationPaused
                ? "La memoria está vacía y las observaciones de hábitos están pausadas. Usá /recordá que … para guardar algo explícito."
                : "La memoria está vacía. Las observaciones son temporales y requieren aprobación para volverse permanentes.");
        }

        // El ID corto va en la primera columna porque es lo que hace falta para poder olvidar algo.
        var items = review.Explicit.Cast<PersonalMemoryItem>()
            .Concat(review.TemporaryObservations)
            .Concat(review.Suggestions)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(5)
            .Select(item => new BubbleListItem(
                ShortId(item.Id),
                item.Content,
                MemoryKindLabel(item.Kind).ToLowerInvariant()))
            .ToArray();

        var observationState = review.IsObservationPaused ? "hábitos pausados" : "hábitos temporales activos";
        var summary = $"{review.TotalCount} dato{(review.TotalCount == 1 ? "" : "s")} · {observationState} · /olvidar ID para borrar";
        return new LocalCommandOutcome(summary, Items: items, ListKind: BubbleListKind.Memoria);
    }

    private async Task<PersonalMemoryItem?> ResolveMemoryItemAsync(
        string idPrefix,
        CancellationToken cancellationToken)
    {
        var normalized = idPrefix.Trim();
        if (normalized.Length < 4)
        {
            return null;
        }

        var matches = (await _memory.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(item => item.Id.ToString("N").StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    private static string MemoryKindLabel(PersonalMemoryKind kind) => kind switch
    {
        PersonalMemoryKind.Explicit => "Explícito",
        PersonalMemoryKind.TemporaryObservation => "Temporal",
        PersonalMemoryKind.Suggestion => "Sugerencia",
        _ => "Dato"
    };

    private static string FormatResult(ToolExecutionResult result)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Array } data || data.GetArrayLength() == 0)
        {
            return result.Message;
        }

        var items = data.EnumerateArray()
            .Take(3)
            .Select(item =>
            {
                var title = ReadString(item, "title") ?? "Sin título";
                var when = ReadString(item, "dueAt") ?? ReadString(item, "startsAt");
                return when is null ? title : $"{title} · {FormatDate(when)}";
            })
            .ToArray();
        var suffix = data.GetArrayLength() > items.Length ? " · …" : string.Empty;
        return $"{result.Message} {string.Join(" · ", items)}{suffix}";
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        // Web defaults use camelCase, but tolerate persisted PascalCase during migrations.
        var pascalName = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        return item.TryGetProperty(pascalName, out value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string FormatDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("dd/MM HH:mm", ArgentineSpanish)
            : value;

    private static bool TryParseLocalDate(string value, out DateTimeOffset result)
    {
        if (!DateTime.TryParse(
                value,
                ArgentineSpanish,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var local))
        {
            result = default;
            return false;
        }

        result = new DateTimeOffset(local);
        return true;
    }
}
