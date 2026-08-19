namespace Viernes.Core.Live;

/// <summary>
/// Dónde sale la voz. Lo implementa el anfitrión de Windows, que es el que tiene los parlantes.
/// </summary>
/// <remarks>
/// <see cref="Flush"/> es el método que justifica que esta interfaz exista. No es «parar»: es tirar
/// <b>todo</b> lo que hay —lo que está sonando en este instante y lo que está esperando turno—.
/// <para>
/// El bug clásico de esta API es implementarlo como un «dejá de encolar»: el servidor deja de mandar
/// audio en cuanto la persona habla, así que uno cree que alcanza. No alcanza. Para cuando llega el
/// aviso ya hay varios segundos de respuesta despachados y bufferados de este lado, y la persona
/// termina de hablar y sigue escuchando a Viernes contestar la pregunta anterior. Desde afuera no se
/// ve como un problema de audio: se ve como que no escucha.
/// </para>
/// <para>
/// Es sincrónico a propósito: pasa en el camino donde cada milisegundo se oye, y no hay nada que
/// esperar para tirar audio.
/// </para>
/// </remarks>
public interface ILiveAudioSink
{
    /// <summary>Agrega audio a la cola de reproducción. PCM 16 bits little endian, mono, 24 kHz.</summary>
    ValueTask EnqueueAsync(ReadOnlyMemory<byte> pcm24k, CancellationToken cancellationToken);

    /// <summary>Tira lo que está sonando y lo que está encolado, ahora.</summary>
    void Flush();

    /// <summary>
    /// Cuánta voz queda por salir. <see cref="TimeSpan.Zero"/> cuando el parlante está callado.
    /// </summary>
    /// <remarks>
    /// Existe porque el servidor manda el audio <b>más rápido que tiempo real</b>: para cuando llega
    /// el <c>turnComplete</c> puede quedar de este lado varios segundos de respuesta sin sonar. Si
    /// nadie mira esto, el orbe vuelve a «te escucho» mientras se la sigue oyendo — la pantalla dice
    /// que escucha y en el cuarto ella sigue hablando.
    /// <para>
    /// Se dice en tiempo y no en bytes porque ésa es la unidad en la que se decide: los bytes
    /// dependen de la frecuencia de salida y quien pregunta no tiene por qué saberla. <b>Y es más
    /// que lo que quede en la cola de quien implemente esto</b>: el audio que ya se le entregó al
    /// dispositivo salió de esa cola y todavía no sonó, así que cuenta. <c>LiveSpeakerSink</c> suma
    /// los dos; contestar sólo con la cola hace que esto llegue a cero antes de que se termine de
    /// oír la última sílaba, y ahí el orbe vuelve a «te escucho» mientras en el cuarto todavía se la
    /// escucha.
    /// </para>
    /// <para>
    /// Tiene implementación por defecto —cero— para que una salida que no encola nada, como
    /// <see cref="NullLiveAudioSink"/> o una de mentira en las pruebas, no tenga que escribirla.
    /// </para>
    /// </remarks>
    TimeSpan Pending => TimeSpan.Zero;

    /// <summary>
    /// Avisa que no viene más audio de este turno.
    /// </summary>
    /// <remarks>
    /// No es lo mismo que <see cref="Flush"/> y confundirlos corta la última palabra de cada
    /// respuesta: acá lo encolado <em>termina de sonar</em>.
    /// </remarks>
    ValueTask CompleteTurnAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Una salida que descarta todo. Para poder encender la sesión sin parlantes y ver si conecta.
/// </summary>
public sealed class NullLiveAudioSink : ILiveAudioSink
{
    /// <summary>La instancia compartida; no guarda estado.</summary>
    public static NullLiveAudioSink Instance { get; } = new();

    /// <summary>Cuántas veces la mandaron a callar. Sirve para las pruebas y para diagnóstico.</summary>
    public int FlushCount { get; private set; }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(ReadOnlyMemory<byte> pcm24k, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Flush() => FlushCount++;

    /// <inheritdoc />
    public ValueTask CompleteTurnAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
