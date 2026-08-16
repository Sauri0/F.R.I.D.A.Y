using System.Text.Json;
using System.Text.Json.Serialization;
using Viernes.Core.Configuration;
using Viernes.Core.Models;

namespace Viernes.Core.Usage;

/// <summary>
/// Local content-free usage ledger and explicit budget guard. It records only request id, UTC time,
/// role, model, token counts, cost metadata, and the caller-declared deep-task flag. It never sees or
/// persists prompts, responses, tool arguments, credentials, or conversation ids.
/// </summary>
public sealed class UsageLedger
{
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const int MaximumRetainedEntries = 10_000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(400);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UsageBudgetConfiguration _budgets;
    private readonly UsageRateCard _rateCard;
    private readonly TimeProvider _timeProvider;
    private readonly string? _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<UsageLedgerEntry> _memoryEntries = [];

    public UsageLedger(
        UsageBudgetConfiguration budgets,
        UsageRateCard? rateCard = null,
        string? filePath = null,
        TimeProvider? timeProvider = null)
        : this(
            budgets,
            rateCard,
            Path.GetFullPath(filePath ?? GetDefaultPath()),
            timeProvider,
            persistToDisk: true)
    {
    }

    private UsageLedger(
        UsageBudgetConfiguration budgets,
        UsageRateCard? rateCard,
        string? filePath,
        TimeProvider? timeProvider,
        bool persistToDisk)
    {
        _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        _rateCard = rateCard ?? new UsageRateCard();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _filePath = persistToDisk ? filePath : null;
    }

    public string? FilePath => _filePath;

    public bool IsPersistent => _filePath is not null;

    public static UsageLedger CreateInMemory(
        UsageBudgetConfiguration budgets,
        UsageRateCard? rateCard = null,
        TimeProvider? timeProvider = null) =>
        new(budgets, rateCard, null, timeProvider, persistToDisk: false);

    /// <summary>
    /// Returns a serialized snapshot preflight. It does not reserve funds or send a request; hosts
    /// issuing concurrent requests must serialize preflight plus dispatch if they require a strict
    /// hard cap.
    /// </summary>
    public async Task<BudgetGuardResult> EvaluateAsync(
        BudgetCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EstimatedRequestCostUsd is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Estimated request cost is outside the safe range.");
        }

        var now = _timeProvider.GetUtcNow();
        var dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var role = RoleBudgetLimits.NormalizeRole(request.Role);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var daily = CalculateTotals(entries, dayStart, now.AddTicks(1));
            var monthly = CalculateTotals(entries, monthStart, now.AddTicks(1));
            var roleDaily = CalculateTotals(entries, dayStart, now.AddTicks(1), role);
            var roleMonthly = CalculateTotals(entries, monthStart, now.AddTicks(1), role);
            var reasons = EvaluateLimits(request, daily, monthly, roleDaily, roleMonthly);
            var overridden = reasons.Count > 0 && request.ExplicitBudgetOverride;

            return new BudgetGuardResult(
                reasons.Count == 0 || overridden
                    ? BudgetGuardDecision.Allow
                    : BudgetGuardDecision.RequiresExplicitApproval,
                overridden,
                Array.AsReadOnly(reasons.ToArray()),
                daily,
                monthly,
                roleDaily,
                roleMonthly);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UsageLedgerEntry> RecordCompletionAsync(
        ModelRole role,
        ChatCompletionResult completion,
        bool isDeepTask = false,
        string? requestId = null,
        DateTimeOffset? timestampUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.IsLocalMode)
        {
            throw new ArgumentException("Local-mode completions do not belong in the remote usage ledger.", nameof(completion));
        }

        if (string.IsNullOrWhiteSpace(completion.Model))
        {
            throw new ArgumentException("A provider model id is required for usage accounting.", nameof(completion));
        }

