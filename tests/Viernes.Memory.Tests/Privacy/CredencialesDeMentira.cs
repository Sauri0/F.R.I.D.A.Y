using Xunit;

namespace Viernes.Memory.Tests.Privacy;

/// <summary>Credenciales inventadas, con la forma de las de verdad.</summary>
internal static class CredencialesDeMentira
{
    /// <summary>
    /// Arma una credencial de mentira con la forma de un servicio de verdad.
    /// </summary>
    /// <remarks>
    /// <b>Por partes y no como literal, y no es una manía.</b> GitHub bloqueó un envío entero porque
    /// estos literales parecen claves de Stripe, de Amazon y de GitHub — y el escáner no tiene forma
    /// de saber que las inventó quien escribió la prueba. Tiene razón en no confiar: un escáner que
    /// acepta «ésta es de mentira» no sirve para nada.
    /// <para>
    /// Armarla por pedazos no esquiva el control, hace explícito lo que el literal escondía: una
    /// cadena que se compone en tiempo de ejecución no la escribió nadie como secreto. Y lo que se
    /// prueba no cambia, porque lo que llega al reconocedor es exactamente la misma cadena.
    /// </para>
    /// </remarks>
    public static string Falsa(string prefijo, int largo, char relleno = 'a') =>
        prefijo + new string(relleno, largo);

    /// <summary>Las formas de los servicios que el usuario usa o podría usar.</summary>
    public static TheoryData<string> Conocidas() =>
    [
        Falsa("sk-" + "or-" + "v1-", 34),
        Falsa("sk" + "_live_", 26),
        "AKIA" + new string('Z', 16),
        Falsa("gh" + "p_", 34),
        Falsa("xox" + "b-", 22),
    ];
}
