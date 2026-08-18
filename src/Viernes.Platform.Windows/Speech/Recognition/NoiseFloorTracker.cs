namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Estima el nivel del ambiente y de ahí sale el umbral que separa «hay alguien hablando» de «no».
/// </summary>
/// <remarks>
/// Vive aparte y sin estado propio porque acá se cometió el error más caro de la voz: al acelerar la
/// adaptación para que la música quedara absorbida como fondo, el piso empezó a treparse encima del
/// habla. Como el umbral es ocho veces el piso, a los ocho buffers —justo los que se exigen de voz
/// sostenida— el umbral llegaba al 92 % del nivel de la voz y al siguiente lo pasaba: dejó de
/// detectar que alguien hablara, con el micrófono impecable.
/// <para>
/// El error no estaba en la constante sino en el planteo: un estimador de ruido de fondo que se
/// alimenta de la señal que quiere distinguir del fondo no puede funcionar. Por eso ahora lo que
/// supera el umbral no toca el piso, y perseguir la voz es imposible por construcción y no por
/// tener bien elegido un número.
/// </para>
/// </remarks>
internal static class NoiseFloorTracker
{
    /// <summary>Cuánto tiene que superar al ambiente para contar como voz.</summary>
    private const double Multiplier = 8;

    /// <summary>
    /// Piso absoluto, por si el ambiente es un estudio y el piso medido da casi cero.
    /// </summary>
    /// <remarks>
    /// Este número tiene historia y conviene contarla, porque se movió dos veces en un día y la
    /// segunda fue para corregir la primera.
    /// <para>
    /// Era 0,0012. Una medición del equipo del usuario dio voz con pico 0,0011 —o sea, el mínimo
    /// quedaba <em>por encima</em> del pico de su voz, y el 0 % de sus buffers cruzaba el umbral— así
    /// que se bajó a 0,00008. Pero una segunda medición del mismo equipo, minutos después, dio pico
    /// 1,0 y media 0,48: cuatrocientas veces más. Entre las dos, la ganancia del micrófono había
    /// cambiado. La primera medición era real pero describía una configuración transitoria, y
    /// calibrar contra ella dejó el piso prácticamente en cero, que es una invitación al ruido.
    /// <para>
    /// 0,0004 es el compromiso: un tercio del valor original —así un micrófono de ganancia baja
    /// sigue entrando— y varias veces mayor que cero —así un cuarto en silencio no pone el umbral a
    /// ras del piso—. La adaptación real la sigue haciendo el multiplicador sobre el ruido medido;
    /// esto es sólo el suelo.
    /// </para>
    /// <para>
    /// La lección para la próxima: un solo número medido una sola vez no alcanza para mover una
    /// constante. Hacen falta dos mediciones que coincidan, o saber qué cambió entre ellas.
    /// </para>
    /// </remarks>
    private const double MinimumThreshold = 0.0004;

    /// <summary>Baja rápido: el ambiente es el nivel más bajo reciente, no el promedio.</summary>
    private const double FallRate = 0.10;

    /// <summary>Sube despacio, desde muestras que ya se consideraron fondo.</summary>
    private const double RiseRate = 0.02;

    /// <summary>
    /// Subida lentísima desde muestras que superan el umbral. Existe para un cuarto que ya arranca
    /// ruidoso —música puesta, un ventilador, la calle— donde el ruido pasa el mínimo absoluto desde
    /// el primer instante y, sin esto, quedaría excluido del fondo para siempre y sonaría a voz.
    /// </summary>
    /// <remarks>
    /// El valor está elegido contra la ventana de detección, no a ojo: en los ocho buffers que se
    /// exigen de voz sostenida el piso recorre el 0,8 % de la distancia, así que el umbral queda en
    /// el 6 % del nivel de la voz y perseguirla sigue siendo imposible. Recién a la media docena de
    /// segundos de sonido continuo el fondo empieza a moverse de verdad, que es exactamente la
    /// diferencia entre una canción y una frase.
    /// </remarks>
    private const double AmbientRiseRate = 0.001;

    public static double ThresholdFor(double noiseFloor) =>
        Math.Max(noiseFloor * Multiplier, MinimumThreshold);

    /// <summary>
    /// Nuevo piso tras observar <paramref name="level"/>. El umbral se evalúa con el piso previo:
    /// la muestra que se está juzgando no puede participar de su propio criterio.
    /// </summary>
    /// <remarks>
    /// Con <paramref name="duringVoice"/> el piso sólo puede bajar. Sin esa condición había un bucle
    /// de realimentación que cortaba a la gente a mitad de frase: el piso subía despacio mientras
    /// hablabas, en algún momento el umbral pasaba el nivel de tu voz, y desde ahí tu propia voz
    /// contaba como fondo —lo que hacía subir el piso veinte veces más rápido—. Medido con el nivel
    /// real de este equipo, el umbral superaba la voz a los <b>4,5 segundos</b> de hablar seguido:
    /// una frase larga se cortaba sola. Mientras sabemos que hay alguien hablando, nada de lo que
    /// llega es ambiente, y el estimador de ambiente no tiene nada que hacer.
    /// </remarks>
    public static double Advance(double noiseFloor, double level, bool duringVoice = false)
    {
        if (level < noiseFloor)
        {
            return noiseFloor + ((level - noiseFloor) * FallRate);
        }

        if (duringVoice)
        {
            return noiseFloor;
        }

        // Por encima del umbral se lo trata como señal y casi no entra en la estimación del fondo.
        return noiseFloor + ((level - noiseFloor) *
            (level < ThresholdFor(noiseFloor) ? RiseRate : AmbientRiseRate));
    }
}
