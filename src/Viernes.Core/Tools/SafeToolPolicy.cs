using System.Text.Json;

namespace Viernes.Core.Tools;

/// <summary>
/// Allows safe tools, gates low-risk side effects behind consent, and permanently prevents
/// sensitive/destructive execution. The latter deliberately remains a confirmation request even
/// when a caller supplies a confirmation flag: a future audited platform adapter must implement
/// those operations, rather than this MVP silently acquiring that power.
/// </summary>
public sealed class SafeToolPolicy : IToolPolicy
{
    public ToolPolicyDecision Evaluate(
        IAssistantTool tool,
        JsonElement arguments,
        ToolExecutionContext context) => tool.AssessRisk(arguments) switch
        {
            ToolRiskLevel.Safe => ToolPolicyDecision.Allow,
            ToolRiskLevel.RequiresConfirmation when context.ConfirmationGranted => ToolPolicyDecision.Allow,
            ToolRiskLevel.RequiresConfirmation => ToolPolicyDecision.RequireConfirmation,
            ToolRiskLevel.Sensitive => ToolPolicyDecision.RequireConfirmation,
            ToolRiskLevel.Destructive => ToolPolicyDecision.RequireConfirmation,
            _ => ToolPolicyDecision.Deny
        };
}
