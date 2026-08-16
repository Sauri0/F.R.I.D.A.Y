namespace Viernes.Core.Models;

/// <summary>A provider-neutral conversation message.</summary>
public sealed record ConversationMessage(
    ConversationRole Role,
    string? Content,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<ToolCall>? ToolCalls = null)
{
    public static ConversationMessage System(string content) => new(ConversationRole.System, content);

    public static ConversationMessage User(string content) => new(ConversationRole.User, content);

    public static ConversationMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new(ConversationRole.Assistant, content, ToolCalls: toolCalls);

    public static ConversationMessage Tool(string callId, string name, string content) =>
        new(ConversationRole.Tool, content, callId, name);
}
