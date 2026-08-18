namespace Viernes.Core.Mcp;

/// <summary>
/// Cuánto esperar antes del próximo intento de reconexión.
/// </summary>
/// <remarks>
/// Crece al doble y tiene techo. Reintentar cada un segundo contra un servidor que no va a volver
/// —un ejecutable que no existe, un token vencido— significa levantar un proceso por segundo para
/// siempre; esperar siempre cinco minutos significa que una caída de dos segundos se siente como
/// cinco minutos. La curva empieza rápido, donde están las caídas reales, y se resigna despacio.
/// <para>
/// Está aparte y sin estado a propósito: es la parte que se puede mirar y probar sin levantar nada.
/// </para>
/// </remarks>
public static class McpRetrySchedule
{
    /// <summary>Lo que se espera después del primer fallo.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>Techo: por más que siga fallando, nunca se espera más que esto.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Espera correspondiente a la cantidad de fallos seguidos acumulados.
    /// </summary>
    public static TimeSpan DelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return FirstDelay;
        }

        // El tope del exponente evita que la multiplicación se desborde antes de que la topemos.
        var exponent = Math.Min(consecutiveFailures - 1, 20);
        var scaled = FirstDelay * Math.Pow(2, exponent);
        return scaled >= MaximumDelay ? MaximumDelay : scaled;
    }
}