        return await RecordAsync(
            role,
            completion.Model,
            completion.Usage,
            completion.Cost,
            isDeepTask,
            requestId ?? completion.RequestId,
            timestampUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageLedgerEntry> RecordAsync(
        ModelRole role,
        string model,
        TokenUsage tokens,
        UsageCost cost = default,
        bool isDeepTask = false,
        string? requestId = null,
        DateTimeOffset? timestampUtc = null,
        CancellationToken cancellationToken = default)
    {
        ValidateEntry(model, tokens, cost, requestId);
        var normalizedRole = RoleBudgetLimits.NormalizeRole(role);
        var estimate = cost.EstimatedUsd ?? _rateCard.EstimateCostUsd(model, tokens);
        var normalizedCost = new UsageCost(cost.ExactUsd, estimate, cost.EffectiveTotalUsd);
        var entry = new UsageLedgerEntry(
            string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim(),
            (timestampUtc ?? _timeProvider.GetUtcNow()).ToUniversalTime(),
            normalizedRole,
            model.Trim(),
            tokens,
            normalizedCost,
            isDeepTask);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var existing = entries.FirstOrDefault(item =>
                string.Equals(item.RequestId, entry.RequestId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Role == entry.Role &&
                    string.Equals(existing.Model, entry.Model, StringComparison.Ordinal) &&
                    existing.Tokens == entry.Tokens &&
                    existing.Cost == entry.Cost &&
                    existing.IsDeepTask == entry.IsDeepTask)
                {
                    return existing;
                }

                throw new InvalidOperationException("The request id already exists with different usage data.");
            }

            entries.Add(entry);
            Prune(entries, _timeProvider.GetUtcNow());
            await WriteUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UsageLedgerTotals> GetDailyTotalsAsync(
        ModelRole? role = null,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        return await GetTotalsAsync(start, now.AddTicks(1), role, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageLedgerTotals> GetMonthlyTotalsAsync(
        ModelRole? role = null,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return await GetTotalsAsync(start, now.AddTicks(1), role, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UsageLedgerEntry>> GetEntriesSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Array.AsReadOnly((await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UsageLedgerTotals> GetTotalsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        ModelRole? role,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return CalculateTotals(
                entries,
                start,
                end,
                role is null ? null : RoleBudgetLimits.NormalizeRole(role.Value));
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> EvaluateLimits(
        BudgetCheckRequest request,
        UsageLedgerTotals daily,
        UsageLedgerTotals monthly,
        UsageLedgerTotals roleDaily,
        UsageLedgerTotals roleMonthly)
    {
        var reasons = new List<string>();
        var projectedCost = request.EstimatedRequestCostUsd ?? 0;
        AddFinancialReason(reasons, daily.EffectiveCostUsd, projectedCost, _budgets.DailyBudgetUsd, "presupuesto diario global");
        AddFinancialReason(reasons, monthly.EffectiveCostUsd, projectedCost, _budgets.MonthlyBudgetUsd, "presupuesto mensual global");
        AddCountReason(reasons, daily.RequestCount, _budgets.MaxRequestsPerDay, "límite diario global de solicitudes");

        var roleLimits = _budgets.GetRoleLimits(request.Role);
        AddFinancialReason(reasons, roleDaily.EffectiveCostUsd, projectedCost, roleLimits.DailyBudgetUsd, "presupuesto diario del rol");
        AddFinancialReason(reasons, roleMonthly.EffectiveCostUsd, projectedCost, roleLimits.MonthlyBudgetUsd, "presupuesto mensual del rol");
        AddCountReason(reasons, roleDaily.RequestCount, roleLimits.MaxRequestsPerDay, "límite diario de solicitudes del rol");

        if (request.IsDeepTask && daily.DeepTaskCount + 1 > _budgets.MaxDeepTasksPerDay)
        {
            reasons.Add("Se alcanzó el límite diario de tareas profundas.");
        }

        return reasons;
    }

    private static void AddFinancialReason(
        ICollection<string> reasons,
        decimal current,
        decimal projected,
        decimal? limit,
        string label)
    {
        if (limit is not null && (limit == 0 || current >= limit || current + projected > limit))
        {
            reasons.Add($"La solicitud alcanzaría o superaría el {label}.");
        }
    }

    private static void AddCountReason(
        ICollection<string> reasons,
        int current,
        int? limit,
        string label)
    {
        if (limit is not null && current + 1 > limit)
        {
            reasons.Add($"La solicitud superaría el {label}.");
        }
    }

    private static UsageLedgerTotals CalculateTotals(
        IEnumerable<UsageLedgerEntry> entries,
        DateTimeOffset start,
        DateTimeOffset end,
        ModelRole? role = null)
    {
        var requestCount = 0;
        var deepTaskCount = 0;
        var tokens = TokenUsage.Zero;
        decimal exact = 0;
        decimal estimated = 0;
        decimal effective = 0;

        foreach (var entry in entries)
        {
            if (entry.TimestampUtc < start || entry.TimestampUtc >= end ||
                (role is not null && entry.Role != role))
            {
                continue;
            }

            requestCount++;
            if (entry.IsDeepTask)
            {
                deepTaskCount++;
            }

            tokens += entry.Tokens;
            exact += entry.Cost.ExactUsd ?? 0;
            estimated += entry.Cost.EstimatedUsd ?? 0;
            effective += entry.Cost.EffectiveUsd ?? 0;
        }

        return new UsageLedgerTotals(requestCount, deepTaskCount, tokens, exact, estimated, effective);
    }

    private async Task<List<UsageLedgerEntry>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            return [.. _memoryEntries];
        }

        if (!File.Exists(_filePath))
        {
            return [];
        }

        var info = new FileInfo(_filePath);
        if (info.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The usage ledger is unexpectedly large.");
        }

        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var entries = await JsonSerializer.DeserializeAsync<List<UsageLedgerEntry>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false) ?? [];
        ValidateLoadedEntries(entries);
        return entries;
    }

    private async Task WriteUnsafeAsync(
        List<UsageLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        if (_filePath is null)
        {
            _memoryEntries.Clear();
            _memoryEntries.AddRange(entries);
            return;
        }

        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The ledger needs a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    entries,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Prune(List<UsageLedgerEntry> entries, DateTimeOffset now)
    {
        var cutoff = now - Retention;
        entries.RemoveAll(entry => entry.TimestampUtc < cutoff);
        if (entries.Count > MaximumRetainedEntries)
        {
            entries.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
            entries.RemoveRange(0, entries.Count - MaximumRetainedEntries);
        }
    }

    private static void ValidateEntry(
        string model,
        TokenUsage tokens,
        UsageCost cost,
        string? requestId)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Trim().Length > 200)
        {
            throw new ArgumentException("A valid model id is required.", nameof(model));
        }

        if (tokens.PromptTokens < 0 || tokens.CompletionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens));
        }

        if (cost.ExactUsd is < 0 or > 1_000_000 ||
            cost.EstimatedUsd is < 0 or > 1_000_000 ||
            cost.EffectiveTotalUsd is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(cost));
        }

        if (requestId?.Trim().Length > 128)
        {
            throw new ArgumentException("The request id is too long.", nameof(requestId));
        }
    }

    private static void ValidateLoadedEntries(IReadOnlyList<UsageLedgerEntry> entries)
    {
        if (entries.Count > MaximumRetainedEntries)
        {
            throw new InvalidDataException("The usage ledger contains too many entries.");
        }

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.RequestId) ||
                !Enum.IsDefined(entry.Role) ||
                !requestIds.Add(entry.RequestId))
            {
                throw new InvalidDataException("The usage ledger contains an invalid entry.");
            }

            try
            {
                ValidateEntry(entry.Model, entry.Tokens, entry.Cost, entry.RequestId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The usage ledger contains invalid accounting data.", exception);
            }
        }
    }

    private static string GetDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Local application data is unavailable.");
        }

        return Path.Combine(root, "Viernes", "usage-ledger.json");
    }
}
