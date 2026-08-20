using System.Text.RegularExpressions;

namespace Viernes.Memory.Privacy;

/// <summary>
/// Qué texto parece una credencial. Uno solo para todo el proyecto.
/// </summary>
/// <remarks>
/// <b>Había dos, y eso ya había costado una vez.</b> El rechazo de memorias tenía el suyo y el
/// tapado de charlas tenía otro, casi iguales; al ensanchar uno —para cubrir «la clave es …», las
/// claves de Stripe y de Amazon, y un token pegado en una dirección— el otro se quedó con la red
/// vieja. Dos copias de la misma decisión terminan diciendo cosas distintas, y acá lo que dicen
/// distinto es qué se guarda en el disco del usuario.
/// <para>
/// Las dos cosas que se hacen con esto son distintas y por eso están las dos acá:
/// <list type="bullet">
///   <item><b>Rechazar</b> —una nota de memoria que contiene una clave no se guarda y el usuario se
///   entera— y</item>
///   <item><b>tapar</b> —un transcripto no se puede rechazar, es lo que se dijo, así que lo único
///   que queda es que la credencial no llegue a escribirse.</item>
/// </list>
/// </para>
/// <para>
/// <b>No pretende ser completo y no puede serlo.</b> Reconoce las formas conocidas; lo que no
/// reconozca pasa. Es una red, no una garantía, y por eso lo que se guarda sigue siendo local y
/// borrable a mano.
/// </para>
/// </remarks>
public static partial class CredentialLikeText
{
    /// <summary>Con qué se reemplaza lo que parece una credencial.</summary>
    public const string Placeholder = "«algo que parecía una credencial, no se guardó»";

    /// <summary>Si el texto tiene algo con forma de credencial.</summary>
    public static bool Looks(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Pattern().IsMatch(text);

    /// <summary>El texto con lo que parece una credencial tapado.</summary>
    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Pattern().Replace(text, Placeholder);

    /// <summary>
    /// Lo que parece una credencial.
    /// </summary>
    /// <remarks>
    /// <b>El <c>\b</c> que estaba al principio tapaba media red.</b> Anclaba TODA la alternancia a un
    /// borde de palabra, así que las formas que no empiezan con letra —un token en la consulta de una
    /// dirección— no podían coincidir nunca. Ahora el borde va adentro de cada rama que lo necesita.
    /// <para>
    /// Qué cubre cada cosa, y por qué: <c>clave</c> a secas, porque exigir «clave secreta» es
    /// pedirle a alguien que dicte una que hable como un manual; <c>sk_</c> con guión bajo, que es
    /// como las escribe Stripe; <c>AKIA…</c>, que es una de Amazon; <c>pwd</c> y <c>secret</c>, que
    /// aparecen en una cadena de conexión; y el token en la consulta de una dirección, que es como se
    /// filtra una credencial pegando un enlace.
    /// </para>
    /// <para>
    /// <b>Prefiere de más.</b> «La clave es entender el problema» va a coincidir. Al lado de una
    /// credencial en texto plano en el disco, no se compara.
    /// </para>
    /// <para>
    /// Lleva plazo: una expresión regular con alternancia y repetición sobre un texto largo —el
    /// cuerpo de una página, un transcripto entero— puede tardar mucho más de lo que uno espera, y
    /// esto corre en el camino de escribir una charla.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?:\bsk-or-v1-[a-z0-9_-]{8,}|\bsk[-_][a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{20,}|\bAKIA[0-9A-Z]{16}|\bgh[pousr]_[a-z0-9]{20,}|\bxox[baprs]-[a-z0-9-]{10,}|\bbearer\s+[a-z0-9._~+/=-]{8,}|[?&](?:access_|api_|auth_|id_)?token=[^\s&]+|\b(?:api[\s_-]*key|token|password|passwd|pwd|secret|contrase(?:ñ|n)a|clave(?:\s+secreta)?|pin)\s*(?::|=|\bes\b)\s*\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex Pattern();
}
