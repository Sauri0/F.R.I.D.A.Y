using System.Net;

namespace Viernes.Core.OpenRouter;

/// <summary>Sanitized provider failure; it never carries request bodies or credentials.</summary>
public sealed class OpenRouterException : Exception
{
    public OpenRouterException(
        string message,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? attemptedModels = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        AttemptedModels = attemptedModels ?? Array.Empty<string>();
    }

    public HttpStatusCode? StatusCode { get; }

    public IReadOnlyList<string> AttemptedModels { get; }
}
