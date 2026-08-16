using System.Text.Json;

namespace Viernes.Core.Tools;

public interface IAssistantTool
{
    ToolDefinition Definition { get; }

    ToolRiskLevel AssessRisk(JsonElement arguments) => Definition.RiskLevel;

    Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
