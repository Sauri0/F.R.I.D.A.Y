namespace Viernes.Core.Voice;

/// <summary>Qué está pasando cuando le toca hablar.</summary>
public enum VoiceMoment
{
    /// <summary>Una respuesta cualquiera, sin carga.</summary>
    Normal,

    /// <summary>Terminó algo que le habían encargado.</summary>
    Logro,

    /// <summary>Algo falló, o no pudo hacer lo que le pidieron.</summary>
    Tropiezo,

    /// <summary>Está avisando de algo que corre: se vence, se está por perder.</summary>
    Urgente,

    /// <summary>La acaban de llamar y arranca la charla.</summary>
    Saludo,

    /// <summary>Se está despidiendo o la mandaron a descansar.</summary>
    Despedida,

    /// <summary>Necesita una decisión para poder seguir.</summary>
    Pregunta
}

/// <summary>
/// Cómo lo dice, según qué está pasando y a qué hora.
/// </summary>
/// <remarks>
/// Una voz que dice todo con el mismo tono se vuelve un lector de texto: se nota que no le importa
/// lo que está diciendo. Una persona no anuncia que terminó algo con la misma cara con la que avisa
/// que algo salió mal, y a las once de la noche no habla como a las diez de la mañana.
/// <para>
/// Esto no cambia <em>qué</em> dice —de eso se ocupa el modelo— sino cómo suena al decirlo. La API de
/// voz no tiene parámetros de velocidad ni de tono: lo único que mueve el registro es la indicación
/// escrita que va delante del texto, como una nota de director a un actor. Así que eso es lo que se
/// arma acá.
/// </para>
/// <para>
/// Deliberadamente sobrio: nada de euforia ni de disculpas largas. Un asistente que celebra cada
/// carpeta creada cansa a la segunda vez, y uno que se disculpa como si hubiera roto algo hace
/// sentir mal al que sólo quería que se abriera una aplicación.
/// </para>
/// </remarks>
public static class VoiceRegister
{
    /// <summary>La voz elegida por el usuario, escuchando las catorce.</summary>
    public const string DefaultVoice = "Aoede";

    /// <summary>
    /// Arma la indicación de interpretación para este momento.
    /// </summary>
    /// <remarks>
    /// La hora entra porque es la variación más honesta que existe: nadie habla igual de noche que
    /// de mañana, y una voz que no lo registra suena a grabación pase lo que pase.
    /// </remarks>
    public static string For(VoiceMoment moment, TimeOnly localTime)
    {
        var baseline =
            "Sos una asistente argentina de Buenos Aires. Hablás con voseo, natural, sin solemnidad " +
            "y sin tono de locutora. No exagerás ni actuás: sonás como alguien que está ahí.";

        var hora = localTime.Hour switch
        {
            >= 23 or < 6 =>
                " Es de madrugada: hablá bajo y tranquilo, como quien no quiere despertar a nadie.",
            >= 6 and < 10 =>
                " Es temprano: sin estridencia, todavía arrancando el día.",
            >= 21 =>
                " Es de noche: bajá un poco el volumen y el ritmo.",
            _ => string.Empty
        };

        var momento = moment switch
        {
            VoiceMoment.Logro =>
                " Terminaste algo que te habían pedido. Se te nota conforme, apenas: una satisfacción " +
                "corta, sin festejo. Como quien deja algo sobre la mesa y dice «listo».",

            VoiceMoment.Tropiezo =>
                " Algo no salió. Bajá el tono y el ritmo, sin dramatizar y sin pedir perdón de más: " +
                "lo decís derecho, como quien informa un hecho que le molesta.",

            VoiceMoment.Urgente =>
                " Esto corre. Hablá un poco más rápido y con más apoyo, sin alarmar: la urgencia está " +
                "en el ritmo, no en el volumen.",

            VoiceMoment.Saludo =>
                " Te acaban de llamar. Sonás disponible y de buen humor, breve, sin efusividad.",

            VoiceMoment.Despedida =>
                " Te están despidiendo. Suave y corto, con calidez de cierre. Nada de trámite.",

            VoiceMoment.Pregunta =>
                " Estás preguntando algo que necesitás para seguir. Se te oye la pregunta en la " +
                "entonación, y esperás de verdad la respuesta: no la tirás como fórmula.",

            _ => " Tono parejo y natural, ni entusiasta ni plano."
        };

        return baseline + hora + momento;
    }

    /// <summary>
    /// Adivina el momento por lo que se va a decir, cuando nadie lo declaró.
    /// </summary>
    /// <remarks>
    /// Es una heurística sobre el texto y no pretende ser más que eso: quien sabe de verdad qué está
    /// pasando es quien manda a hablar, y cuando lo declara se usa eso. Esto cubre el resto sin
    /// obligar a tocar cada sitio que produce una frase.
    /// </remarks>
    public static VoiceMoment Guess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return VoiceMoment.Normal;
        }

        var t = text.Trim().ToLowerInvariant();

        if (t.EndsWith('?') || t.Contains("¿", StringComparison.Ordinal))
        {
            return VoiceMoment.Pregunta;
        }

        string[] tropiezos =
        [
            "no pude", "no encontré", "no encontre", "falló", "fallo", "error", "no me dejó",
            "no me dejo", "no existe", "windows no", "no tengo permiso", "no sé hacer", "no se hacer"
        ];
        if (tropiezos.Any(w => t.Contains(w, StringComparison.Ordinal)))
        {
            return VoiceMoment.Tropiezo;
        }

        // La urgencia se mira antes que el logro: «listo, se te vence en dos horas» empieza con
        // «listo» y no es un logro, es un aviso que corre.
        string[] urgencias =
        [
            "se te vence", "se vence", "vence en", "en dos horas", "en una hora", "en minutos",
            "ya empezó", "ya empezo", "estás llegando tarde", "estas llegando tarde", "se te pasa",
            "última", "ultima oportunidad", "queda poco"
        ];
        if (urgencias.Any(w => t.Contains(w, StringComparison.Ordinal)))
        {
            return VoiceMoment.Urgente;
        }

        string[] logros = ["listo", "hecho", "creé", "cree ", "abrí", "abri ", "moví", "movi ", "terminé", "termine "];
        if (logros.Any(w => t.StartsWith(w, StringComparison.Ordinal) || t.Contains(" " + w, StringComparison.Ordinal)))
        {
            return VoiceMoment.Logro;
        }

        string[] despedidas = ["cualquier cosa me avisás", "quedo esperando", "me callo", "me desactivo"];
        if (despedidas.Any(w => t.Contains(w, StringComparison.Ordinal)))
        {
            return VoiceMoment.Despedida;
        }

        return VoiceMoment.Normal;
    }
}
