using Viernes.Memory.Privacy;

namespace Viernes.Core.Tools.BuiltIn;

/// <summary>
/// Evita que las herramientas que guardan cosas persistan una credencial.
/// </summary>
/// <remarks>
/// Es una defensa de último recurso, no un detector infalible de secretos.
/// <para>
/// <b>Tenía su propio reconocedor y por eso está esto acá.</b> El del tapado de charlas se ensanchó
/// —para cubrir «la clave es …», las claves de Stripe y de Amazon, y un token pegado en una
/// dirección— y éste se quedó con la red vieja: la misma decisión escrita dos veces terminó diciendo
/// cosas distintas sobre qué llega al disco del usuario. Ahora los dos preguntan a
/// <see cref="CredentialLikeText"/>.
/// </para>
/// </remarks>
internal static class SensitiveContentGuard
{
    public static void RejectCredentialLikeContent(string? value)
    {
        if (CredentialLikeText.Looks(value))
        {
            // Sin el nombre del parámetro: .NET le pega «(Parameter 'title')» al final del mensaje, y
            // eso viaja tal cual hasta el usuario. Le quedaba un renglón en inglés hablando de un
            // parámetro que no sabe qué es, colgado de una frase en castellano.
            //
            // Y diciendo qué hacer. «No lo guardé» sin más deja a alguien mirando la pantalla sin
            // saber si tiene que insistir, reformular, o si se rompió algo.
            throw new ArgumentException(
                "No lo guardé porque parece tener una clave adentro. Si no la tiene, escribilo sin " +
                "esa parte; si la tiene, mejor no la anotes acá.");
        }
    }
}
