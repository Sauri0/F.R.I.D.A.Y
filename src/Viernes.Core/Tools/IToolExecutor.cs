using Viernes.Core.Models;

namespace Viernes.Core.Tools;

public interface IToolExecutor
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        ToolCall call,
        bool confirmationGranted = false,
        CancellationToken cancellationToken = default);
}
