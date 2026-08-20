using System.Text.Json;
using Viernes.Core.Tools;

namespace Viernes.Core.Live;

/// <summary>
/// Una llamada a herramienta pedida por el servidor en el medio de la charla hablada.
/// </summary>
/// <remarks>
/// Los argumentos vienen clonados del mensaje: el <see cref="JsonDocument"/> que los trajo se cierra
/// apenas se termina de leer el mensaje, y la herramienta se ejecuta bastante después —abrir una
/// aplicación tarda—. Leerlos sin clonar es leer memoria devuelta.
/// </remarks>
public sealed record LiveFunctionCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// Lo que la herramienta contestó, tal como se le devuelve al servidor.
/// </summary>
/// <remarks>
/// Viaja el estado además del texto, y no es redundancia: el modelo tiene que poder distinguir «lo
/// hice» de «no pude» sin interpretar una frase en castellano. Contar como hecho algo que falló es
/// la peor forma de fallar de este proyecto, porque suena a que funcionó.
/// </remarks>
public sealed record LiveToolOutcome(string Status, string Message)
{
    /// <summary>El estado que el ejecutor de herramientas usa para «salió bien».</summary>
    public const string SucceededStatus = "Succeeded";

    /// <summary>Si la herramienta llegó a hacer lo que le pidieron.</summary>
    public bool Succeeded => string.Equals(Status, SucceededStatus, StringComparison.Ordinal);

    /// <summary>Un resultado fallido con el motivo que se le puede contar al modelo.</summary>
    public static LiveToolOutcome Failed(string message) => new("Failed", message);
}

/// <summary>Una llamada ya contestada, lista para volver al servidor.</summary>
public sealed record LiveFunctionResponse(string Id, string Name, LiveToolOutcome Outcome);

/// <summary>
/// Las manos de la sesión hablada.
/// </summary>
/// <remarks>
/// Está acá y no en la aplicación para que el cliente pueda declarar herramientas y contestar
/// llamadas sin saber quién las ejecuta —y para poder probar todo el camino sin red y sin abrir
/// nada—. Quien la implementa es el anfitrión, que es el único que tiene el ejecutor de herramientas
/// con su política.
/// <para>
/// <see cref="Declarations"/> se lee <b>en cada conexión</b>, no una vez: si entre una charla y otra
/// cambió lo que hay disponible, el setup siguiente lo refleja sin reiniciar nada.
/// </para>
/// </remarks>
public interface ILiveToolBridge
{
    /// <summary>Lo que se declara en el setup. Devolver una lista vacía es no declarar ninguna.</summary>
    IReadOnlyList<ToolDefinition> Declarations { get; }

    /// <summary>
    /// El piso al que caer si el servidor rechaza el setup con todas.
    /// </summary>
    /// <remarks>
    /// <b>Existe porque lo que se declara no se puede verificar por adelantado.</b> Los esquemas de
    /// las herramientas de servidores MCP los escribe un tercero, y el usuario puede agregar un
    /// servidor nuevo cualquier día: cualquier lista que se haya probado deja de estar probada en
    /// cuanto alguien agrega algo. Un esquema que este protocolo no acepta no da un error de campo
    /// —rebota el setup entero— así que sin un piso conocido, un servidor MCP raro deja a la
    /// asistente sin voz y sin que nadie entienda por qué.
    /// <para>
    /// Tiene que ser un subconjunto de <see cref="Declarations"/> y tiene que estar medido contra el
    /// servidor de verdad. Devolver lo mismo que <see cref="Declarations"/> es no tener piso.
    /// </para>
    /// </remarks>
    IReadOnlyList<ToolDefinition> EssentialDeclarations { get; }

    /// <summary>
    /// Ejecuta una llamada y devuelve qué pasó. Nunca debería lanzar: un error se cuenta.
    /// </summary>
    Task<LiveToolOutcome> InvokeAsync(LiveFunctionCall call, CancellationToken cancellationToken);
}

/// <summary>
/// Una herramienta arrancó o terminó adentro de la sesión hablada.
/// </summary>
/// <remarks>
/// Existe para la bitácora, no para la lógica. Cuando el usuario dice «me contestó cualquier cosa»,
/// lo único que permite saber si llegó a mover la computadora es que quede escrito qué se llamó y
/// cómo salió.
/// </remarks>
public sealed class LiveToolEventArgs(string name, bool finished, bool succeeded, string? message)
    : EventArgs
{
    /// <summary>Nombre de la herramienta.</summary>
    public string Name { get; } = name;

    /// <summary>Falso cuando recién arranca, verdadero cuando ya contestó.</summary>
    public bool Finished { get; } = finished;

    /// <summary>Si salió bien. Sólo tiene sentido con <see cref="Finished"/> puesto.</summary>
    public bool Succeeded { get; } = succeeded;

    /// <summary>Lo que contestó la herramienta, si ya contestó.</summary>
    public string? Message { get; } = message;
}
