using Viernes.Core.Autonomy;

namespace Viernes.Mcp;

/// <summary>
/// La frontera del conector: qué puede hacer Claude a través de Viernes, y qué no.
/// </summary>
/// <remarks>
/// Está escrita en código y no en el LEEME porque el próximo que agregue una herramienta va a
/// querer «completar la API», y la lista de abajo no son funciones que faltan: son decisiones.
/// <list type="number">
/// <item>
/// <description>
/// <b>No aprueba memoria.</b> Aprobar es del usuario. El conector propone y la propuesta queda
/// pendiente hasta que él diga que sí, en Viernes. Si el conector pudiera aprobar, cualquier cosa
/// que Claude dedujera en una charla se volvería un hecho sobre el usuario sin que él se entere.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No pasa por encima de <see cref="AutonomyPolicy"/>.</b> Toda acción que escribe algo consulta
/// primero, y si la política dice «preguntar» el conector <em>no la hace</em> y devuelve por qué.
/// Conectar un servidor no puede ser la forma de saltearse los permisos que el usuario configuró:
/// si lo fuera, la política valdría exactamente hasta que alguien agregue un conector.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No toca las claves.</b> Ninguna herramienta lee, devuelve ni nombra la clave de Google ni la
/// de OpenRouter. Por eso el conector nunca construye la configuración de Viernes —que las
/// resuelve del entorno—: lee misiones, memoria, permisos y gasto, y nada más.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No borra nada de forma irreversible.</b> Cerrar una misión la deja cerrada con su bitácora;
/// descartar un pendiente de memoria es del usuario. No hay una herramienta de olvidar.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class ConnectorBoundary
{
    private readonly AutonomyPolicy _policy;

    public ConnectorBoundary(AutonomyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>
    /// Devuelve <see langword="null"/> si la acción puede hacerse, o el motivo por el que no.
    /// </summary>
    /// <remarks>
    /// El que llama tiene que cortar con lo que devuelve esto <b>antes</b> de tocar nada. Está
    /// escrito así —negativo, y no un <c>bool</c>— para que un <c>if</c> olvidado se note leyendo:
    /// un permiso que se consulta y se ignora es peor que no consultarlo.
    /// </remarks>
    /// <param name="action">Qué se quiere hacer, en las palabras con las que el usuario escribiría la regla.</param>
    /// <param name="subject">Sobre qué o sobre quién.</param>
    /// <param name="cancellationToken">Para cortar la lectura del archivo de permisos.</param>
    public async Task<string?> WhyNotAsync(
        string action,
        string? subject,
        CancellationToken cancellationToken = default)
    {
        var level = await _policy.DecideAsync(action, subject, cancellationToken).ConfigureAwait(false);
        var about = string.IsNullOrWhiteSpace(subject) ? action : $"{action} · {subject}";

        return level switch
        {
            AutonomyLevel.Automatico => null,
            AutonomyLevel.Nunca =>
                $"No lo hice: dejaste dicho que esto no se hace nunca ({about}). Si cambiaste de " +
                "idea, decíselo a Viernes; el permiso lo cambiás vos, no yo.",
            _ =>
                $"Esto necesita que lo autorices vos ({about}). En los permisos de Viernes está " +
                "como «preguntar», y el conector no pregunta en tu nombre: no hice nada. Pedíselo " +
                "a Viernes directamente o cambiá el permiso desde la aplicación."
        };
    }
}
