using Viernes.Core.Configuration;

namespace Viernes.App.Shell;

/// <summary>
/// Qué va a quedar si se acepta el nombre que está escrito, o por qué no se puede.
/// </summary>
/// <remarks>
/// Vive afuera de <see cref="AssistantNameDialog"/> y no adentro por una razón práctica: acá se
/// decide lo único que la ventana del nombre tiene de lógica —qué se muestra mientras se escribe y
/// cuándo se habilita Aceptar— y desde una prueba no se puede construir una ventana de WPF sin un
/// hilo STA y un despachador. Separado, se prueba con una llamada.
/// <para>
/// El motivo del rechazo lo redacta <see cref="AssistantIdentity.TryValidate"/> y se muestra tal
/// cual: está escrito en castellano justamente para eso, y volver a redactarlo acá sería mantener
/// dos versiones del mismo mensaje —el instalador ya muestra ésa—.
/// </para>
/// </remarks>
internal static class AssistantNamePreview
{
    /// <param name="raw">Lo que el usuario lleva escrito, tal cual.</param>
    /// <returns>Si sirve, y la frase que hay que mostrarle debajo del campo.</returns>
    internal static (bool Valid, string Message) Describe(string? raw)
    {
        if (!AssistantIdentity.TryValidate(raw, out var problem))
        {
            return (false, problem!);
        }

        // Se muestran las tres frases y no sólo una porque son las tres que van a andar, y quien
        // eligió el nombre tiene que poder probarlo sin adivinar la fórmula.
        var identity = new AssistantIdentity(raw);
        return (
            true,
            $"Va a quedar «{identity.Name}» · me llamás diciendo " +
            string.Join(", ", identity.WakePhrases.Select(phrase => $"«{phrase}»")) + ".");
    }
}
