using System.Globalization;

namespace Viernes.Core.Live;

/// <summary>
/// Cómo se abre y cómo se comporta la sesión en vivo.
/// </summary>
/// <remarks>
/// Viene <b>apagada</b> de fábrica. No por desconfianza en el camino nuevo sino porque el camino
/// viejo —reconocer, pensar, sintetizar— sigue funcionando y hay que poder volver a él sin
/// recompilar: si la sesión en vivo se porta mal en el equipo del usuario, apagar una variable de
/// entorno tiene que alcanzar para recuperar un asistente que anda.
/// <para>
/// Los valores por defecto que importan están medidos o recomendados, no elegidos a ojo, y cada uno
/// dice de dónde salió.
/// </para>
/// </remarks>
public sealed class GeminiLiveOptions
{
    /// <summary>Enciende o apaga todo el camino en vivo.</summary>
    public const string EnabledEnvironmentVariable = "VIERNES_LIVE";

    /// <summary>Permite cambiar de modelo sin recompilar cuando el preview se renombre.</summary>
    public const string ModelEnvironmentVariable = "VIERNES_LIVE_MODEL";

    /// <summary>Por si el usuario cambia de opinión sobre la voz.</summary>
    public const string VoiceEnvironmentVariable = "VIERNES_LIVE_VOICE";

    /// <summary>Milisegundos de silencio que el servidor espera para dar por cerrado el turno.</summary>
    public const string SilenceEnvironmentVariable = "VIERNES_LIVE_SILENCE_MS";

    /// <summary>Tamaño del fragmento de audio que se sube, en milisegundos.</summary>
    public const string ChunkEnvironmentVariable = "VIERNES_LIVE_CHUNK_MS";

    /// <summary>Con qué apagar la búsqueda web de la sesión hablada.</summary>
    public const string WebSearchEnvironmentVariable = "VIERNES_LIVE_WEB_SEARCH";

    /// <summary>Modelo de la sesión en vivo. Lleva <c>-preview</c> y existe en la cuenta del usuario.</summary>
    public const string DefaultModel = "gemini-3.1-flash-live-preview";

    /// <summary>La voz que el usuario eligió escuchando las catorce.</summary>
    public const string DefaultVoice = "Aoede";

    /// <summary>
    /// Silencio, en milisegundos, para que el servidor considere que la persona terminó de hablar.
    /// </summary>
    /// <remarks>
    /// Google recomienda entre 500 y 800. Abajo de eso —los 100 o 200 que uno pondría buscando
    /// reflejos— el servidor corta la frase en cada pausa para respirar: no baja la latencia, parte
    /// una oración en tres turnos y la calidad de la respuesta se cae con ella.
    /// </remarks>
    public const int DefaultSilenceDurationMs = 700;

    /// <summary>Extremos aceptados para el silencio de fin de turno.</summary>
    private const int MinimumSilenceDurationMs = 100;
    private const int MaximumSilenceDurationMs = 5_000;

    /// <summary>
    /// Cuánto audio entra en cada fragmento que se sube.
    /// </summary>
    /// <remarks>
    /// Veinte milisegundos. La tentación es juntar un segundo y mandar de a uno —menos mensajes,
    /// menos trabajo—, y es exactamente lo que arruina esto: cada segundo que se acumula acá es un
    /// segundo que el detector de voz del servidor todavía no vio, así que se suma entero a la
    /// demora en contestar y a la demora en darse cuenta de que la persona la interrumpió.
    /// </remarks>
    public const int DefaultChunkMilliseconds = 20;

    private const int MinimumChunkMilliseconds = 10;
    private const int MaximumChunkMilliseconds = 60;

    /// <summary>
    /// A partir de cuántos tokens de contexto el servidor empieza a comprimir.
    /// </summary>
    /// <remarks>
    /// Son los números del ejemplo de la documentación de Google, no una elección propia. Sin
    /// compresión la sesión no es que degrade: se muere sola al llenar la ventana. Y como en cada
    /// turno se cobra el contexto completo, la ventana deslizante además es una cuestión de plata.
    /// </remarks>
    public const long DefaultTriggerTokens = 25_600;

    /// <summary>A cuánto se recorta el contexto cuando la compresión se dispara.</summary>
    public const long DefaultTargetTokens = 12_800;

