namespace Viernes.Core.Live;

/// <summary>Por dónde va a pasar esta conversación hablada.</summary>
public enum VoiceRoute
{
    /// <summary>Reconocer acá, pensar en la nube, sintetizar acá. Tres servicios y tres esperas.</summary>
    Classic,

    /// <summary>Una sola conexión dúplex con Gemini Live. Se la puede interrumpir hablándole encima.</summary>
    Live
}

/// <summary>
/// Qué camino se eligió y por qué.
/// </summary>
/// <remarks>
/// El motivo viaja con la decisión y no se calcula después a propósito: «se fue por el camino de
/// siempre» sin decir si fue porque falta la clave, porque está apagado o porque la sesión se cayó
/// hace un minuto obliga a abrir tres archivos para averiguarlo. Es la línea que se escribe en la
/// bitácora, así que nunca puede contener la credencial —por eso el router recibe un booleano y no
/// la clave—.
/// </remarks>
public readonly record struct VoiceRouteDecision(VoiceRoute Route, string Reason)
{
    /// <summary>Si se eligió la sesión en vivo.</summary>
    public bool IsLive => Route == VoiceRoute.Live;

    /// <summary>Una línea para la bitácora. Nunca lleva credenciales.</summary>
    public override string ToString() => $"{(IsLive ? "vivo" : "siempre")} · {Reason}";
}

/// <summary>
/// Decide por cuál de los dos caminos hablar.
/// </summary>
/// <remarks>
/// Es una función pura y vive en Core por dos razones. Una: es lo único de toda la elección que se
/// puede probar, y hay que probarlo porque un asistente que elige mal el camino se queda mudo. Dos:
/// <b>no recibe la clave, recibe si la hay</b>. Que la firma no admita el secreto es lo que
/// garantiza que la decisión —que se escribe en la bitácora entera, con su motivo— no pueda
/// filtrarlo ni por descuido.
/// </remarks>
public static class VoiceRouter
{
    /// <summary>El motivo cuando se va por el camino nuevo.</summary>
    public const string LiveReason = "hay clave de Google y la sesión en vivo está encendida";

    /// <summary>El motivo cuando la sesión en vivo está apagada por configuración.</summary>
    public const string DisabledReason = "la sesión en vivo está apagada por configuración";

    /// <summary>El motivo cuando falta la credencial.</summary>
    public const string MissingKeyReason = "falta la clave de Google";

    /// <summary>
    /// Elige el camino.
    /// </summary>
    /// <param name="liveEnabled">Si la sesión en vivo está encendida por configuración.</param>
    /// <param name="hasGoogleKey">
    /// Si hay credencial. <b>Es un booleano y no la clave</b>: acá adentro no entra ningún secreto,
    /// porque lo que sale de acá se escribe en la bitácora.
    /// </param>
    /// <param name="blockedReason">
    /// Por qué la sesión en vivo está trabada, si lo está. Sale de <see cref="LiveFallbackLatch"/>.
    /// </param>
    /// <remarks>
    /// El orden de las preguntas es el orden en que el usuario podría arreglarlas: primero el
    /// interruptor, después la clave, y al final la traba, que se destraba sola. Decir «falta la
    /// clave» sobre una instalación que además lo tiene apagado manda a buscar la clave para nada.
    /// </remarks>
    public static VoiceRouteDecision Choose(bool liveEnabled, bool hasGoogleKey, string? blockedReason = null)
    {
        if (!liveEnabled)
        {
            return new VoiceRouteDecision(VoiceRoute.Classic, DisabledReason);
        }

        if (!hasGoogleKey)
        {
            return new VoiceRouteDecision(VoiceRoute.Classic, MissingKeyReason);
        }

        return string.IsNullOrWhiteSpace(blockedReason)
            ? new VoiceRouteDecision(VoiceRoute.Live, LiveReason)
            : new VoiceRouteDecision(VoiceRoute.Classic, blockedReason);
    }
}
