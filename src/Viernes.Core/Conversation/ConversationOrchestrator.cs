using Viernes.Core.Configuration;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Tools;
using System.Text.Json;

namespace Viernes.Core.Conversation;

/// <summary>
/// Provider-neutral assistant facade consumed by the Windows shell. It serializes turns, publishes
/// state changes, executes model tool calls only through the policy gate, and retains no secrets.
/// </summary>
public sealed class ConversationOrchestrator : IConversationOrchestrator
{
    private const int MaximumInputCharacters = 12_000;
    private const int MaximumHistoryMessages = 80;
    private const int MaximumPendingConfirmations = 32;

    private const string DefaultSystemPrompt = """
        Sos Viernes, un asistente personal sereno, preciso y cálido. Ayudá de forma proactiva sin
        invadir y mantené siempre al usuario al mando. Usá herramientas sólo cuando aporten valor.
        Nunca afirmes que una acción se ejecutó si el resultado indica simulación, bloqueo o
        confirmación pendiente. No pidas ni repitas claves, tokens o contraseñas. Las acciones
        sensibles o destructivas no están habilitadas en este MVP.
        """;

    /// <summary>
    /// Instrucción del turno hablado. Se agrega sólo mientras hay conversación por voz: una
    /// respuesta que se lee bien escrita puede ser insoportable dicha en voz alta, y contestar
    /// largo es lo que más rompe la sensación de estar charlando.
    /// </summary>
    private const string SpokenTurnDirective = """
        Este turno se responde EN VOZ. Contestá en una o dos frases, como en una charla.
        Nada de listas, viñetas, encabezados ni markdown: se va a leer en voz alta.
        Si la respuesta completa es larga, decí lo esencial y ofrecé ampliar.
        Usá español rioplatense natural, con vos y sin solemnidad.
        """;

