namespace Viernes.Core.Tools;

/// <summary>Lo que efectivamente pasó al pedir una acción de PC.</summary>
/// <remarks>
/// <c>ImageDataUrl</c> viene sólo cuando la acción produce algo que hay que ver y no se puede
/// contar: la captura de pantalla. Es un fotograma de paso —se manda al modelo y se descarta— y
/// nunca se guarda en disco.
/// </remarks>
public sealed record PcActionOutcome(bool Executed, string Message, string? ImageDataUrl = null);

/// <summary>
/// Ejecuta acciones concretas del sistema. Vive fuera del núcleo a propósito: el núcleo decide
/// <em>si</em> una acción está permitida y el host decide <em>cómo</em> se hace en Windows.
/// </summary>
/// <remarks>
/// La política de riesgo corre antes que cualquier implementación de esta interfaz. Un ejecutor
/// nunca recibe una acción sensible o destructiva, ni una que no haya sido confirmada.
/// </remarks>
public interface IPcActionExecutor
{
    /// <summary>Acciones que este ejecutor sabe hacer de verdad, en minúsculas.</summary>
    IReadOnlySet<string> SupportedActions { get; }

    Task<PcActionOutcome> ExecuteAsync(
        string action,
        string? target,
        CancellationToken cancellationToken = default);
}
