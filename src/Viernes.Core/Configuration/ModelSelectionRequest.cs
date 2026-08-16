namespace Viernes.Core.Configuration;

/// <summary>
/// Explicit model-lane request. Approval flags must come from a user-visible decision; the core
/// never infers them from prompt content.
/// </summary>
public sealed record ModelSelectionRequest(
    ModelRole Role,
    bool PremiumApproved = false,
    bool AllowRemoteForLocalPreferredRole = false);
