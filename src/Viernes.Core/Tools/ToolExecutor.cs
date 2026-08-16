using System.Collections.ObjectModel;
using System.Text.Json;
using Viernes.Core.Models;

namespace Viernes.Core.Tools;

/// <summary>Single enforcement point for all model-requested capabilities.</summary>
public sealed class ToolExecutor : IToolExecutor
{
    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools;
    private readonly IToolPolicy _policy;

    public ToolExecutor(IEnumerable<IAssistantTool> tools, IToolPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _policy = policy ?? new SafeToolPolicy();

        var dictionary = new Dictionary<string, IAssistantTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ValidateName(tool.Definition.Name);
            if (!dictionary.TryAdd(tool.Definition.Name, tool))
            {
                throw new ArgumentException($"Duplicate tool name '{tool.Definition.Name}'.", nameof(tools));
            }
        }

        _tools = new ReadOnlyDictionary<string, IAssistantTool>(dictionary);
        Definitions = Array.AsReadOnly(_tools.Values.Select(tool => tool.Definition).ToArray());
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolCall call,
        bool confirmationGranted = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return ToolExecutionResult.Denied(call.Id, call.Name, "La herramienta solicitada no está disponible.");
        }

        var context = new ToolExecutionContext(call.Id, confirmationGranted);
        ToolPolicyDecision decision;
        ToolRiskLevel risk;
        try
        {
            risk = tool.AssessRisk(call.Arguments);
            decision = _policy.Evaluate(tool, call.Arguments, context);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return ToolExecutionResult.Failure(call.Id, call.Name, "Los argumentos de la herramienta no son válidos.");
        }

        if (decision == ToolPolicyDecision.RequireConfirmation)
        {
            var message = risk is ToolRiskLevel.Sensitive or ToolRiskLevel.Destructive
                ? "La acción es sensible o destructiva y este MVP no puede ejecutarla. Requiere revisión explícita."
                : "Necesito tu confirmación explícita antes de continuar.";
            return ToolExecutionResult.Confirmation(call.Id, call.Name, message);
        }

        if (decision == ToolPolicyDecision.Deny)
        {
            return ToolExecutionResult.Denied(call.Id, call.Name, "La política de seguridad bloqueó la acción.");
        }

        try
        {
            return await tool.ExecuteAsync(call.Arguments, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return ToolExecutionResult.Failure(call.Id, call.Name, "Los argumentos de la herramienta no son válidos.");
        }
        catch (ArgumentException exception)
        {
            return ToolExecutionResult.Failure(call.Id, call.Name, exception.Message);
        }
        catch (Exception)
        {
            // Provider/model messages must not receive local paths, stack traces, or platform details.
            return ToolExecutionResult.Failure(call.Id, call.Name, "La herramienta no pudo completar la operación.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw new ArgumentException("Tool names must contain only letters, digits, underscores, hyphens or dots.", nameof(name));
        }
    }
}
