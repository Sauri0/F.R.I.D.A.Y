namespace Viernes.Core.Conversation;

public enum TurnStepStatus
{
    /// <summary>Todavía no empezó.</summary>
    Pending,

    /// <summary>Está ocurriendo ahora.</summary>
    Running,

    /// <summary>Terminó bien.</summary>
    Done,

    /// <summary>La política local lo detuvo o quedó esperando confirmación.</summary>
    Blocked
}

/// <summary>Un paso del turno, en el idioma del usuario y sin exponer argumentos de herramientas.</summary>
public sealed record TurnStep(string Label, TurnStepStatus Status);

public sealed class TurnProgressEventArgs(IReadOnlyList<TurnStep> steps) : EventArgs
{
    public IReadOnlyList<TurnStep> Steps { get; } = steps ?? throw new ArgumentNullException(nameof(steps));
}

/// <summary>
/// Traduce nombres técnicos de herramienta a lo que el usuario reconoce. Deliberadamente no incluye
/// argumentos: el objetivo es que se vea <em>qué</em> está haciendo, no repetir el contenido.
/// </summary>
public static class TurnStepLabels
{
    public static string ForTool(string toolName) => toolName switch
    {
        "reminder_create" => "Guardando el recordatorio",
        "reminder_list" => "Leyendo tus recordatorios",
        "agenda_create" => "Escribiendo en tu agenda",
        "agenda_list" => "Leyendo tu agenda",
        "web_search" => "Preparando la búsqueda",
        "pc_action" => "Revisando la acción de PC",
        _ => "Usando una herramienta"
    };
}
