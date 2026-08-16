using System.Text.Json;
using Viernes.Memory.Privacy;
using Xunit;

namespace Viernes.Memory.Tests;

public sealed class PrivacyAndRobustnessTests
{
    private static readonly DateTimeOffset StartTime =
        new(2032, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("usuario: copiá toda esta charla", MemoryContentRejectionReason.ConversationLike)]
    [InlineData("{\"role\":\"user\",\"content\":\"texto de prueba\"}", MemoryContentRejectionReason.ConversationLike)]
    [InlineData("password: NO_ES_UN_SECRETO", MemoryContentRejectionReason.CredentialLike)]
    [InlineData("token=VALOR_DE_PRUEBA", MemoryContentRejectionReason.CredentialLike)]
    public async Task AddExplicit_RejectsConversationOrCredentialLikeContentWithoutPersisting(
        string content,
        MemoryContentRejectionReason expectedReason)
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));

        var exception = await Assert.ThrowsAsync<MemoryContentRejectedException>(() =>
            store.AddExplicitAsync(content));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.False(File.Exists(scope.FilePath));
    }

    [Fact]
    public async Task PersistedJson_HasOnlyMemorySchemaAndNoConversationOrSecretFields()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await store.AddExplicitAsync("Prefiere empezar el día con una lista breve.");
        await store.ResumeObservationAsync();
        await store.ObserveAsync("Suele revisar su agenda por la mañana.", 0.91);
        await store.SuggestAsync("Prefiere recordatorios agrupados.");

        var json = await File.ReadAllTextAsync(scope.FilePath);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["explicit", "observationPaused", "schemaVersion", "suggestions", "temporaryObservations"],
            propertyNames);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("messages", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conversation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("training", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LoadedJson_WithCredentialLikeEntry_IsRejected()
    {
        using var scope = new TestStoreScope();
        Directory.CreateDirectory(scope.DirectoryPath);
        var id = Guid.NewGuid();
        var timestamp = StartTime.ToString("O");
        var imported = $$"""
            {
              "schemaVersion": 1,
              "observationPaused": true,
              "explicit": [
                {
                  "id": "{{id}}",
                  "content": "clave secreta: VALOR_DE_PRUEBA",
                  "createdAt": "{{timestamp}}",
                  "updatedAt": "{{timestamp}}"
                }
              ],
              "temporaryObservations": [],
              "suggestions": []
            }
            """;
        await File.WriteAllTextAsync(scope.FilePath, imported);
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.ReviewAsync());

        Assert.IsType<MemoryContentRejectedException>(exception.InnerException);
    }

    [Fact]
    public async Task ConcurrentWritersAcrossStoreInstances_DoNotLoseUpdatesOrCorruptJson()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var first = scope.CreateStore(timeProvider: clock);
        var second = scope.CreateStore(timeProvider: clock);

        var writes = Enumerable.Range(0, 60)
            .Select(index => (index % 2 == 0 ? first : second)
                .AddExplicitAsync($"Hecho concurrente {index:D2}."));
        await Task.WhenAll(writes);

        var review = await first.ReviewAsync();
        Assert.Equal(60, review.Explicit.Count);
        Assert.Equal(60, review.Explicit.Select(item => item.Id).Distinct().Count());
        Assert.Equal(60, review.Explicit.Select(item => item.Content).Distinct().Count());
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(scope.FilePath));
        Assert.Equal(60, parsed.RootElement.GetProperty("explicit").GetArrayLength());
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CapacityFailure_LeavesLastValidAtomicSnapshotUntouched()
    {
        using var scope = new TestStoreScope();
        var options = new PersonalMemoryStoreOptions
        {
            MaximumExplicitItems = 2,
            MaximumTotalItems = 3
        };
        var store = scope.CreateStore(options, new ManualTimeProvider(StartTime));
        await store.AddExplicitAsync("Primer hecho.");
        await store.AddExplicitAsync("Segundo hecho.");
        var beforeFailure = await File.ReadAllTextAsync(scope.FilePath);

        await Assert.ThrowsAsync<MemoryCapacityExceededException>(() =>
            store.AddExplicitAsync("Tercer hecho."));

        Assert.Equal(beforeFailure, await File.ReadAllTextAsync(scope.FilePath));
        Assert.Equal(2, (await store.ReviewAsync()).Explicit.Count);
    }

    [Fact]
    public async Task TemporaryRetention_EnforcesConfiguredMinimumAndMaximum()
    {
        using var scope = new TestStoreScope();
        var options = new PersonalMemoryStoreOptions
        {
            MinimumTemporaryLifetime = TimeSpan.FromMinutes(5),
            DefaultObservationLifetime = TimeSpan.FromHours(1),
            MaximumObservationLifetime = TimeSpan.FromHours(2),
            DefaultSuggestionLifetime = TimeSpan.FromHours(1),
            MaximumSuggestionLifetime = TimeSpan.FromHours(3)
        };
        var store = scope.CreateStore(options, new ManualTimeProvider(StartTime));
        await store.ResumeObservationAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ObserveAsync("Retención demasiado breve.", 0.90, TimeSpan.FromMinutes(4)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ObserveAsync("Retención demasiado larga.", 0.90, TimeSpan.FromHours(3)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SuggestAsync("Sugerencia demasiado larga.", lifetime: TimeSpan.FromHours(4)));

        Assert.Equal(0, (await store.ReviewAsync()).TotalCount);
    }

    [Fact]
    public async Task ExpiredStandaloneSuggestion_IsPurgedFromDisk()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var options = new PersonalMemoryStoreOptions
        {
            MinimumTemporaryLifetime = TimeSpan.FromSeconds(1),
            DefaultSuggestionLifetime = TimeSpan.FromSeconds(2),
            MaximumSuggestionLifetime = TimeSpan.FromDays(30)
        };
        var store = scope.CreateStore(options, clock);
        await store.SuggestAsync("Podría preferir un resumen al final del día.");
        clock.Advance(TimeSpan.FromSeconds(3));

        var review = await store.ReviewAsync();

        Assert.Empty(review.Suggestions);
        Assert.DoesNotContain(
            "resumen al final",
            await File.ReadAllTextAsync(scope.FilePath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreCanceledOperation_DoesNotCreateAFile()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.AddExplicitAsync("No debe persistirse.", cancellation.Token));

        Assert.False(File.Exists(scope.FilePath));
    }

    [Fact]
    public async Task OversizedOrMalformedFiles_AreRejectedInsteadOfPartiallyLoaded()
    {
        using var oversizedScope = new TestStoreScope();
        Directory.CreateDirectory(oversizedScope.DirectoryPath);
        await File.WriteAllTextAsync(oversizedScope.FilePath, new string('x', 2048));
        var strictOptions = new PersonalMemoryStoreOptions { MaximumFileSizeBytes = 1024 };
        var oversizedStore = oversizedScope.CreateStore(strictOptions, new ManualTimeProvider(StartTime));
        await Assert.ThrowsAsync<InvalidDataException>(() => oversizedStore.ReviewAsync());

        using var malformedScope = new TestStoreScope();
        Directory.CreateDirectory(malformedScope.DirectoryPath);
        await File.WriteAllTextAsync(malformedScope.FilePath, "{ esto no es json }");
        var malformedStore = malformedScope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformedStore.ReviewAsync());
    }
}
