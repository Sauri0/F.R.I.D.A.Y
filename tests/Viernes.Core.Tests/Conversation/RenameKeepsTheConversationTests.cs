using Viernes.Core.Configuration;
using Viernes.Core.Conversation;
using Viernes.Core.Models;
using Viernes.Core.OpenRouter;
using Viernes.Core.Tools;
using Xunit;

namespace Viernes.Core.Tests.Conversation;

/// <summary>
/// Cambiarle el nombre en el medio de una charla cambia el prompt y no la charla.
/// </summary>
/// <remarks>
/// El camino corto sería rehacer el orquestador con el nombre nuevo. Es lo que se hace al arrancar,
/// y ahí no cuesta nada porque todavía no hay nada hablado. En el medio de una conversación cuesta
/// la conversación entera, que es exactamente lo que el usuario no pidió cuando pidió cambiar cómo
/// se llama.
/// </remarks>
public sealed class RenameKeepsTheConversationTests
{
    [Fact]
    public async Task ElPromptPasaADecirElNombreNuevo()
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null, assistantName: "Viernes"));

        Assert.True(await orchestrator.TryRenameAsync("ana maría"));
        await orchestrator.ProcessAsync("hola");

        var system = client.Seen.First(message => message.Role == ConversationRole.System);
        Assert.StartsWith("Sos Ana María,", system.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoHabladoSigueAhiDespuesDeRenombrar()
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null, assistantName: "Viernes"));

        await orchestrator.ProcessAsync("acordate del pan");
        Assert.True(await orchestrator.TryRenameAsync("Ana"));

        var history = orchestrator.GetHistorySnapshot();
        Assert.Contains(history, message => message.Content == "acordate del pan");

        // Y el contrato con el modelo sigue siendo el primero de la lista, no uno más entre los
        // mensajes: es lo primero que lee el modelo y mudarlo cambiaría su peso.
        Assert.Equal(ConversationRole.System, history[0].Role);
        Assert.StartsWith("Sos Ana,", history[0].Content, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Con un prompt escrito a mano el nombre puede estar en cualquier parte, o no estar. Devolver
    /// <c>false</c> es lo que le permite al shell avisar que el modelo va a seguir presentándose
    /// como antes, en vez de pisar lo que escribió otro.
    /// </remarks>
    [Fact]
    public async Task ConUnPromptEscritoAManoNoSeRenombra()
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null, assistantName: "Viernes"),
            systemPrompt: "Sos un asistente de prueba.");

        Assert.False(await orchestrator.TryRenameAsync("Ana"));

        var history = orchestrator.GetHistorySnapshot();
        Assert.Equal("Sos un asistente de prueba.", history[0].Content);
    }

    /// <remarks>
    /// Un nombre que no sirve no puede dejar al asistente sin prompt: cae al de fábrica, igual que
    /// cuando el archivo de preferencias viene editado a mano.
    /// </remarks>
    [Fact]
    public async Task UnNombreQueNoSirveVuelveAlDeFabrica()
    {
        var client = new CapturingChatClient();
        var orchestrator = new ConversationOrchestrator(
            client,
            new ToolExecutor([]),
            ViernesOptions.FromEnvironment(_ => null, assistantName: "Ana"));

        Assert.True(await orchestrator.TryRenameAsync("R2D2"));

        var history = orchestrator.GetHistorySnapshot();
        Assert.StartsWith($"Sos {AssistantIdentity.DefaultName},", history[0].Content, StringComparison.Ordinal);
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
