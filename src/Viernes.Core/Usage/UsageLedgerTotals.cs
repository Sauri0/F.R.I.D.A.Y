using Viernes.Core.Models;

namespace Viernes.Core.Usage;

public sealed record UsageLedgerTotals(
    int RequestCount,
    int DeepTaskCount,
    TokenUsage Tokens,
    decimal ExactCostUsd,
    decimal EstimatedCostUsd,
    decimal EffectiveCostUsd)
{
    public static UsageLedgerTotals Empty { get; } =
        new(0, 0, TokenUsage.Zero, 0, 0, 0);
}
