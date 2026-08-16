using System.Text.Json;

namespace Viernes.Core.Tools;

/// <summary>Provider-neutral function definition.</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    ToolRiskLevel RiskLevel = ToolRiskLevel.Safe)
{
    public static ToolDefinition Create<TSchema>(
        string name,
        string description,
        TSchema parameters,
        ToolRiskLevel riskLevel = ToolRiskLevel.Safe) =>
        new(name, description, JsonSerializer.SerializeToElement(parameters), riskLevel);
}
