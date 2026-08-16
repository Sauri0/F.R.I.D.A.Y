namespace Viernes.Core.Models;

/// <summary>
/// USD cost metadata. Exact cost is provider-reported; estimated cost comes only from the
/// caller-configured rate card. Neither value contains prompt or response content.
/// </summary>
public readonly record struct UsageCost(
    decimal? ExactUsd = null,
    decimal? EstimatedUsd = null,
    decimal? EffectiveTotalUsd = null)
{
    public decimal? EffectiveUsd => EffectiveTotalUsd ?? ExactUsd ?? EstimatedUsd;

    public bool HasValue => ExactUsd is not null || EstimatedUsd is not null || EffectiveTotalUsd is not null;

    public static UsageCost operator +(UsageCost left, UsageCost right)
    {
        var leftHasValue = left.HasValue;
        var rightHasValue = right.HasValue;
        decimal? effective = leftHasValue || rightHasValue
            ? (left.EffectiveUsd ?? 0) + (right.EffectiveUsd ?? 0)
            : null;
        return new UsageCost(
            AddOnlyWhenComplete(left.ExactUsd, right.ExactUsd, leftHasValue, rightHasValue),
            AddOnlyWhenComplete(left.EstimatedUsd, right.EstimatedUsd, leftHasValue, rightHasValue),
            effective);
    }

    private static decimal? AddOnlyWhenComplete(
        decimal? left,
        decimal? right,
        bool leftCostExists,
        bool rightCostExists)
    {
        if (!leftCostExists)
        {
            return right;
        }

        if (!rightCostExists)
        {
            return left;
        }

        return left is not null && right is not null ? left + right : null;
    }
}
