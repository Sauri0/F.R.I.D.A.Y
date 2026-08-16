using System.Text.Json;

namespace Viernes.Core.Tools;

/// <summary>Safe, serializable outcome returned to the model and UI.</summary>
public sealed record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    ToolExecutionStatus Status,
    string Message,
    JsonElement? Data = null)
{
    public static ToolExecutionResult Success<T>(string callId, string toolName, string message, T data) =>
        new(callId, toolName, ToolExecutionStatus.Succeeded, message, JsonSerializer.SerializeToElement(data));

    public static ToolExecutionResult Success(string callId, string toolName, string message) =>
        new(callId, toolName, ToolExecutionStatus.Succeeded, message);

    public static ToolExecutionResult Confirmation(string callId, string toolName, string message) =>
        new(callId, toolName, ToolExecutionStatus.NeedsConfirmation, message);

    public static ToolExecutionResult Denied(string callId, string toolName, string message) =>
        new(callId, toolName, ToolExecutionStatus.Denied, message);

    public static ToolExecutionResult Failure(string callId, string toolName, string message) =>
        new(callId, toolName, ToolExecutionStatus.Failed, message);

    public string ToModelMessage() => JsonSerializer.Serialize(new
    {
        status = Status.ToString(),
        message = Message,
        data = Data
    });
}
