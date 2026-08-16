using System.Text.Json;

namespace Viernes.Core.Tools;

public interface IToolPolicy
{
    ToolPolicyDecision Evaluate(
        IAssistantTool tool,
        JsonElement arguments,
        ToolExecutionContext context);
}
