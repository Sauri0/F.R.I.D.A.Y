using System.Collections.ObjectModel;
using Viernes.Core.Configuration;

namespace Viernes.Core.Usage;

/// <summary>Immutable global and per-role spending/request guardrails.</summary>
public sealed class UsageBudgetConfiguration
{
    private readonly ReadOnlyDictionary<ModelRole, RoleBudgetLimits> _roleLimits;

    public UsageBudgetConfiguration(
        decimal? dailyBudgetUsd = null,
        decimal? monthlyBudgetUsd = null,
        int? maxRequestsPerDay = null,
        int maxDeepTasksPerDay = 3,
        IEnumerable<RoleBudgetLimits>? roleLimits = null)
    {
        DailyBudgetUsd = RoleBudgetLimits.ValidateBudget(dailyBudgetUsd, nameof(dailyBudgetUsd));
        MonthlyBudgetUsd = RoleBudgetLimits.ValidateBudget(monthlyBudgetUsd, nameof(monthlyBudgetUsd));
        MaxRequestsPerDay = RoleBudgetLimits.ValidateRequestLimit(maxRequestsPerDay, nameof(maxRequestsPerDay));
        MaxDeepTasksPerDay = maxDeepTasksPerDay is >= 0 and <= 100
            ? maxDeepTasksPerDay
            : throw new ArgumentOutOfRangeException(
                nameof(maxDeepTasksPerDay),
                "Use between 0 and 100 deep tasks per day.");

        var limits = new Dictionary<ModelRole, RoleBudgetLimits>();
        foreach (var item in roleLimits ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!limits.TryAdd(item.Role, item))
            {
                throw new ArgumentException($"Duplicate limits for role '{item.Role}'.", nameof(roleLimits));
            }
        }

        _roleLimits = new ReadOnlyDictionary<ModelRole, RoleBudgetLimits>(limits);
    }

    public decimal? DailyBudgetUsd { get; }

    public decimal? MonthlyBudgetUsd { get; }

    public int? MaxRequestsPerDay { get; }

    public int MaxDeepTasksPerDay { get; }

    public IReadOnlyDictionary<ModelRole, RoleBudgetLimits> RoleLimits => _roleLimits;

    public bool HasAnyFinancialLimit => DailyBudgetUsd is not null ||
                                        MonthlyBudgetUsd is not null ||
                                        _roleLimits.Values.Any(item =>
                                            item.DailyBudgetUsd is not null || item.MonthlyBudgetUsd is not null);

    public RoleBudgetLimits GetRoleLimits(ModelRole role)
    {
        role = RoleBudgetLimits.NormalizeRole(role);
        return _roleLimits.TryGetValue(role, out var limits)
            ? limits
            : new RoleBudgetLimits(role);
    }
}