    /// <summary>
    /// Arma la configuración validando lo que se puede validar acá.
    /// </summary>
    /// <remarks>
    /// Los rangos son anchos a propósito: son una red contra el error de tipeo —un cero de más en
    /// los milisegundos—, no una opinión sobre qué valor conviene. La opinión está en los valores
    /// por defecto y en los comentarios de cada uno.
    /// </remarks>
    public GeminiLiveOptions(
        bool enabled = false,
        string? model = null,
        string? voiceName = null,
        string? systemInstruction = null,
        int silenceDurationMs = DefaultSilenceDurationMs,
        int chunkMilliseconds = DefaultChunkMilliseconds,
        bool interruptOnUserSpeech = true,
        bool transcribeInput = true,
        bool transcribeOutput = true,
        long triggerTokens = DefaultTriggerTokens,
        long targetTokens = DefaultTargetTokens,
        TimeSpan? setupTimeout = null,
        bool webSearch = true)
    {
        Enabled = enabled;
        Model = Normalize(model) ?? DefaultModel;
        VoiceName = Normalize(voiceName) ?? DefaultVoice;
        SystemInstruction = Normalize(systemInstruction);

        SilenceDurationMs = silenceDurationMs is >= MinimumSilenceDurationMs and <= MaximumSilenceDurationMs
            ? silenceDurationMs
            : throw new ArgumentOutOfRangeException(
                nameof(silenceDurationMs),
                $"Usá entre {MinimumSilenceDurationMs} y {MaximumSilenceDurationMs} milisegundos de silencio.");

        ChunkMilliseconds = chunkMilliseconds is >= MinimumChunkMilliseconds and <= MaximumChunkMilliseconds
            ? chunkMilliseconds
            : throw new ArgumentOutOfRangeException(
                nameof(chunkMilliseconds),
                $"Usá fragmentos de entre {MinimumChunkMilliseconds} y {MaximumChunkMilliseconds} milisegundos.");

        InterruptOnUserSpeech = interruptOnUserSpeech;
        TranscribeInput = transcribeInput;
        TranscribeOutput = transcribeOutput;
        WebSearch = webSearch;

        if (triggerTokens is < 1_000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(triggerTokens), "El disparador de compresión tiene que estar entre 1000 y 1000000 tokens.");
        }

