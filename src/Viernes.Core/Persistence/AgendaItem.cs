namespace Viernes.Core.Persistence;

/// <summary>
/// Un evento de la agenda local.
/// </summary>
/// <remarks>
/// <c>NotifiedAt</c> se estampa cuando el host ya avisó del evento, igual que en
/// <see cref="Reminder"/>, para que un reinicio no vuelva a anunciar lo que ya sonó.
/// <para>
/// El campo no existía, y ésa era la razón de fondo por la que la agenda no avisaba nunca: sin
/// dónde anotar «esto ya se avisó», el vigía no tenía forma de anunciar un evento una sola vez, así
/// que directamente no miraba la agenda. Ponías un evento para el martes 15:30 y el martes a las
/// 15:30 no pasaba absolutamente nada.
/// </para>
/// </remarks>
public sealed record AgendaItem(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt,
    string? Notes = null,
    DateTimeOffset? NotifiedAt = null);
