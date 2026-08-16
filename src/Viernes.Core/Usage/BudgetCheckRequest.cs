using Viernes.Core.Configuration;

namespace Viernes.Core.Usage;

/// <summary>
/// Caller-declared request shape. Deep-task status and override consent are never inferred from a
/// prompt or automatically supplied by the ledger.
/// </summary>
public sealed record BudgetCheckRequest(
    ModelRole Role,
    decimal? EstimatedRequestCostUsd = null,
    bool IsDeepTask = false,
    bool ExplicitBudgetOverride = false);
