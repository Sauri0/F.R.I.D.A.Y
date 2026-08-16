using System.Collections.Concurrent;
using System.Text.Json;
using Viernes.Memory.Models;
using Viernes.Memory.Privacy;

namespace Viernes.Memory.Persistence;

/// <summary>
/// Persistencia JSON local y atómica. No contiene campos para credenciales, conversaciones
/// ni transcripciones; las observaciones automáticas empiezan pausadas.
/// </summary>
public sealed class JsonPersonalMemoryStore : IPersonalMemoryStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate;
    private readonly PersonalMemoryStoreOptions _options;
    private readonly TimeProvider _timeProvider;

    public JsonPersonalMemoryStore(
        string? filePath = null,
        PersonalMemoryStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new PersonalMemoryStoreOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;

        FilePath = Path.GetFullPath(filePath ?? GetDefaultFilePath());
        if (string.IsNullOrWhiteSpace(Path.GetFileName(FilePath)))
        {
            throw new ArgumentException("La memoria requiere una ruta de archivo JSON.", nameof(filePath));
        }

        _gate = FileGates.GetOrAdd(FilePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string FilePath { get; }

    public async Task<MemoryReview> ReviewAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            if (read.Changed)
            {
                await WriteUnsafeAsync(read.Document, cancellationToken).ConfigureAwait(false);
            }

            return BuildReview(read.Document, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PersonalMemoryItem>> ListAsync(
        PersonalMemoryKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var review = await ReviewAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<PersonalMemoryItem> items = kind switch
        {
            PersonalMemoryKind.Explicit => review.Explicit,
            PersonalMemoryKind.TemporaryObservation => review.TemporaryObservations,
            PersonalMemoryKind.Suggestion => review.Suggestions,
            null => review.Explicit
                .Cast<PersonalMemoryItem>()
                .Concat(review.TemporaryObservations)
                .Concat(review.Suggestions),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return items.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task<ExplicitMemory> AddExplicitAsync(
        string fact,
        CancellationToken cancellationToken = default)
    {
        var content = ValidateContent(fact, nameof(fact));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;
            var existing = document.Explicit.FirstOrDefault(item => SameContent(item.Content, content));
            if (existing is not null)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                return existing;
            }

            document.TemporaryObservations.RemoveAll(item => SameContent(item.Content, content));
            document.Suggestions.RemoveAll(item => SameContent(item.Content, content));
            EnsureCapacity(document, PersonalMemoryKind.Explicit);

            var memory = new ExplicitMemory(Guid.NewGuid(), content, now, now);
            document.Explicit.Add(memory);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return memory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ObservationCaptureResult> ObserveAsync(
        string distilledFact,
        double confidence,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "La confianza debe estar entre 0 y 1.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;

            if (document.ObservationPaused)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                return new ObservationCaptureResult(ObservationCaptureStatus.Paused);
            }

            if (confidence < _options.MinimumObservationConfidence)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                return new ObservationCaptureResult(ObservationCaptureStatus.BelowConfidenceThreshold);
            }

            var content = ValidateContent(distilledFact, nameof(distilledFact));
            var effectiveLifetime = ValidateLifetime(
                lifetime ?? _options.DefaultObservationLifetime,
                _options.MaximumObservationLifetime,
                nameof(lifetime));

            if (document.Explicit.Any(item => SameContent(item.Content, content)))
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                return new ObservationCaptureResult(ObservationCaptureStatus.AlreadyExplicit);
            }

            var index = document.TemporaryObservations.FindIndex(item => SameContent(item.Content, content));
            if (index >= 0)
            {
                var existing = document.TemporaryObservations[index];
                var refreshed = existing with
                {
                    Confidence = Math.Max(existing.Confidence, confidence),
                    ExpiresAt = now.Add(effectiveLifetime),
                    UpdatedAt = now
                };
                document.TemporaryObservations[index] = refreshed;
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                return new ObservationCaptureResult(ObservationCaptureStatus.Refreshed, refreshed);
            }

            EnsureCapacity(document, PersonalMemoryKind.TemporaryObservation);
            var observation = new TemporaryObservation(
                Guid.NewGuid(),
                content,
                confidence,
                now,
                now.Add(effectiveLifetime),
                now);
            document.TemporaryObservations.Add(observation);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return new ObservationCaptureResult(ObservationCaptureStatus.Captured, observation);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemorySuggestion> SuggestAsync(
        string distilledFact,
        Guid? basedOnObservationId = null,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var content = ValidateContent(distilledFact, nameof(distilledFact));
        var effectiveLifetime = ValidateLifetime(
            lifetime ?? _options.DefaultSuggestionLifetime,
            _options.MaximumSuggestionLifetime,
            nameof(lifetime));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;
            if (basedOnObservationId is { } observationId &&
                document.TemporaryObservations.All(item => item.Id != observationId))
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                throw new MemoryItemNotFoundException(observationId);
            }

            if (document.Explicit.Any(item => SameContent(item.Content, content)))
            {
                throw new InvalidOperationException("Ese hecho ya forma parte de la memoria explícita.");
            }

            var existing = document.Suggestions.FirstOrDefault(item => SameContent(item.Content, content));
            if (existing is not null)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                return existing;
            }

            EnsureCapacity(document, PersonalMemoryKind.Suggestion);
            var suggestion = new MemorySuggestion(
                Guid.NewGuid(),
                content,
                now,
                now.Add(effectiveLifetime),
                now,
                basedOnObservationId);
            document.Suggestions.Add(suggestion);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return suggestion;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExplicitMemory> ApproveSuggestionAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(suggestionId, nameof(suggestionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;
            var suggestion = document.Suggestions.FirstOrDefault(item => item.Id == suggestionId);
            if (suggestion is null)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                }

                throw new MemoryItemNotFoundException(suggestionId);
            }

            var existing = document.Explicit.FirstOrDefault(item => SameContent(item.Content, suggestion.Content));
            if (existing is not null)
            {
                RemoveSuggestionAndSource(document, suggestion);
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                return existing;
            }

            EnsureCapacity(document, PersonalMemoryKind.Explicit, replacingExistingItem: true);
            var approved = new ExplicitMemory(suggestion.Id, suggestion.Content, now, now);
            RemoveSuggestionAndSource(document, suggestion);
            document.Explicit.Add(approved);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            return approved;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RejectSuggestionAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(suggestionId, nameof(suggestionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var suggestion = read.Document.Suggestions.FirstOrDefault(item => item.Id == suggestionId);
            if (suggestion is null)
            {
                if (read.Changed)
                {
                    await WriteUnsafeAsync(read.Document, cancellationToken).ConfigureAwait(false);
                }

                return false;
            }

            RemoveSuggestionAndSource(read.Document, suggestion);
            await WriteUnsafeAsync(read.Document, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersonalMemoryItem> EditAsync(
        Guid itemId,
        string revisedFact,
        CancellationToken cancellationToken = default)
    {
        ValidateId(itemId, nameof(itemId));
        var content = ValidateContent(revisedFact, nameof(revisedFact));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;
            if (AllItems(document).Any(item => item.Id != itemId && SameContent(item.Content, content)))
            {
                throw new InvalidOperationException("Ya existe otra memoria con ese contenido.");
            }

            var explicitIndex = document.Explicit.FindIndex(item => item.Id == itemId);
            if (explicitIndex >= 0)
            {
                var edited = document.Explicit[explicitIndex] with { Content = content, UpdatedAt = now };
                document.Explicit[explicitIndex] = edited;
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                return edited;
            }

            var observationIndex = document.TemporaryObservations.FindIndex(item => item.Id == itemId);
            if (observationIndex >= 0)
            {
                var edited = document.TemporaryObservations[observationIndex] with
                {
                    Content = content,
                    UpdatedAt = now
                };
                document.TemporaryObservations[observationIndex] = edited;
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                return edited;
            }

            var suggestionIndex = document.Suggestions.FindIndex(item => item.Id == itemId);
            if (suggestionIndex >= 0)
            {
                var edited = document.Suggestions[suggestionIndex] with { Content = content, UpdatedAt = now };
                document.Suggestions[suggestionIndex] = edited;
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                return edited;
            }

            if (read.Changed)
            {
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            }

            throw new MemoryItemNotFoundException(itemId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ForgetAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        ValidateId(itemId, nameof(itemId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var document = read.Document;
            var removed = document.Explicit.RemoveAll(item => item.Id == itemId) > 0;
            var removedObservation = document.TemporaryObservations.RemoveAll(item => item.Id == itemId) > 0;
            removed |= removedObservation;
            removed |= document.Suggestions.RemoveAll(item =>
                item.Id == itemId || (removedObservation && item.BasedOnObservationId == itemId)) > 0;

            if (removed || read.Changed)
            {
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemoryDeletionSummary> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            var summary = new MemoryDeletionSummary(
                read.Document.Explicit.Count,
                read.Document.TemporaryObservations.Count,
                read.Document.Suggestions.Count);

            await WriteUnsafeAsync(new MemoryDocument(), cancellationToken).ConfigureAwait(false);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task PauseObservationAsync(CancellationToken cancellationToken = default) =>
        SetObservationPausedAsync(isPaused: true, cancellationToken);

    public Task ResumeObservationAsync(CancellationToken cancellationToken = default) =>
        SetObservationPausedAsync(isPaused: false, cancellationToken);

    private async Task SetObservationPausedAsync(bool isPaused, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = GetUtcNow();
            var read = await ReadUnsafeAsync(now, cancellationToken).ConfigureAwait(false);
            if (read.Document.ObservationPaused != isPaused)
            {
                read.Document.ObservationPaused = isPaused;
                read = read with { Changed = true };
            }

            if (read.Changed)
            {
                await WriteUnsafeAsync(read.Document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DocumentReadResult> ReadUnsafeAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new DocumentReadResult(new MemoryDocument(), Changed: false);
        }

        var fileInfo = new FileInfo(FilePath);
        if (fileInfo.Length > _options.MaximumFileSizeBytes)
        {
            throw new InvalidDataException("El archivo de memoria supera el límite permitido.");
        }

        MemoryDocument document;
        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<MemoryDocument>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("El archivo de memoria está vacío.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("El archivo de memoria no contiene JSON válido.", exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("La versión del archivo de memoria no es compatible.");
        }

        var changed = PurgeExpired(document, now);
        changed |= ValidateLoadedDocument(document);
        return new DocumentReadResult(document, changed);
    }

    private async Task WriteUnsafeAsync(MemoryDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("El archivo de memoria requiere una carpeta contenedora.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length > _options.MaximumFileSizeBytes)
            {
                throw new InvalidDataException("La memoria resultante supera el límite permitido.");
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private bool ValidateLoadedDocument(MemoryDocument document)
    {
        if (document.Explicit is null || document.TemporaryObservations is null || document.Suggestions is null)
        {
            throw new InvalidDataException("El archivo de memoria tiene una estructura incompleta.");
        }

        if (document.Explicit.Count > _options.MaximumExplicitItems ||
            document.TemporaryObservations.Count > _options.MaximumTemporaryObservations ||
            document.Suggestions.Count > _options.MaximumSuggestions ||
            CountItems(document) > _options.MaximumTotalItems)
        {
            throw new InvalidDataException("El archivo de memoria contiene demasiados elementos.");
        }

        var changed = false;
        var identifiers = new HashSet<Guid>();
        for (var index = 0; index < document.Explicit.Count; index++)
        {
            var item = document.Explicit[index];
            ValidateLoadedIdAndTime(item.Id, item.CreatedAt, item.UpdatedAt, identifiers);
            var normalized = ValidateLoadedContent(item.Content);
            if (!string.Equals(normalized, item.Content, StringComparison.Ordinal))
            {
                document.Explicit[index] = item with { Content = normalized };
                changed = true;
            }
        }

        for (var index = 0; index < document.TemporaryObservations.Count; index++)
        {
            var item = document.TemporaryObservations[index];
            ValidateLoadedIdAndTime(item.Id, item.ObservedAt, item.UpdatedAt, identifiers);
            if (!double.IsFinite(item.Confidence) || item.Confidence is < 0 or > 1 || item.ExpiresAt <= item.ObservedAt)
            {
                throw new InvalidDataException("El archivo contiene una observación temporal inválida.");
            }

            var normalized = ValidateLoadedContent(item.Content);
            if (!string.Equals(normalized, item.Content, StringComparison.Ordinal))
            {
                document.TemporaryObservations[index] = item with { Content = normalized };
                changed = true;
            }
        }

        for (var index = 0; index < document.Suggestions.Count; index++)
        {
            var item = document.Suggestions[index];
            ValidateLoadedIdAndTime(item.Id, item.SuggestedAt, item.UpdatedAt, identifiers);
            if (item.ExpiresAt <= item.SuggestedAt)
            {
                throw new InvalidDataException("El archivo contiene una sugerencia inválida.");
            }

            if (item.BasedOnObservationId is { } sourceId &&
                document.TemporaryObservations.All(observation => observation.Id != sourceId))
            {
                document.Suggestions[index] = item with { BasedOnObservationId = null };
                item = document.Suggestions[index];
                changed = true;
            }

            var normalized = ValidateLoadedContent(item.Content);
            if (!string.Equals(normalized, item.Content, StringComparison.Ordinal))
            {
                document.Suggestions[index] = item with { Content = normalized };
                changed = true;
            }
        }

        return changed;
    }

    private string ValidateLoadedContent(string content)
    {
        try
        {
            return ValidateContent(content, "content");
        }
        catch (MemoryContentRejectedException exception)
        {
            throw new InvalidDataException("El archivo contiene una entrada prohibida por la política de privacidad.", exception);
        }
    }

    private static void ValidateLoadedIdAndTime(
        Guid id,
        DateTimeOffset recordedAt,
        DateTimeOffset updatedAt,
        HashSet<Guid> identifiers)
    {
        if (id == Guid.Empty || !identifiers.Add(id) || recordedAt == default || updatedAt == default ||
            updatedAt < recordedAt)
        {
            throw new InvalidDataException("El archivo contiene identificadores o fechas de memoria inválidos.");
        }
    }

    private static bool PurgeExpired(MemoryDocument document, DateTimeOffset now)
    {
        var removedObservationIds = document.TemporaryObservations
            .Where(item => item.ExpiresAt <= now)
            .Select(item => item.Id)
            .ToHashSet();
        var changed = document.TemporaryObservations.RemoveAll(item => item.ExpiresAt <= now) > 0;
        changed |= document.Suggestions.RemoveAll(item =>
            item.ExpiresAt <= now ||
            (item.BasedOnObservationId is { } sourceId && removedObservationIds.Contains(sourceId))) > 0;
        return changed;
    }

    private void EnsureCapacity(
        MemoryDocument document,
        PersonalMemoryKind kind,
        bool replacingExistingItem = false)
    {
        var kindCount = kind switch
        {
            PersonalMemoryKind.Explicit => document.Explicit.Count,
            PersonalMemoryKind.TemporaryObservation => document.TemporaryObservations.Count,
            PersonalMemoryKind.Suggestion => document.Suggestions.Count,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var kindLimit = kind switch
        {
            PersonalMemoryKind.Explicit => _options.MaximumExplicitItems,
            PersonalMemoryKind.TemporaryObservation => _options.MaximumTemporaryObservations,
            PersonalMemoryKind.Suggestion => _options.MaximumSuggestions,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        if (kindCount >= kindLimit)
        {
            throw new MemoryCapacityExceededException($"Se alcanzó el límite de memorias de tipo {kind}.");
        }

        var resultingTotal = CountItems(document) + (replacingExistingItem ? 0 : 1);
        if (resultingTotal > _options.MaximumTotalItems)
        {
            throw new MemoryCapacityExceededException("Se alcanzó el límite total de memoria personal.");
        }
    }

    private TimeSpan ValidateLifetime(TimeSpan lifetime, TimeSpan maximum, string parameterName)
    {
        if (lifetime < _options.MinimumTemporaryLifetime || lifetime > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"La retención debe estar entre {_options.MinimumTemporaryLifetime} y {maximum}.");
        }

        return lifetime;
    }

    private string ValidateContent(string content, string parameterName) =>
        MemoryContentPolicy.NormalizeAndValidate(content, _options.MaximumContentLength, parameterName);

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static MemoryReview BuildReview(MemoryDocument document, DateTimeOffset now) =>
        new(
            document.Explicit.OrderByDescending(item => item.UpdatedAt).ToArray(),
            document.TemporaryObservations.OrderByDescending(item => item.UpdatedAt).ToArray(),
            document.Suggestions.OrderByDescending(item => item.UpdatedAt).ToArray(),
            document.ObservationPaused,
            now);

    private static IEnumerable<PersonalMemoryItem> AllItems(MemoryDocument document) =>
        document.Explicit
            .Cast<PersonalMemoryItem>()
            .Concat(document.TemporaryObservations)
            .Concat(document.Suggestions);

    private static int CountItems(MemoryDocument document) =>
        document.Explicit.Count + document.TemporaryObservations.Count + document.Suggestions.Count;

    private static bool SameContent(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("El identificador no puede estar vacío.", parameterName);
        }
    }

    private static void RemoveSuggestionAndSource(MemoryDocument document, MemorySuggestion suggestion)
    {
        document.Suggestions.RemoveAll(item => item.Id == suggestion.Id);
        if (suggestion.BasedOnObservationId is { } observationId)
        {
            document.TemporaryObservations.RemoveAll(item => item.Id == observationId);
        }
    }

    private static string GetDefaultFilePath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("LOCALAPPDATA no está disponible.");
        }

        return Path.Combine(localApplicationData, "Viernes", "memory.json");
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MemoryDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public bool ObservationPaused { get; set; } = true;

        public List<ExplicitMemory> Explicit { get; set; } = [];

        public List<TemporaryObservation> TemporaryObservations { get; set; } = [];

        public List<MemorySuggestion> Suggestions { get; set; } = [];
    }

    private sealed record DocumentReadResult(MemoryDocument Document, bool Changed);
}
