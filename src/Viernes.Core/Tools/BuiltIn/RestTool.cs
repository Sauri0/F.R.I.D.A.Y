using System.Text.Json;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>Qué tan lejos se aparta cuando le piden que pare.</summary>
public enum RestDepth
{
    /// <summary>Deja de hablar, sigue en la conversación.</summary>
    Callar,

    /// <summary>Cierra la conversación y vuelve a esperar que la llamen por su nombre.</summary>
    Dormir,

    /// <summary>Suelta el micrófono del todo. Hay que reactivarla a mano.</summary>
    Apagar
}

/// <summary>
/// Dejar de escuchar cuando se lo piden, entendiendo la intención y no la palabra.
/// </summary>
/// <remarks>
/// Antes esto lo resolvía una lista de frases comparada contra la transcripción, antes de que el
/// modelo viera nada. Una lista nunca alcanza: cubría «callate» y «descansá» y no cubría
/// «desactivate», y no hay forma de escribirlas todas —«tomate cinco», «apagá el micrófono», «basta
/// por hoy», «andá a hacer otra cosa»—. Cada palabra que falta es un pedido de parar que se ignora,
/// que es el peor momento para no entender.
/// <para>
/// La lista rápida sigue existiendo para lo literal, porque «callate» tiene que cortar al instante y
/// no después de una ida y vuelta al modelo. Esta herramienta cubre todo lo demás: lo que hace falta
/// entender en vez de reconocer.
/// </para>
/// </remarks>
public sealed class RestTool : IAssistantTool
{
    public const string ToolName = "descansar";

    private readonly Func<RestDepth, CancellationToken, Task>? _rest;

    public RestTool(Func<RestDepth, CancellationToken, Task>? rest = null) => _rest = rest;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Usala cuando el usuario quiere que dejes de escuchar o de hablar, con las palabras que sea. " +
        "No hay una lista de frases: es por intención. «Callate», «desactivate», «apagate», «tomate " +
        "cinco», «basta por hoy», «andá a hacer otra cosa», «no me escuches más», «dejame tranquilo» " +
        "son todos esto. " +
        "nivel=«callar» si sólo quiere que te calles y sigas ahí. " +
        "nivel=«dormir» si quiere terminar la charla y que vuelvas a esperar que te llame por tu " +
        "nombre; es el caso normal y el que usás si dudás entre dormir y apagar. " +
        "nivel=«apagar» sólo si pide explícitamente que sueltes el micrófono o te desactives del todo. " +
        "Si no estás segura de si te está pidiendo que pares o está hablando de otra cosa " +
        "—«cortá el video», «terminá el informe»— NO uses esto: preguntale.",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["nivel"] = ToolSchemas.String("callar, dormir o apagar.")
            },
            ["nivel"]),
        ToolRiskLevel.Safe);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = JsonToolArguments.OptionalString(arguments, "nivel", 20)?.ToLowerInvariant();

        // Ante la duda, dormir. Callar de menos deja el micrófono abierto cuando pidieron que no;
        // apagar de más obliga a reactivarla a mano, que es peor que tener que volver a nombrarla.
        var depth = raw switch
        {
            "callar" or "callate" or "silencio" => RestDepth.Callar,
            "apagar" or "apagate" or "desactivar" or "desactivate" => RestDepth.Apagar,
            _ => RestDepth.Dormir
        };

        if (_rest is null)
        {
            return ToolExecutionResult.Failure(
                context.ToolCallId,
                ToolName,
                "No tengo control del micrófono en este contexto.");
        }

        await _rest(depth, cancellationToken).ConfigureAwait(false);

        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            depth switch
            {
                RestDepth.Callar => "Me callo.",
                RestDepth.Apagar => "Listo, me desactivo. Prendeme desde la bandeja cuando quieras.",
                _ => "Dale, quedo esperando que me llames."
            },
            new { nivel = depth.ToString() });
    }
}
