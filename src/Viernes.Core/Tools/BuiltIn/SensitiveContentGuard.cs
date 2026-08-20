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
    public static void RejectCredentialLikeContent(string? value, string parameterName)
    {
        if (CredentialLikeText.Looks(value))
        {
            throw new ArgumentException(
                "No guardé el dato porque parece contener una credencial o un secreto.",
                parameterName);
        }
    }
}
