using System.Text.Json;
using Viernes.Memory.Models;
using Viernes.Memory.Privacy;
using Xunit;

namespace Viernes.Memory.Tests;

public sealed class PersonalMemoryStoreTests
{
    private static readonly DateTimeOffset StartTime =
        new(2032, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NewStore_IsEmptyPausedAndExplicitAboutNoTraining()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));

        var review = await store.ReviewAsync();

        Assert.True(review.IsObservationPaused);
        Assert.Equal(0, review.TotalCount);
        Assert.False(MemoryPrivacy.IsUsedForModelTraining);
        Assert.False(MemoryPrivacy.StoresConversations);
        Assert.False(MemoryPrivacy.StoresCredentials);
        Assert.Contains("no se usa para entrenar modelos", review.PrivacyNotice, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(scope.FilePath));
    }

    [Fact]
    public async Task ExplicitMemory_NormalizesPersistsAndReloadsAsExplicit()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var firstStore = scope.CreateStore(timeProvider: clock);

        var added = await firstStore.AddExplicitAsync("  Prefiere café sin azúcar.  ");
        var reloaded = await scope.CreateStore(timeProvider: clock).ReviewAsync();

        Assert.Equal("Prefiere café sin azúcar.", added.Content);
        Assert.Equal(StartTime, added.CreatedAt);
        Assert.Equal(added, Assert.Single(reloaded.Explicit));
        Assert.Empty(reloaded.TemporaryObservations);
        Assert.Empty(reloaded.Suggestions);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(scope.FilePath));
        Assert.True(json.RootElement.GetProperty("observationPaused").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("explicit").GetArrayLength());
    }

    [Fact]
    public async Task Observe_WhenPaused_DoesNotInspectOrPersistInput()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));

        var result = await store.ObserveAsync("Usuario: contenido que no debe guardarse", 0.99);

        Assert.Equal(ObservationCaptureStatus.Paused, result.Status);
        Assert.False(result.WasStored);
        Assert.False(File.Exists(scope.FilePath));
    }

    [Fact]
    public async Task ResumeThenObserve_CapturesTemporaryFactWithConfidenceAndExpiry()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var store = scope.CreateStore(timeProvider: clock);
        await store.ResumeObservationAsync();

        var result = await store.ObserveAsync("Trabaja mejor por la mañana.", 0.87, TimeSpan.FromDays(2));
        var observation = Assert.IsType<TemporaryObservation>(result.Observation);
        var review = await store.ReviewAsync();

        Assert.Equal(ObservationCaptureStatus.Captured, result.Status);
        Assert.Equal(0.87, observation.Confidence);
        Assert.Equal(StartTime, observation.ObservedAt);
        Assert.Equal(StartTime.AddDays(2), observation.ExpiresAt);
        Assert.False(review.IsObservationPaused);
        Assert.Equal(observation, Assert.Single(review.TemporaryObservations));
        Assert.Empty(review.Explicit);
    }

    [Fact]
    public async Task Observe_BelowThresholdIsNotRetained()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await store.ResumeObservationAsync();

        var result = await store.ObserveAsync("Quizá prefiere caminar.", 0.40);

        Assert.Equal(ObservationCaptureStatus.BelowConfidenceThreshold, result.Status);
        Assert.Empty((await store.ReviewAsync()).TemporaryObservations);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ObserveAsync("Valor inválido", double.NaN));
    }

    [Fact]
    public async Task RepeatedObservation_RefreshesWithoutDuplicatingAndKeepsBestConfidence()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var store = scope.CreateStore(timeProvider: clock);
        await store.ResumeObservationAsync();
        var first = await store.ObserveAsync("Prefiere reuniones breves.", 0.80, TimeSpan.FromDays(1));
        clock.Advance(TimeSpan.FromHours(4));

        var second = await store.ObserveAsync("prefiere reuniones breves.", 0.70, TimeSpan.FromDays(3));
        var stored = Assert.Single((await store.ReviewAsync()).TemporaryObservations);

        Assert.Equal(ObservationCaptureStatus.Refreshed, second.Status);
        Assert.Equal(first.Observation!.Id, second.Observation!.Id);
        Assert.Equal(0.80, stored.Confidence);
        Assert.Equal(StartTime, stored.ObservedAt);
        Assert.Equal(clock.GetUtcNow().AddDays(3), stored.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredObservationAndDerivedSuggestion_ArePurgedOnReview()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var options = new PersonalMemoryStoreOptions
        {
            MinimumTemporaryLifetime = TimeSpan.FromSeconds(1),
            DefaultObservationLifetime = TimeSpan.FromSeconds(2),
            MaximumObservationLifetime = TimeSpan.FromDays(30)
        };
        var store = scope.CreateStore(options, clock);
        await store.ResumeObservationAsync();
        var captured = await store.ObserveAsync("Usa auriculares al concentrarse.", 0.90);
        await store.SuggestAsync(
            "Conviene ofrecer bloques de foco.",
            captured.Observation!.Id,
            TimeSpan.FromDays(2));

        clock.Advance(TimeSpan.FromSeconds(3));
        var review = await store.ReviewAsync();

        Assert.Empty(review.TemporaryObservations);
        Assert.Empty(review.Suggestions);
        Assert.Empty(review.Explicit);
        var persisted = await File.ReadAllTextAsync(scope.FilePath);
        Assert.DoesNotContain("auriculares", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suggestion_RemainsPendingUntilApprovedThenBecomesExplicit()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var store = scope.CreateStore(timeProvider: clock);
        await store.ResumeObservationAsync();
        var observation = (await store.ObserveAsync("Prefiere respuestas concisas.", 0.95)).Observation!;
        var suggestion = await store.SuggestAsync("Prefiere respuestas concisas.", observation.Id);

        var pending = await store.ReviewAsync();
        Assert.Empty(pending.Explicit);
        Assert.Single(pending.Suggestions);

        clock.Advance(TimeSpan.FromMinutes(1));
        var approved = await store.ApproveSuggestionAsync(suggestion.Id);
        var final = await store.ReviewAsync();

        Assert.Equal(suggestion.Id, approved.Id);
        Assert.Equal(clock.GetUtcNow(), approved.CreatedAt);
        Assert.Equal(approved, Assert.Single(final.Explicit));
        Assert.Empty(final.Suggestions);
        Assert.Empty(final.TemporaryObservations);
    }

    [Fact]
    public async Task RejectSuggestion_DeletesSuggestionAndItsSourceObservation()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await store.ResumeObservationAsync();
        var observation = (await store.ObserveAsync("Puede preferir modo oscuro.", 0.88)).Observation!;
        var suggestion = await store.SuggestAsync("Prefiere modo oscuro.", observation.Id);

        var rejected = await store.RejectSuggestionAsync(suggestion.Id);
        var review = await store.ReviewAsync();

        Assert.True(rejected);
        Assert.Empty(review.Suggestions);
        Assert.Empty(review.TemporaryObservations);
        Assert.False(await store.RejectSuggestionAsync(suggestion.Id));
    }

    [Fact]
    public async Task Edit_PreservesMemoryLevelAndUpdatesContent()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var store = scope.CreateStore(timeProvider: clock);
        var explicitMemory = await store.AddExplicitAsync("Prefiere té.");
        await store.ResumeObservationAsync();
        var observation = (await store.ObserveAsync("Trabaja de noche.", 0.75)).Observation!;
        var suggestion = await store.SuggestAsync("Prefiere alertas suaves.");
        clock.Advance(TimeSpan.FromHours(1));

        var editedExplicit = await store.EditAsync(explicitMemory.Id, "Prefiere té verde.");
        var editedObservation = await store.EditAsync(observation.Id, "Suele trabajar de noche.");
        var editedSuggestion = await store.EditAsync(suggestion.Id, "Prefiere alertas visuales suaves.");

        Assert.IsType<ExplicitMemory>(editedExplicit);
        Assert.IsType<TemporaryObservation>(editedObservation);
        Assert.IsType<MemorySuggestion>(editedSuggestion);
        Assert.All([editedExplicit, editedObservation, editedSuggestion], item =>
            Assert.Equal(clock.GetUtcNow(), item.UpdatedAt));
        Assert.Single(await store.ListAsync(PersonalMemoryKind.Explicit));
        Assert.Equal(3, (await store.ListAsync()).Count);
    }

    [Fact]
    public async Task ForgetObservation_AlsoForgetsSuggestionDerivedFromIt()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await store.ResumeObservationAsync();
        var observation = (await store.ObserveAsync("Usa calendario semanal.", 0.92)).Observation!;
        await store.SuggestAsync("Prefiere un resumen semanal.", observation.Id);

        Assert.True(await store.ForgetAsync(observation.Id));
        Assert.Equal(0, (await store.ReviewAsync()).TotalCount);
        Assert.False(await store.ForgetAsync(observation.Id));
    }

    [Fact]
    public async Task DeleteAll_ClearsEveryLevelAndRestoresPausedDefault()
    {
        using var scope = new TestStoreScope();
        var store = scope.CreateStore(timeProvider: new ManualTimeProvider(StartTime));
        await store.AddExplicitAsync("Vive en Buenos Aires.");
        await store.ResumeObservationAsync();
        await store.ObserveAsync("Prefiere llamadas por la tarde.", 0.80);
        await store.SuggestAsync("Prefiere recordatorios discretos.");

        var deleted = await store.DeleteAllAsync();
        var review = await store.ReviewAsync();

        Assert.Equal(3, deleted.TotalDeleted);
        Assert.Equal(0, review.TotalCount);
        Assert.True(review.IsObservationPaused);
        var persisted = await File.ReadAllTextAsync(scope.FilePath);
        Assert.DoesNotContain("Buenos Aires", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recordatorios", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PauseAndResume_ArePersistedAcrossInstances()
    {
        using var scope = new TestStoreScope();
        var clock = new ManualTimeProvider(StartTime);
        var store = scope.CreateStore(timeProvider: clock);

        await store.ResumeObservationAsync();
        Assert.False((await scope.CreateStore(timeProvider: clock).ReviewAsync()).IsObservationPaused);

        await store.PauseObservationAsync();
        Assert.True((await scope.CreateStore(timeProvider: clock).ReviewAsync()).IsObservationPaused);
    }
}