        if (targetTokens < 500 || targetTokens >= triggerTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTokens),
                "La ventana a la que se recorta tiene que ser menor que el disparador; si no, comprimir no libera nada.");
        }

        TriggerTokens = triggerTokens;
        TargetTokens = targetTokens;
        SetupTimeout = setupTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Si el camino en vivo está encendido. Apagado deja intacto el camino de siempre.</summary>
    public bool Enabled { get; }

    /// <summary>Modelo, sin el prefijo <c>models/</c>: eso lo agrega el armado del setup.</summary>
    public string Model { get; }

    /// <summary>Nombre de la voz preconstruida.</summary>
    public string VoiceName { get; }

    /// <summary>Instrucción de sistema, o <c>null</c> para no mandar ninguna.</summary>
    public string? SystemInstruction { get; }

    /// <summary>Silencio de fin de turno, en milisegundos.</summary>
    public int SilenceDurationMs { get; }

    /// <summary>Duración de cada fragmento de audio que se sube.</summary>
    public int ChunkMilliseconds { get; }

    /// <summary>Bytes que ocupa un fragmento del tamaño elegido.</summary>
    public int ChunkBytes => LiveAudioFormat.InputBytesForMilliseconds(ChunkMilliseconds);

    /// <summary>
    /// Si hablarle encima la calla.
    /// </summary>
    /// <remarks>
    /// Es el valor por defecto del servidor y es <em>lo que hace</em> que se la pueda interrumpir.
    /// Apagarlo la convierte en alguien que termina su párrafo pase lo que pase, que es justamente
    /// lo que esta sesión vino a arreglar. Está expuesto porque existe el caso de querer que lea
    /// algo largo sin que un ruido la corte.
    /// </remarks>
    public bool InterruptOnUserSpeech { get; }

    /// <summary>
    /// Si la sesión hablada puede buscar en la web.
    /// </summary>
    /// <remarks>
    /// <b>Hablando no podía buscar nada, y eso no se veía por ningún lado.</b> La búsqueda web del
    /// proyecto la inyecta el proveedor del camino escrito; el camino hablado no pasa por ahí, va
    /// directo a este servidor. Así que por voz contestaba de memoria, con fecha de corte, mientras
    /// el texto que le pega sus manos al modelo le prometía que podía «buscar en la web».
    /// <para>
    /// <b>Medido, no supuesto.</b> Preguntarle por una noticia de esta semana, con la misma clave y
    /// el mismo modelo, dos corridas:
    /// <code>
    ///   sin la herramienta: «No tengo acceso a noticias en tiempo real, así que no puedo…»
    ///   con la herramienta: contestó la noticia
    /// </code>
    /// Y de paso quedó descartada la prueba fácil: mandar el setup con la herramienta y ver si
    /// vuelve <c>setupComplete</c> no prueba nada, porque el setup <em>sin ninguna</em> también
    /// vuelve completo. Este servidor acepta campos que no conoce. Aceptar no es activar.
    /// </para>
    /// <para>
    /// Prendida por omisión, por el mismo motivo que en el camino escrito: un asistente que no puede
    /// mirar la web contesta de memoria. Se apaga con <see cref="WebSearchEnvironmentVariable"/>.
    /// </para>
    /// </remarks>
    public bool WebSearch { get; }

    /// <summary>Si se pide la transcripción de lo que dice la persona.</summary>
    /// <remarks>
    /// Encendida porque, sin texto, el resto de Viernes se queda ciego: la memoria, las misiones y
    /// la pantalla trabajan sobre palabras, y una conversación que sólo existió como audio no deja
    /// nada atrás.
    /// </remarks>
    public bool TranscribeInput { get; }

    /// <summary>Si se pide la transcripción de lo que ella contesta.</summary>
    public bool TranscribeOutput { get; }

    /// <summary>Tokens de contexto a partir de los cuales se comprime.</summary>
    public long TriggerTokens { get; }

    /// <summary>Tokens a los que se recorta al comprimir.</summary>
    public long TargetTokens { get; }

    /// <summary>Cuánto se espera el <c>setupComplete</c> antes de dar la conexión por perdida.</summary>
    public TimeSpan SetupTimeout { get; }

    /// <summary>
    /// Lee la configuración del entorno.
    /// </summary>
    /// <remarks>
    /// Un valor mal escrito no tumba el arranque: se cae al valor por defecto. Es deliberado y es lo
    /// contrario de lo que hace <c>ViernesOptions</c>, que sí lanza. La diferencia es qué se pierde:
    /// allá una variable mal puesta puede significar gastar de más sin darse cuenta, así que conviene
    /// que grite; acá lo peor que pasa es que el silencio de fin de turno quede en 700 en vez de en
    /// 650, y que eso impida abrir el programa sería desproporcionado.
    /// </remarks>
    /// <param name="readVariable">De dónde salen los valores.</param>
    /// <param name="systemInstruction">La instrucción de sistema de la sesión hablada.</param>
    /// <param name="hasKey">
    /// Si hay clave de Google puesta. Es lo que decide el valor por omisión del interruptor.
    /// </param>
    public static GeminiLiveOptions FromEnvironment(
        Func<string, string?>? readVariable = null,
        string? systemInstruction = null,
        bool hasKey = false)
    {
        readVariable ??= Environment.GetEnvironmentVariable;

        return new GeminiLiveOptions(
            // Arranca prendida si hay clave, y eso es un cambio deliberado.
            //
            // Estaba en false fijo. El usuario abría claves.json, pegaba su clave de Google
            // justamente para tener la sesión hablada, guardaba, y la sesión no se usaba nunca:
            // la traza decía «la sesión en vivo está apagada por configuración» y el interruptor
            // ni siquiera figuraba en el archivo que se le pidió que editara. Todo el camino nuevo
            // quedaba escrito y muerto, que es exactamente el modo de falla que este proyecto
            // viene persiguiendo.
            //
            // Pegar la clave ES la decisión. Pedir dos gestos para encender una sola cosa —pegar
            // la clave y además descubrir una variable sin documentar— no es prudencia, es una
            // función que no existe. Quien quiera el camino de siempre teniendo clave puesta,
            // escribe VIERNES_LIVE=false y listo.
            enabled: ParseBoolean(readVariable(EnabledEnvironmentVariable), defaultValue: hasKey),
            model: readVariable(ModelEnvironmentVariable),
            voiceName: readVariable(VoiceEnvironmentVariable),
            systemInstruction: systemInstruction,
            silenceDurationMs: ParseInt(
                readVariable(SilenceEnvironmentVariable),
                DefaultSilenceDurationMs,
                MinimumSilenceDurationMs,
                MaximumSilenceDurationMs),
            webSearch: ParseBoolean(readVariable(WebSearchEnvironmentVariable), defaultValue: true),
            chunkMilliseconds: ParseInt(
                readVariable(ChunkEnvironmentVariable),
                DefaultChunkMilliseconds,
                MinimumChunkMilliseconds,
                MaximumChunkMilliseconds));
    }

    /// <summary>Copia con la instrucción de sistema cambiada, que es lo único que arma el anfitrión.</summary>
    public GeminiLiveOptions WithSystemInstruction(string? instruction) =>
        new(
            Enabled,
            Model,
            VoiceName,
            instruction,
            SilenceDurationMs,
            ChunkMilliseconds,
            InterruptOnUserSpeech,
            TranscribeInput,
            TranscribeOutput,
            TriggerTokens,
            TargetTokens,
            SetupTimeout);

    /// <summary>Nunca incluye credenciales: acá no hay ninguna.</summary>
    public override string ToString() =>
        $"GeminiLiveOptions {{ Enabled = {Enabled}, Model = {Model}, Voice = {VoiceName}, " +
        $"SilenceDurationMs = {SilenceDurationMs}, ChunkMs = {ChunkMilliseconds}, " +
        $"Interrumpible = {InterruptOnUserSpeech} }}";

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBoolean(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "yes" or "on" or "si" or "sí" => true,
            "0" or "no" or "off" => false,
            _ => defaultValue
        };
    }

    private static int ParseInt(string? value, int defaultValue, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return parsed >= minimum && parsed <= maximum ? parsed : defaultValue;
    }
}
