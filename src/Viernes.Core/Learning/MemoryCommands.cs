namespace Viernes.Core.Learning;

/// <summary>
/// Los comandos tipeados de lo pendiente: <c>/pendientes</c>, <c>/aprobar</c> y <c>/rechazar</c>.
/// </summary>
/// <remarks>
/// Existe aparte del enrutador de comandos de la aplicación porque acá se puede probar y allá no
/// —el proyecto de pruebas no referencia la interfaz—, y porque lo que decide qué es una aprobación
/// es la misma regla que usa la herramienta hablada. Enchufarlo es una línea: si esto devuelve algo,
/// ése es el resultado; si devuelve <see langword="null"/>, el texto no era para acá.
/// <para>
/// El camino tipeado no es un lujo: es el que sigue andando cuando no hay nube, igual que
/// <c>/agenda</c> o <c>/recordatorios</c>.
/// </para>
/// </remarks>
public sealed class MemoryCommands
{
    private readonly MemoryApprovals _approvals;

    public MemoryCommands(MemoryApprovals approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        _approvals = approvals;
    }

    /// <summary>Una línea de ayuda para sumar a la que ya muestra el modo local.</summary>
    public static string Help => "/pendientes, /aprobar ID o TEXTO, /rechazar ID o TEXTO";

    /// <summary>
    /// Ejecuta el comando si el texto es uno de los suyos; si no, devuelve <see langword="null"/>.
    /// </summary>
    public async Task<string?> TryExecuteAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = input.Trim();
        var normalized = text.ToLowerInvariant();

        if (normalized is "/pendientes" or "/sugerencias" or "qué creés que aprendiste" or
            "que crees que aprendiste")
        {
            return await DescribePendingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (TryTake(text, normalized, "/aprobar", out var approve))
        {
            var outcome = await _approvals.ApproveAsync(approve, cancellationToken).ConfigureAwait(false);
            return outcome.Message;
        }

        if (TryTake(text, normalized, "/rechazar", out var reject))
        {
            var outcome = await _approvals.RejectAsync(reject, cancellationToken).ConfigureAwait(false);
            return outcome.Message;
        }

        return null;
    }

    private async Task<string> DescribePendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _approvals.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return "No tengo nada pendiente de confirmar.";
        }

        var lines = pending
            .Take(10)
            .Select(item => $"[{item.ShortId}] {item.Content}");
        return $"Pendiente de que confirmes · /aprobar ID o /rechazar ID\n{string.Join('\n', lines)}";
    }

    /// <summary>Acepta tanto «/aprobar» solo como «/aprobar loquesea».</summary>
    private static bool TryTake(string text, string normalized, string command, out string? argument)
    {
        if (string.Equals(normalized, command, StringComparison.Ordinal))
        {
            argument = null;
            return true;
        }

        if (normalized.StartsWith(command + ' ', StringComparison.Ordinal))
        {
            argument = text[(command.Length + 1)..].Trim();
            return true;
        }

        argument = null;
        return false;
    }
}
