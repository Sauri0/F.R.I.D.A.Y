namespace Viernes.Platform.Windows.Actions;

/// <summary>
/// Una acción ejecutada junto con la que la revierte, si existe.
/// </summary>
/// <remarks>
/// <c>Inverse</c> queda en <c>null</c> cuando la acción no tiene vuelta atrás —un clic ya dado, una
/// búsqueda ya abierta—. Se guarda igual: el diario sirve para contar qué pasó aunque no todo se
/// pueda deshacer.
/// </remarks>
internal sealed record JournalEntry(
    string Action,
    string? Target,
    string Description,
    (string Action, string? Target)? Inverse);

/// <summary>
/// Registro de lo que Viernes hizo, con lo necesario para volver atrás.
/// </summary>
/// <remarks>
/// Es la contrapartida de haberle sacado las confirmaciones. Sin permiso previo, la única defensa
/// que queda es que lo hecho se pueda desarmar: «deshacé lo último» tiene que funcionar, y para eso
/// hay que haber anotado el estado anterior en el momento, no reconstruirlo después.
/// <para>
/// Vive en memoria y muere con el proceso, a propósito. Un deshacer que sobrevive a un reinicio
/// promete revertir un escritorio que ya no existe: las ventanas se cerraron, los programas
/// cambiaron de estado, y aplicar el inverso de algo de ayer haría daño en vez de repararlo.
/// </para>
/// </remarks>
internal sealed class ActionJournal
{
    private const int Capacity = 50;

    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _entries = [];

    public void Record(JournalEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>Saca la última acción reversible. Las irreversibles quedan como historia.</summary>
    public JournalEntry? TakeLastReversible()
    {
        lock (_gate)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                if (_entries[index].Inverse is not null)
                {
                    var entry = _entries[index];
                    _entries.RemoveAt(index);
                    return entry;
                }
            }

            return null;
        }
    }

    /// <summary>Lo hecho recientemente, de lo más nuevo a lo más viejo, para poder contarlo.</summary>
    public IReadOnlyList<JournalEntry> Recent(int count)
    {
        lock (_gate)
        {
            return _entries
                .AsEnumerable()
                .Reverse()
                .Take(count)
                .ToArray();
        }
    }
}
