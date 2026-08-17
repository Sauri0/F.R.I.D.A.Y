namespace Viernes.Core.Learning;

/// <summary>
/// Memoria de <em>cómo</em> se hacen las cosas, distinta de la memoria de hechos sobre el usuario.
/// </summary>
/// <remarks>
/// La memoria personal guarda «le gusta el mate»; ésta guarda «pedirle una canción por nombre se
/// resuelve con play_music, y funcionó». Son dos cosas distintas y mezclarlas arruina las dos: los
/// hechos del usuario se revisan y aprueban a mano, mientras que un método que ya se comprobó que
/// anda no necesita permiso para volver a usarse.
/// </remarks>
public interface IActionMemory
{
    /// <summary>
    /// Lo aprendido que viene al caso para este pedido, listo para inyectar como contexto, o
    /// <c>null</c> si todavía no aprendió nada parecido.
    /// </summary>
    Task<string?> RecallAsync(string request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Anota cómo terminó un intento. Los fracasos valen tanto como los aciertos: saber que
    /// «wsp» no resuelve a nada evita repetir el mismo error la próxima vez.
    /// </summary>
    Task RecordAsync(
        string request,
        string action,
        string? target,
        bool succeeded,
        string outcome,
        CancellationToken cancellationToken = default);
}
