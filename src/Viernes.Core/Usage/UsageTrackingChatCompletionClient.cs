using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.Tools;

namespace Viernes.Core.Usage;

/// <summary>
/// Opt-in decorator that records one content-free ledger entry per successful remote completion.
/// It does not evaluate budgets; callers that need a hard preflight must invoke
/// <see cref="UsageLedger.EvaluateAsync"/> before calling this client.
/// </summary>
public sealed class UsageTrackingChatCompletionClient : IRoleAwareChatCompletionClient
{
    private readonly IChatCompletionClient _inner;
    private readonly UsageLedger _ledger;
    private readonly Func<ModelRole, bool> _isDeepTask;

    public UsageTrackingChatCompletionClient(
        IChatCompletionClient inner,
        UsageLedger ledger,
        Func<ModelRole, bool>? isDeepTask = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        // Deep-task accounting is an explicit host decision; no role is classified automatically.
        _isDeepTask = isDeepTask ?? (_ => false);
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var completion = await _inner.CompleteAsync(messages, tools, cancellationToken)
            .ConfigureAwait(false);
        await RecordIfRemoteAsync(ModelRole.Fast, completion, cancellationToken).ConfigureAwait(false);
        return completion;
    }

    public async Task<ChatCompletionResult> CompleteAsync(
        IReadOnlyList<ConversationMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelSelectionRequest selectionRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectionRequest);
        if (_inner is not IRoleAwareChatCompletionClient roleAware)
        {
            throw new NotSupportedException("The wrapped client does not support explicit model roles.");
        }

        var completion = await roleAware.CompleteAsync(
            messages,
            tools,
            selectionRequest,
            cancellationToken).ConfigureAwait(false);
        await RecordIfRemoteAsync(selectionRequest.Role, completion, cancellationToken).ConfigureAwait(false);
        return completion;
    }

    private async Task RecordIfRemoteAsync(
        ModelRole role,
        ChatCompletionResult completion,
        CancellationToken cancellationToken)
    {
        if (!completion.IsLocalMode)
        {
            await _ledger.RecordCompletionAsync(
                role,
                completion,
                isDeepTask: _isDeepTask(role),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
