namespace Viernes.Core.Live;

/// <summary>Lo que el servidor contó de cuántos tokens gastó el turno.</summary>
public sealed record LiveTokenUsage(int PromptTokens, int ResponseTokens, int TotalTokens);

/// <summary>
/// Un mensaje del servidor, ya leído.
/// </summary>
/// <remarks>
/// Es un solo tipo con varias banderas y no una jerarquía de clases, porque el protocolo manda
/// varias cosas juntas: un mismo mensaje puede traer audio <em>y</em> el fin del turno. Partirlo en
/// subclases obligaría a inventar un orden entre eventos que llegaron a la vez.
/// <para>
/// <see cref="Interrupted"/> es el que importa. Es el único aviso de que la persona habló encima, y
/// lo que hay que hacer al verlo no es «anotar que interrumpió» sino <b>parar la reproducción y
/// vaciar la cola en el acto</b>. Si la cola no se vacía, la persona ya está hablando y sigue
/// escuchando segundos de la respuesta vieja que estaba bufferada: la interrupción funcionó del lado
/// del servidor y no se nota del lado del usuario.
/// </para>
/// </remarks>
public sealed class LiveServerEvent
{
    /// <summary>Un mensaje que no trajo nada que nos sirva. No es un error.</summary>
    public static LiveServerEvent Empty { get; } = new();

    /// <summary>Llegó el <c>setupComplete</c>: recién ahora se puede mandar audio.</summary>
    public bool SetupComplete { get; init; }

    /// <summary>
    /// Los bloques de audio de la respuesta, PCM 16 bits a 24 kHz.
    /// </summary>
    /// <remarks>
    /// Es una lista porque un mensaje puede traer varias <c>parts</c> con audio, y quedarse con la
    /// primera deja huecos en la voz.
    /// </remarks>
    public IReadOnlyList<byte[]> Audio { get; init; } = [];

    /// <summary>Texto que el modelo devolvió como texto, si lo hubo.</summary>
    public string? Text { get; init; }

    /// <summary>Transcripción de lo que dijo la persona.</summary>
    public string? InputTranscript { get; init; }

    /// <summary>Transcripción de lo que está diciendo ella.</summary>
    public string? OutputTranscript { get; init; }

    /// <summary>La persona habló encima. Hay que cortar y vaciar la cola, no anotarlo.</summary>
    public bool Interrupted { get; init; }

    /// <summary>
    /// Terminó de generar. Todavía puede quedar audio sonando.
    /// </summary>
    /// <remarks>
    /// Después de una interrupción esto <b>no llega</b>: se salta directo a
    /// <see cref="TurnComplete"/>. Cualquier lógica que espere el generado antes del fin de turno se
    /// cuelga justo en el caso que más importa.
    /// </remarks>
    public bool GenerationComplete { get; init; }

    /// <summary>Se cerró el turno. Es el único final garantizado.</summary>
    public bool TurnComplete { get; init; }

    /// <summary>
    /// Herramientas que el servidor pide ejecutar.
    /// </summary>
    /// <remarks>
    /// Mientras esto está pendiente el turno <b>no cierra</b>: el servidor espera la respuesta antes
    /// de seguir hablando. Por eso una llamada que no se contesta no se ve como un error sino como
    /// que se quedó muda a mitad de frase.
    /// </remarks>
    public IReadOnlyList<LiveFunctionCall> FunctionCalls { get; init; } = [];

    /// <summary>
    /// Llamadas que el servidor ya no quiere que se contesten.
    /// </summary>
    /// <remarks>
    /// Llega cuando la persona interrumpió mientras la herramienta corría. Lo que la herramienta ya
    /// hizo, hecho está —abrir una aplicación no se deshace—; lo que cambia es que la respuesta ya
    /// no tiene a dónde ir, y mandarla igual le contesta a un turno que del otro lado no existe.
    /// </remarks>
    public IReadOnlyList<string> CancelledToolCalls { get; init; } = [];

    /// <summary>El handle para reconectar sin perder la conversación.</summary>
    public string? ResumptionHandle { get; init; }

    /// <summary>Si ese handle sirve para reanudar de verdad.</summary>
    public bool ResumptionHandleIsResumable { get; init; }

    /// <summary>Cuánto falta para que el servidor cierre la conexión.</summary>
    /// <remarks>
    /// Llega solo, antes de cerrar, y es la ventana que hay para reconectar sin que se note. La
    /// conexión dura unos diez minutos y la sesión de audio quince: esto no es una excepción rara,
    /// es parte del funcionamiento normal de cualquier charla larga.
    /// </remarks>
    public TimeSpan? GoAwayTimeLeft { get; init; }

    /// <summary>Lo que gastó el turno, cuando el servidor lo informa.</summary>
    public LiveTokenUsage? Usage { get; init; }

    /// <summary>El servidor mandó un error antes de cortar.</summary>
    public string? Error { get; init; }

    /// <summary>Si el mensaje no trajo nada accionable.</summary>
    public bool IsEmpty =>
        !SetupComplete &&
        Audio.Count == 0 &&
        Text is null &&
        InputTranscript is null &&
        OutputTranscript is null &&
        !Interrupted &&
        !GenerationComplete &&
        !TurnComplete &&
        FunctionCalls.Count == 0 &&
        CancelledToolCalls.Count == 0 &&
        ResumptionHandle is null &&
        GoAwayTimeLeft is null &&
        Usage is null &&
        Error is null;

    /// <summary>Cuántos bytes de audio trajo en total.</summary>
    public int AudioByteCount
    {
        get
        {
            var total = 0;
            for (var i = 0; i < Audio.Count; i++)
            {
                total += Audio[i].Length;
            }

            return total;
        }
    }
}
