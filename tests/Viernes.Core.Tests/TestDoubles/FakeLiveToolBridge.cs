using System.Text.Json;
using Viernes.Core.Live;
using Viernes.Core.Tools;

namespace Viernes.Core.Tests.TestDoubles;

/// <summary>
/// Unas manos de mentira: anotan lo que les pidieron y contestan cuando la prueba quiere.
/// </summary>
/// <remarks>
/// Poder demorarlas es lo que importa. Lo que hay que verificar no es que una herramienta ande
/// —eso se prueba en su propia prueba— sino que mientras corre el cliente <b>siga leyendo</b>: abrir
/// una aplicación tarda un segundo largo, y ese segundo es justo el que la persona puede usar para
/// hablarle encima.
/// </remarks>
public sealed class FakeLiveToolBridge : ILiveToolBridge
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <param name="blocks">Si se queda esperando a <see cref="Release"/> antes de contestar.</param>
    public FakeLiveToolBridge(bool blocks = false)
    {
        if (!blocks)
        {
            _released.TrySetResult();
        }
    }

    /// <summary>Lo que se declara en el setup.</summary>
    public List<ToolDefinition> Tools { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Declarations => Tools;

    /// <summary>Cuántas veces la llamaron.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>El nombre de la última llamada.</summary>
    public string? LastName { get; private set; }

    /// <summary>Los argumentos de la última llamada, tal como llegaron.</summary>
    public JsonElement LastArguments { get; private set; }

    /// <summary>Lo que va a contestar.</summary>
    public LiveToolOutcome Outcome { get; set; } = new(LiveToolOutcome.SucceededStatus, "Listo.");

    /// <summary>La deja contestar.</summary>
    public void Release() => _released.TrySetResult();

    /// <inheritdoc />
    public async Task<LiveToolOutcome> InvokeAsync(LiveFunctionCall call, CancellationToken cancellationToken)
    {
        LastName = call.Name;
        LastArguments = call.Arguments;
        Interlocked.Increment(ref _calls);

        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Outcome;
    }
}
