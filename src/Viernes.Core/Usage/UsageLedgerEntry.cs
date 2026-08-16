using Viernes.Core.Configuration;
using Viernes.Core.Models;

namespace Viernes.Core.Usage;

/// <summary>Content-free accounting entry for one completed provider request.</summary>
public sealed record UsageLedgerEntry(
    string RequestId,
    DateTimeOffset TimestampUtc,
    ModelRole Role,
    string Model,
    TokenUsage Tokens,
    UsageCost Cost,
    bool IsDeepTask);
