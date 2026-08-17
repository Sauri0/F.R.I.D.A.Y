using System.Text.Json;

namespace Viernes.Core.Tools;

/// <summary>Safe, serializable outcome returned to the model and UI.</summary>
/// <remarks>
/// <c>ImageDataUrl</c> lleva la imagen que la herramienta produjo. Existe porque un resultado de
/// herramienta viaja al modelo como texto, y hay cosas —lo que hay en pantalla— que no se pueden
/// contar en una cadena: se muestran. Cuando viene con imagen, el orquestador la adjunta al turno.
/// Nunca se registra ni se persiste: es un fotograma, y se descarta apenas se usa.
/// </remarks>
public sealed record ToolExecutionResult(
    string ToolCallId,
    string ToolName,
    ToolExecutionStatus Status,
    string Message,
    JsonElement? Data = null,
    string? ImageDataUrl = null)
{
    public static ToolExecutionResult SuccessWithImage(
        string callId,
        string toolName,
        string message,
        string imageDataUrl) =>
        new(callId, toolName, ToolExecutionStatus.Succeeded, message, null, imageDataUrl);

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
