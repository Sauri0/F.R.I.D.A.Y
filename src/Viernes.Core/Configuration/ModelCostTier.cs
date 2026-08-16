namespace Viernes.Core.Configuration;

/// <summary>
/// Banda de precio/capacidad que el Auto Router de OpenRouter usa para elegir candidatos.
/// Reemplaza a fijar un slug: el rol expresa la intención y el router resuelve el modelo del día.
/// </summary>
public enum ModelCostTier
{
    /// <summary>Prefiere los modelos capaces más baratos.</summary>
    Low,

    /// <summary>Equilibrio entre costo y capacidad.</summary>
    Medium,

    High,

    XHigh,

    /// <summary>Prefiere los modelos más capaces sin mirar el precio.</summary>
    Max
}
