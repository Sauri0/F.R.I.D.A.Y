using System.Text.RegularExpressions;

namespace Viernes.Memory.Privacy;

/// <summary>
/// Qué texto parece una credencial. Uno solo para todo el proyecto.
/// </summary>
/// <remarks>
/// <b>Había dos, y eso ya costó una vez.</b> El rechazo de memorias tenía el suyo y el tapado de
/// charlas tenía otro, casi iguales; al ensanchar uno el otro se quedó con la red vieja. Dos copias
/// de la misma decisión terminan diciendo cosas distintas, y acá lo que dicen distinto es qué se
/// guarda en el disco del usuario.
/// <para>
/// <b>Pero unificarlas del todo también estuvo mal, y eso costó otra vez.</b> Las dos cosas que se
/// hacen con esto no quieren el mismo umbral:
/// <list type="bullet">
///   <item><b>Tapar</b> un transcripto: equivocarse de más cuesta un renglón menos legible en una
///   charla guardada. Barato. Conviene pasarse.</item>
///   <item><b>Rechazar</b> una memoria o un recordatorio: equivocarse de más significa que la
///   asistente <em>se niega a guardar lo que le pediste</em>. Caro, y encima desconcertante. Al
///   ensanchar la red sin mirar esto, «recordame que la clave del examen es la constancia» dejó de
///   poder guardarse.</item>
/// </list>
/// Por eso hay dos umbrales sobre la <em>misma</em> idea, y una prueba que verifica que el estricto
/// sea siempre un subconjunto del ancho: lo que se rechaza siempre se tapa, nunca al revés.
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

    /// <summary>
    /// Si el texto tiene algo con forma de credencial <b>como para negarse a guardarlo</b>.
    /// </summary>
    /// <remarks>
    /// El umbral estricto. Las formas propias de un servicio —una clave de Google, de Stripe, de
    /// GitHub— se reconocen solas y alcanzan. Lo dictado en castellano necesita además que el valor
    /// tenga pinta de secreto: seis caracteres y por lo menos un dígito. «La clave es la constancia»
    /// se guarda; «la clave es Casa12345» no.
    /// <para>
    /// Un dígito no es un detector de secretos y no pretende serlo: es lo que separa una frase en
    /// castellano de algo que alguien escribió para que no se adivine. Una contraseña de puras
    /// letras dictada en un recordatorio se va a guardar, y eso está asumido — se guarda local, y el
    /// tapado ancho igual la cubre en las charlas, que es donde de verdad se dicta una clave.
    /// </para>
    /// </remarks>
    public static bool Looks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return Estricto().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Se cae para el lado seguro: si no se pudo mirar, se supone que sí. Lo que se pierde es
            // que una nota no se guarde; lo que se ganaría con la otra decisión es guardar una clave.
            return true;
        }
    }

    /// <summary>
    /// El texto con lo que parece una credencial tapado, <b>prefiriendo pasarse</b>.
    /// </summary>
    /// <remarks>
    /// El umbral ancho. Acá equivocarse de más cuesta un renglón menos legible en una charla; no
    /// equivocarse de menos cuesta una credencial en texto plano en el disco.
    /// </remarks>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return Ancho().Replace(text, Placeholder);
        }
        catch (RegexMatchTimeoutException)
        {
            // Acá también para el lado seguro, y duele más: se tapa el renglón entero. Es la única
            // salida honesta — no se pudo mirar, así que no se puede decir que no había nada.
            //
            // Sin esto la excepción salía por el camino de escribir una charla, donde el único catch
            // espera errores de disco: se llevaba puesto el turno del usuario sin decir por qué.
            return Placeholder;
        }
    }

    /// <summary>
    /// Todo lo que parece una credencial. Es el que tapa.
    /// </summary>
    /// <remarks>
    /// <b>El <c>\b</c> que estaba al principio tapaba media red.</b> Anclaba TODA la alternancia a un
    /// borde de palabra, así que las formas que no empiezan con letra —un token en la consulta de una
    /// dirección— no podían coincidir nunca. El borde va adentro de cada rama que lo necesita.
    /// <para>
    /// Qué cubre cada cosa: <c>clave</c> a secas, porque exigir «clave secreta» es pedirle a alguien
    /// que dicte una que hable como un manual; el complemento del medio —«la clave <b>del wifi</b>
    /// es…»—, que es como se dice de verdad y antes no coincidía; <c>sk_</c> con guión bajo, que es
    /// como las escribe Stripe; <c>AKIA…</c>, de Amazon; <c>pwd</c> y <c>secret</c>, que aparecen en
    /// una cadena de conexión; y el token en la consulta de una dirección, que es como se filtra una
    /// credencial pegando un enlace.
    /// </para>
    /// <para>
    /// Lleva plazo: una alternancia con repetición sobre un texto largo —el cuerpo de una página, un
    /// transcripto entero— puede tardar mucho más de lo que uno espera, y esto corre en el camino de
    /// escribir una charla. Quien llama tiene que atrapar el plazo; ver <see cref="Looks"/> y
    /// <see cref="Redact"/> en sus llamadores.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?:\bsk-or-v1-[a-z0-9_-]{8,}|\bsk[-_][a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{20,}|\bAKIA[0-9A-Z]{16}|\bgh[pousr]_[a-z0-9]{20,}|\bxox[baprs]-[a-z0-9-]{10,}|\bbearer\s+[a-z0-9._~+/=-]{8,}|[?&](?:access_|api_|auth_|id_)?token=[^\s&]+|\b(?:api[\s_-]*key|token|password|passwd|pwd|secret|contrase(?:ñ|n)a|clave(?:\s+secreta)?|pin)(?:\s+(?:de|del|para)(?:\s+(?:el|la|los|las))?\s+\S{1,20}){0,2}\s*(?::|=|\bes\b)\s*\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex Ancho();

    /// <summary>
    /// Lo que alcanza para negarse a guardar algo. Es un subconjunto del de arriba.
    /// </summary>
    /// <remarks>
    /// Mismas ramas de formato —ésas son inequívocas— y la rama dictada partida en dos, porque los
    /// dos puntos y el igual no son lo mismo que la palabra «es»:
    /// <list type="bullet">
    ///   <item><c>clave: X</c> y <c>Pwd=X</c> son <b>sintaxis</b>. Nadie escribe eso hablando de
    ///   otra cosa, así que alcanza con que el valor tenga seis caracteres.</item>
    ///   <item><c>la clave es X</c> es <b>prosa castellana</b>, y ahí «la clave es la constancia» es
    ///   una frase de todos los días. Se le exige además un dígito: es lo que separa una frase de
    ///   algo que alguien escribió para que no se adivine.</item>
    /// </list>
    /// Una contraseña de puras letras dictada así se va a guardar, y eso está asumido: se guarda
    /// local, y el tapado ancho igual la cubre en las charlas, que es donde de verdad se dicta una.
    /// <para>
    /// Hay una prueba que verifica que todo lo que esto reconoce, el ancho también.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?:\bsk-or-v1-[a-z0-9_-]{8,}|\bsk[-_][a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{20,}|\bAKIA[0-9A-Z]{16}|\bgh[pousr]_[a-z0-9]{20,}|\bxox[baprs]-[a-z0-9-]{10,}|\bbearer\s+[a-z0-9._~+/=-]{8,}|[?&](?:access_|api_|auth_|id_)?token=[^\s&]+|\b(?:api[\s_-]*key|token|password|passwd|pwd|secret|contrase(?:ñ|n)a|clave(?:\s+secreta)?|pin)(?:\s+(?:de|del|para)(?:\s+(?:el|la|los|las))?\s+\S{1,20}){0,2}\s*(?::|=)\s*\S{6,}|\b(?:api[\s_-]*key|token|password|passwd|pwd|secret|contrase(?:ñ|n)a|clave(?:\s+secreta)?|pin)(?:\s+(?:de|del|para)(?:\s+(?:el|la|los|las))?\s+\S{1,20}){0,2}\s+es\s+(?=\S*[0-9])\S{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex Estricto();
}
