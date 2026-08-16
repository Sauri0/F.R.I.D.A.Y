namespace Viernes.Core.Configuration;

/// <summary>Explicit model lanes. The caller, never hidden heuristics, chooses a lane.</summary>
public enum ModelRole
{
    Fast,
    /// <summary>Legacy name retained for compatibility; resolves to the Agent lane.</summary>
    Planning,
    Agent,
    Reasoning,
    Premium,
    Embeddings,
    LocalSummary
}
