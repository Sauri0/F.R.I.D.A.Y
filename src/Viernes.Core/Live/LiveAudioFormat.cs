namespace Viernes.Core.Live;

/// <summary>
/// Los dos formatos de audio de la sesión en vivo, que no son el mismo.
/// </summary>
/// <remarks>
/// Lo que sube va a 16 kHz y lo que baja vuelve a 24 kHz. No es un descuido de la API: el
/// reconocimiento trabaja a 16 y el sintetizador produce a 24. Confundirlos es el error más común de
/// esta API y además es un error traicionero, porque no rompe nada: reproducir a 16 kHz lo que vino
/// a 24 suena grave y lento, lo bastante «voz» como para que uno busque el problema en el modelo, en
/// el prompt o en el micrófono antes que en un número.
/// <para>
/// Por eso están acá, con nombre, y no como enteros sueltos en cada llamada.
/// </para>
/// </remarks>
public static class LiveAudioFormat
{
    /// <summary>Frecuencia de lo que se le manda: PCM 16 bits little endian, mono.</summary>
    public const int InputSampleRate = 16_000;

    /// <summary>Frecuencia de lo que devuelve: PCM 16 bits little endian, mono.</summary>
    public const int OutputSampleRate = 24_000;

    /// <summary>Bits por muestra, en los dos sentidos.</summary>
    public const int BitsPerSample = 16;

    /// <summary>Canales, en los dos sentidos. Mono: estéreo no está soportado.</summary>
    public const int Channels = 1;

    /// <summary>Bytes que ocupa una muestra. Con 16 bits mono, dos.</summary>
    public const int BytesPerSample = BitsPerSample / 8 * Channels;

    /// <summary>Lo que hay que declarar en cada fragmento que se envía.</summary>
    /// <remarks>
    /// La frecuencia va escrita en el propio tipo MIME —no en un campo aparte—, así que si alguien
    /// cambia <see cref="InputSampleRate"/> sin tocar esto el servidor recibe una mentira y
    /// reconstruye mal el audio. Se arma con interpolación justamente para que no puedan separarse.
    /// </remarks>
    public static string InputMimeType { get; } = $"audio/pcm;rate={InputSampleRate}";

    /// <summary>El tipo MIME con el que vuelve el audio de la respuesta.</summary>
    public static string OutputMimeType { get; } = $"audio/pcm;rate={OutputSampleRate}";

    /// <summary>Cuántos bytes de entrada son estos milisegundos de audio.</summary>
    /// <remarks>
    /// Redondea hacia abajo a muestra entera: mandar medio par de bytes desalinea todo lo que sigue,
    /// porque el servidor lee de a dos y no tiene forma de saber que arrancó corrido.
    /// </remarks>
    public static int InputBytesForMilliseconds(int milliseconds) =>
        milliseconds <= 0
            ? 0
            : milliseconds * InputSampleRate / 1000 * BytesPerSample;

    /// <summary>Cuánto dura, dicho, este bloque de audio de respuesta.</summary>
    public static TimeSpan OutputDurationOf(int byteCount) =>
        byteCount <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)byteCount / (OutputSampleRate * BytesPerSample));

    /// <summary>Cuánto dura, dicho, este bloque de audio de entrada.</summary>
    public static TimeSpan InputDurationOf(int byteCount) =>
        byteCount <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)byteCount / (InputSampleRate * BytesPerSample));
}
