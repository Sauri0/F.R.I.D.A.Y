namespace Viernes.Core.Missions;

/// <summary>En qué anda una misión.</summary>
public enum MissionState
{
    /// <summary>Anotada, todavía sin empezar.</summary>
    Abierta,

    /// <summary>Trabajando en eso.</summary>
    EnCurso,

    /// <summary>Frenada hasta que el usuario conteste algo.</summary>
    Esperando,

    /// <summary>Cumplida.</summary>
    Terminada,

    /// <summary>Abandonada a pedido del usuario.</summary>
    Cancelada
}

/// <summary>Una anotación en la bitácora de una misión.</summary>
/// <param name="At">Cuándo pasó.</param>
/// <param name="Text">Qué pasó, en una línea.</param>
public sealed record MissionStep(DateTimeOffset At, string Text);

/// <summary>
/// Un encargo que dura más que una conversación.
/// </summary>
/// <remarks>
/// La diferencia con un recordatorio es que un recordatorio sólo sabe cuándo sonar; una misión sabe
/// <em>en qué estado está</em>. Es lo que permite que el asistente retome algo tres días después
/// sabiendo qué hizo, qué le falta y qué le preguntó al usuario sin que le contesten todavía.
/// </remarks>
public sealed class Mission
{
    /// <summary>Identificador corto, para poder nombrarla en voz alta: <c>m1</c>, <c>m2</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Cómo se la nombra en una línea.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Qué cuenta como cumplida. Sin esto no se puede saber cuándo cerrarla.</summary>
    public string Goal { get; set; } = string.Empty;

    public MissionState State { get; set; } = MissionState.Abierta;

    /// <summary>Lo que se hizo, en orden. Es lo que se le cuenta al usuario cuando pregunta.</summary>
    public List<MissionStep> Log { get; set; } = [];

    /// <summary>
    /// Lo que se le preguntó al usuario y todavía no contestó.
    /// </summary>
    /// <remarks>
    /// Que la pregunta viva en la misión y no en la conversación es lo que hace que sobreviva a
    /// cerrar la charla, apagar la máquina y volver mañana. Una pregunta que se olvida al reiniciar
    /// es una misión que queda trabada para siempre sin que nadie se entere.
    /// </remarks>
    public string? Question { get; set; }

    /// <summary>Cuándo se preguntó, para no repetir la pregunta cada cinco minutos.</summary>
    public DateTimeOffset? AskedAt { get; set; }

    /// <summary>A qué se refiere: una carpeta de proyecto, una cuenta de mail, lo que sea.</summary>
    public string? Context { get; set; }

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastProgress { get; set; } = DateTimeOffset.Now;

    /// <summary>Cuándo conviene volver a mirarla. <c>null</c> es «cuando haya novedad».</summary>
    public DateTimeOffset? NextReview { get; set; }

    /// <summary>Si sigue viva, o sea si todavía puede pasar algo con ella.</summary>
    public bool IsOpen =>
        this.State is MissionState.Abierta or MissionState.EnCurso or MissionState.Esperando;
}
