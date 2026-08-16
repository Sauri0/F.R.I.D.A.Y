using Viernes.Core.Configuration;

namespace Viernes.Core.Usage;

/// <summary>Optional per-role guardrails. Null means the corresponding limit is not configured.</summary>
public sealed record RoleBudgetLimits
{
    public RoleBudgetLimits(
        ModelRole role,
        decimal? dailyBudgetUsd = null,
        decimal? monthlyBudgetUsd = null,
        int? maxRequestsPerDay = null)
    {
        Role = NormalizeRole(role);
        DailyBudgetUsd = ValidateBudget(dailyBudgetUsd, nameof(dailyBudgetUsd));
        MonthlyBudgetUsd = ValidateBudget(monthlyBudgetUsd, nameof(monthlyBudgetUsd));
        MaxRequestsPerDay = ValidateRequestLimit(maxRequestsPerDay, nameof(maxRequestsPerDay));
    }

    public ModelRole Role { get; }

    public decimal? DailyBudgetUsd { get; }

    public decimal? MonthlyBudgetUsd { get; }

    public int? MaxRequestsPerDay { get; }

    internal static ModelRole NormalizeRole(ModelRole role) =>
        role == ModelRole.Planning ? ModelRole.Agent : role;

    internal static decimal? ValidateBudget(decimal? value, string parameterName)
    {
        if (value is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A budget must be between 0 and 100000 USD.");
        }

        return value;
    }

    internal static int? ValidateRequestLimit(int? value, string parameterName)
    {
        if (value is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A request limit must be between 0 and 1000000.");
        }

        return value;
    }
}
