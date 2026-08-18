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