    private readonly IChatCompletionClient _chatClient;
    private readonly IToolExecutor _toolExecutor;
    private readonly int _maxToolIterations;
    private readonly string _systemPrompt;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly List<ConversationMessage> _history = [];
    private readonly Dictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);
    private readonly List<TurnStep> _steps = [];
    private AssistantState _currentState = AssistantState.Idle;

    public ConversationOrchestrator(
        IChatCompletionClient chatClient,
        IToolExecutor toolExecutor,
        ViernesOptions? options = null,
        string? systemPrompt = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _maxToolIterations = options?.MaxToolIterations ?? 3;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt.Trim();
        _history.Add(ConversationMessage.System(_systemPrompt));
    }

    public AssistantState CurrentState
    {
        get
        {
            lock (_stateGate)
            {
                return _currentState;
            }
        }
    }

    public event EventHandler<AssistantStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Progreso del turno en curso. Hace visible que una respuesta convincente no ejecutó nada por
    /// su cuenta: cada herramienta aparece como un paso y muestra si la política la dejó pasar.
    /// </summary>
    public event EventHandler<TurnProgressEventArgs>? ProgressChanged;

    public Task<ConversationTurnResult> ProcessAsync(
        string input,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(input, spoken: false, cancellationToken);

    /// <summary>
    /// <paramref name="spoken"/> marca que la respuesta se va a decir en voz alta, no leer.
    /// La directiva se agrega al turno y no al historial: no debe contaminar los turnos escritos.
    /// </summary>
    public async Task<ConversationTurnResult> ProcessAsync(
        string input,
        bool spoken,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var totalUsage = TokenUsage.Zero;
        var totalCost = new UsageCost();
        string? lastModel = null;
        try
        {
            TransitionTo(AssistantState.Thinking);
            RemoveExpiredPendingCalls();
            BeginSteps(input.Trim());

            var userMessage = ConversationMessage.User(input.Trim());
            _history.Add(userMessage);
            TrimHistory();

            var results = new List<ToolExecutionResult>();
            for (var iteration = 0; iteration < _maxToolIterations; iteration++)
            {
                var turn = spoken
                    ? _history.Append(ConversationMessage.System(SpokenTurnDirective)).ToArray()
                    : _history.ToArray();

                var completion = await _chatClient.CompleteAsync(
                    turn,
                    _toolExecutor.Definitions,
                    cancellationToken).ConfigureAwait(false);
                totalUsage += completion.Usage;
                totalCost += completion.Cost;
                lastModel = completion.Model ?? lastModel;

                if (completion.IsLocalMode)
                {
                    var localText = BuildLocalModeResponse(input);
                    _history.Add(ConversationMessage.Assistant(localText));
                    return Finish(localText, isLocalMode: true, results, lastModel, totalUsage, totalCost);
                }

                _history.Add(ConversationMessage.Assistant(completion.Content, completion.ToolCalls));
                TrimHistory();

                if (completion.ToolCalls.Count == 0)
                {
                    var text = string.IsNullOrWhiteSpace(completion.Content)
                        ? "No pude generar una respuesta útil. Probemos de otra manera."
                        : completion.Content.Trim();
                    return Finish(text, isLocalMode: false, results, lastModel, totalUsage, totalCost);
                }

                foreach (var call in completion.ToolCalls)
                {
                    var stepIndex = PushStep(TurnStepLabels.ForTool(call.Name), TurnStepStatus.Running);

                    ToolExecutionResult toolResult;
                    if (results.Any(previous => string.Equals(
                            previous.ToolCallId,
                            call.Id,
                            StringComparison.Ordinal)))
                    {
                        toolResult = ToolExecutionResult.Denied(
                            call.Id,
                            call.Name,
                            "Se bloqueó una llamada de herramienta duplicada.");
                    }
                    else
                    {
                        toolResult = await _toolExecutor.ExecuteAsync(
                            call,
                            confirmationGranted: false,
                            cancellationToken).ConfigureAwait(false);
                    }

                    UpdateStep(stepIndex, toolResult.Status switch
                    {
                        ToolExecutionStatus.Succeeded => TurnStepStatus.Done,
                        _ => TurnStepStatus.Blocked
                    });
                    results.Add(toolResult);
                    _history.Add(ConversationMessage.Tool(
                        call.Id,
                        call.Name,
                        toolResult.ToModelMessage()));

                    if (toolResult.Status == ToolExecutionStatus.NeedsConfirmation)
                    {
                        RememberPendingCall(call);
                    }
                }

                TrimHistory();
            }

            var finalText = results.Any(result => result.Status == ToolExecutionStatus.NeedsConfirmation)
                ? "La acción quedó pendiente de tu confirmación y no se ejecutó."
                : "Alcancé el límite seguro de pasos con herramientas. No realicé más acciones.";
            _history.Add(ConversationMessage.Assistant(finalText));
            return Finish(finalText, isLocalMode: false, results, lastModel, totalUsage, totalCost);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionTo(AssistantState.Idle);
            throw;
        }
        catch (OpenRouterException)
        {
            const string text = "No pude comunicarme con el servicio de conversación. Tus datos locales siguen intactos.";
            TransitionTo(AssistantState.Error);
            return new ConversationTurnResult(
                text,
                AssistantState.Error,
                false,
                Array.Empty<ToolExecutionResult>(),
                lastModel,
                totalUsage,
                totalCost);
        }
        catch (Exception)
        {
            const string text = "Ocurrió un problema interno. No se ejecutaron acciones adicionales.";
            TransitionTo(AssistantState.Error);
            return new ConversationTurnResult(
                text,
                AssistantState.Error,
                false,
                Array.Empty<ToolExecutionResult>(),
                lastModel,
                totalUsage,
                totalCost);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async Task<ToolExecutionResult> ConfirmToolAsync(
        string toolCallId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            throw new ArgumentException("A tool call id is required.", nameof(toolCallId));
        }

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveExpiredPendingCalls();
            if (!_pendingCalls.TryGetValue(toolCallId, out var pending))
            {
                return ToolExecutionResult.Denied(
                    toolCallId,
                    "unknown",
                    "La confirmación ya no está disponible.");
            }

            var result = await _toolExecutor.ExecuteAsync(
                pending.Call,
                confirmationGranted: true,
                cancellationToken).ConfigureAwait(false);

            if (result.Status != ToolExecutionStatus.NeedsConfirmation)
            {
                _pendingCalls.Remove(toolCallId);
            }

            return result;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async Task<ToolExecutionResult> ExecuteLocalToolAsync(
        string toolName,
        JsonElement arguments,
        bool confirmationGranted = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException("A tool name is required.", nameof(toolName));
        }

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TransitionTo(AssistantState.Thinking);
            var call = new ToolCall($"local-{Guid.NewGuid():N}", toolName.Trim(), arguments.Clone());
            var result = await _toolExecutor.ExecuteAsync(
                call,
                confirmationGranted,
                cancellationToken).ConfigureAwait(false);

            if (result.Status == ToolExecutionStatus.NeedsConfirmation)
            {
                RememberPendingCall(call);
            }

            TransitionTo(result.Status == ToolExecutionStatus.Failed
                ? AssistantState.Error
                : AssistantState.Idle);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionTo(AssistantState.Idle);
            throw;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void SetListening(bool isListening) =>
        TransitionTo(isListening ? AssistantState.Listening : AssistantState.Idle);

    public void ClearHistory()
    {
        _turnGate.Wait();
        try
        {
            _history.Clear();
            _history.Add(ConversationMessage.System(_systemPrompt));
            _pendingCalls.Clear();
            TransitionTo(AssistantState.Idle);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public IReadOnlyList<ConversationMessage> GetHistorySnapshot()
    {
        _turnGate.Wait();
        try
        {
            return _history.ToArray();
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private ConversationTurnResult Finish(
        string text,
        bool isLocalMode,
        IReadOnlyList<ToolExecutionResult> results,
        string? model,
        TokenUsage usage,
        UsageCost cost)
    {
        CompleteSteps();
        TransitionTo(AssistantState.Speaking);
        TransitionTo(AssistantState.Idle);
        return new ConversationTurnResult(
            text,
            AssistantState.Idle,
            isLocalMode,
            results.ToArray(),
            model,
            usage,
            cost);
    }

    private void TransitionTo(AssistantState nextState)
    {
        AssistantState previous;
        lock (_stateGate)
        {
            previous = _currentState;
            if (previous == nextState)
            {
                return;
            }

            _currentState = nextState;
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new AssistantStateChangedEventArgs(previous, nextState);
        foreach (EventHandler<AssistantStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // A presentation-layer event handler must not corrupt the assistant state machine.
            }
        }
    }

    private void BeginSteps(string input)
    {
        lock (_steps)
        {
            _steps.Clear();
            _steps.Add(new TurnStep(BuildUnderstoodLabel(input), TurnStepStatus.Done));
            _steps.Add(new TurnStep("Pensando la respuesta", TurnStepStatus.Running));
        }

        PublishSteps();
    }

    private int PushStep(string label, TurnStepStatus status)
    {
        int index;
        lock (_steps)
        {
            // «Pensando» deja de estar en curso apenas aparece una herramienta concreta.
            for (var i = 0; i < _steps.Count; i++)
            {
                if (_steps[i].Status == TurnStepStatus.Running)
                {
                    _steps[i] = _steps[i] with { Status = TurnStepStatus.Done };
                }
            }

            _steps.Add(new TurnStep(label, status));
            index = _steps.Count - 1;
        }

        PublishSteps();
        return index;
    }

    private void UpdateStep(int index, TurnStepStatus status)
    {
        lock (_steps)
        {
            if (index < 0 || index >= _steps.Count)
            {
                return;
            }

            _steps[index] = _steps[index] with { Status = status };
        }

        PublishSteps();
    }

    private void CompleteSteps()
    {
        lock (_steps)
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                if (_steps[i].Status == TurnStepStatus.Running)
                {
                    _steps[i] = _steps[i] with { Status = TurnStepStatus.Done };
                }
            }
        }

        PublishSteps();
    }

    private void PublishSteps()
    {
        var handlers = ProgressChanged;
        if (handlers is null)
        {
            return;
        }

        TurnStep[] snapshot;
        lock (_steps)
        {
            snapshot = _steps.ToArray();
        }

        var eventArgs = new TurnProgressEventArgs(snapshot);
        foreach (EventHandler<TurnProgressEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // La presentación del progreso no puede romper el turno.
            }
        }
    }

    private static string BuildUnderstoodLabel(string input)
    {
        const int maximum = 44;
        var single = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var trimmed = single.Length <= maximum ? single : single[..maximum].TrimEnd() + "…";
        return $"Entendí: «{trimmed}»";
    }

    private void RememberPendingCall(ToolCall call)
    {
        if (_pendingCalls.Count >= MaximumPendingConfirmations)
        {
            var oldest = _pendingCalls.MinBy(item => item.Value.CreatedAt).Key;
            _pendingCalls.Remove(oldest);
        }

        _pendingCalls[call.Id] = new PendingToolCall(call, DateTimeOffset.UtcNow);
    }

    private void RemoveExpiredPendingCalls()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        foreach (var id in _pendingCalls
                     .Where(item => item.Value.CreatedAt < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _pendingCalls.Remove(id);
        }
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaximumHistoryMessages)
        {
            return;
        }

        // Preserve the system contract and the newest complete context window.
        _history.RemoveRange(1, _history.Count - MaximumHistoryMessages);
    }

    private static void ValidateInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("El mensaje no puede estar vacío.", nameof(input));
        }

        if (input.Length > MaximumInputCharacters)
        {
            throw new ArgumentException($"El mensaje no puede superar {MaximumInputCharacters} caracteres.", nameof(input));
        }
    }

    private static string BuildLocalModeResponse(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();
        if (normalized.Contains("clave", StringComparison.Ordinal) ||
            normalized.Contains("openrouter", StringComparison.Ordinal))
        {
            return "Estoy en modo local. La integración está preparada y se activará cuando configures " +
                   "OPENROUTER_API_KEY en tu entorno; no guardo esa clave en archivos.";
        }

        return "Estoy funcionando en modo local, sin enviar datos a servicios externos. " +
               "La interfaz, los estados y las herramientas seguras están disponibles; " +
               "la conversación avanzada se habilita al configurar OpenRouter.";
    }

    private sealed record PendingToolCall(ToolCall Call, DateTimeOffset CreatedAt);
}
