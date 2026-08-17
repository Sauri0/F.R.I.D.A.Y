using System.Text.Json;
using Viernes.Core.Learning;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Deja que el usuario le enseñe algo de una vez y para siempre.
/// </summary>
/// <remarks>
/// Es lo que faltaba para que aprender fuera algo que el usuario <em>pide</em> y no un efecto
/// secundario que a veces ocurre. Sin esto, «acordate que cuando te pido una canción la ponés en
/// Spotify» no tenía dónde ir: el modelo lo tomaba como un pedido cualquiera y en el registro real
/// terminó resolviéndolo como una búsqueda web.
/// <para>
/// Lo aprendido acá se inyecta <b>entero y en todos los turnos</b>, no por parecido con lo que
/// acabás de decir. Una regla que sólo aparece cuando algo se le parece es una regla que se olvida
/// justo cuando hacía falta.
/// </para>
/// </remarks>
public sealed class TeachTool : IAssistantTool
{
    public const string ToolName = "aprender";

    private readonly RuleBook _rules;

    public TeachTool(RuleBook rules) => _rules = rules;

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Guarda algo que el usuario te enseña para que valga de acá en adelante. " +
        "Usala SIEMPRE que diga «aprendé que…», «acordate que…», «de ahora en más…», " +
        "«siempre que… hacé…», «no vuelvas a…», o te corrija una forma de trabajar. " +
        "regla: la instrucción redactada en una frase corta y general, no el pedido puntual. " +
        "olvidar: en vez de guardar, borra la regla que contenga ese texto. " +
        "Ejemplo: si dice «cuando te pido una canción ponela en Spotify», la regla es " +
        "«Las canciones se ponen en Spotify, no se buscan en la web».",
        ToolSchemas.Object(
            new Dictionary<string, object>
            {
                ["regla"] = ToolSchemas.String("La instrucción a recordar, en una frase."),
                ["olvidar"] = ToolSchemas.String("Texto de la regla a borrar, si se trata de olvidar.")
            },
            []),
        ToolRiskLevel.Safe);

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var forget = JsonToolArguments.OptionalString(arguments, "olvidar", 200);
        if (!string.IsNullOrWhiteSpace(forget))
        {
            var removed = await _rules.ForgetAsync(forget, cancellationToken).ConfigureAwait(false);
            return removed is null
                ? ToolExecutionResult.Failure(context.ToolCallId, ToolName, "No tenía ninguna regla sobre eso.")
                : ToolExecutionResult.Success(context.ToolCallId, ToolName, $"Listo, me olvido de: {removed}.");
        }

        var rule = JsonToolArguments.OptionalString(arguments, "regla", 200);
        if (string.IsNullOrWhiteSpace(rule))
        {
            return ToolExecutionResult.Failure(context.ToolCallId, ToolName, "Necesito saber qué aprender.");
        }

        var stored = await _rules.RememberAsync(rule, cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Success(
            context.ToolCallId,
            ToolName,
            stored ? $"Aprendido: {rule.Trim()}." : "Eso ya lo tenía aprendido.");
    }
}
