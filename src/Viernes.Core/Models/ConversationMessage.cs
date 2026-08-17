namespace Viernes.Core.Models;

/// <summary>A provider-neutral conversation message.</summary>
/// <remarks>
/// <c>ImageDataUrl</c> lleva una imagen adjunta como <c>data:</c> URL. Es lo que le permite al
/// modelo mirar la pantalla en vez de que se la describan con palabras.
/// </remarks>
public sealed record ConversationMessage(
    ConversationRole Role,
    string? Content,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ImageDataUrl = null)
{
    public static ConversationMessage System(string content) => new(ConversationRole.System, content);

    public static ConversationMessage User(string content) => new(ConversationRole.User, content);

    public static ConversationMessage UserWithImage(string content, string imageDataUrl) =>
        new(ConversationRole.User, content, ImageDataUrl: imageDataUrl);

    public static ConversationMessage Assistant(string? content, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new(ConversationRole.Assistant, content, ToolCalls: toolCalls);

    public static ConversationMessage Tool(string callId, string name, string content) =>
        new(ConversationRole.Tool, content, callId, name);
}
