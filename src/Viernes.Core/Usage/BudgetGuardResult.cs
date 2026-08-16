namespace Viernes.Core.Usage;

public sealed record BudgetGuardResult(
    BudgetGuardDecision Decision,
    bool WasExplicitlyOverridden,
    IReadOnlyList<string> Reasons,
    UsageLedgerTotals DailyTotals,
    UsageLedgerTotals MonthlyTotals,
    UsageLedgerTotals RoleDailyTotals,
    UsageLedgerTotals RoleMonthlyTotals)
{
    public bool CanProceed => Decision == BudgetGuardDecision.Allow;
}
