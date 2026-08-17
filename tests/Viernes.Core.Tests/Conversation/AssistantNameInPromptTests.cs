using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Tools;
using Xunit;

namespace Viernes.Core.Tests.Conversation;

/// <summary>
/// El nombre elegido tiene que llegar a la primera línea del prompt del sistema.
/// </summary>
/// <remarks>
/// Es el único lugar donde el nombre cambia el comportamiento y no sólo la decoración: si el prompt
/// dice «Sos Viernes» y el usuario lo llama «Ana», el modelo se corrige a sí mismo en voz alta y
/// contesta que en realidad se llama Viernes.
/// </remarks>
public sealed class AssistantNameInPromptTests
{
    [Theory]
    [InlineData("Ana")]
    [InlineData("JARVIS")]
    [InlineData("Friday")]
    public async Task ElPromptSePresentaConElNombreElegido(string name)
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null, assistantName: name));

        await orchestrator.ProcessAsync("hola");

        var system = Assert.Single(client.Seen, message => message.Role == ConversationRole.System);
        Assert.StartsWith($"Sos {name},", system.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{NOMBRE}", system.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinNombreElegidoSigueSiendoElDeFabrica()
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null));

        await orchestrator.ProcessAsync("hola");

        var system = Assert.Single(client.Seen, message => message.Role == ConversationRole.System);
        Assert.StartsWith("Sos Viernes,", system.Content, StringComparison.Ordinal);
    }

    private sealed class CapturingChatClient : IChatCompletionClient
    {
        public List<ConversationMessage> Seen { get; } = [];

        public Task<ChatCompletionResult> CompleteAsync(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken = default)
        {
            this.Seen.AddRange(messages);
            return Task.FromResult(new ChatCompletionResult("listo", [], "test/model"));
        }
    }
}
