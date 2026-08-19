using Viernes.App.ViewModels;

namespace Viernes.App.Shell;

/// <summary>
/// La ficha de un desplegable: medida exacta, familia de vidrio, estado del orbe y si se cierra solo.
/// </summary>
/// <param name="Kind">Cuál de los trece.</param>
/// <param name="Name">Cómo se lo nombra en voz alta y en los diagnósticos.</param>
/// <param name="Width">Ancho del vidrio, en unidades independientes del DPI.</param>
/// <param name="Height">Alto del vidrio. La ventana no cambia: cambia esto.</param>
/// <param name="OrbState">En qué estado deja al orbe mientras está abierto.</param>
/// <param name="Family">Familia de vidrio.</param>
/// <param name="LifeMs">
/// Cuántos milisegundos vive antes de retraerse solo. Cero significa que espera una decisión y no se
/// va por su cuenta.
/// </param>
internal sealed record PanelSpec(
    PanelKind Kind,
    string Name,
    double Width,
    double Height,
    AssistantVisualState OrbState,
    PanelFamily Family,
    int LifeMs)
{
    /// <summary>Si se retrae solo pasado <see cref="LifeMs"/>.</summary>
    public bool ClosesItself => LifeMs > 0;
}

/// <summary>
/// Las trece fichas, con los números medidos sobre la referencia ejecutable.
/// </summary>
/// <remarks>
/// Los anchos y altos no son gusto: cada panel se midió con su contenido real hasta que dejó de
/// sobrar y de faltar. Por eso van acá, en una sola tabla, y no repartidos por el XAML — que es
/// donde se desincronizan.
/// <para>
/// El alto máximo de la tabla es el alto de la ventana. Esa es la decisión de arquitectura del
/// rework: la ventana mide siempre lo mismo y es transparente; lo que se anima es el vidrio adentro.
/// Cambiar <see cref="System.Windows.Window.Height"/> por cuadro es lo que hacía saltar al orbe.
/// </para>
/// </remarks>
internal static class PanelCatalog
{
    private static readonly IReadOnlyDictionary<PanelKind, PanelSpec> Table = new Dictionary<PanelKind, PanelSpec>
    {
        [PanelKind.Escribir] = new(PanelKind.Escribir, "escribir", 372, 206, AssistantVisualState.Listening, PanelFamily.Neutro, 0),
        [PanelKind.Trabajando] = new(PanelKind.Trabajando, "trabajando", 364, 200, AssistantVisualState.Thinking, PanelFamily.Neutro, 0),
        [PanelKind.Calendario] = new(PanelKind.Calendario, "calendario", 364, 188, AssistantVisualState.Idle, PanelFamily.Neutro, 7000),
        [PanelKind.Muestras] = new(PanelKind.Muestras, "muestras", 376, 202, AssistantVisualState.Idle, PanelFamily.Neutro, 7000),
        [PanelKind.Caja] = new(PanelKind.Caja, "caja", 364, 220, AssistantVisualState.Attention, PanelFamily.Neutro, 0),
        [PanelKind.Gastos] = new(PanelKind.Gastos, "gastos", 364, 166, AssistantVisualState.Speaking, PanelFamily.Neutro, 7000),
        [PanelKind.Musica] = new(PanelKind.Musica, "música", 376, 196, AssistantVisualState.Idle, PanelFamily.Neutro, 0),
        [PanelKind.Recordatorio] = new(PanelKind.Recordatorio, "recordatorio", 364, 136, AssistantVisualState.Attention, PanelFamily.Ambar, 0),
        [PanelKind.Permiso] = new(PanelKind.Permiso, "permiso", 364, 148, AssistantVisualState.Attention, PanelFamily.Ambar, 0),
        [PanelKind.Politica] = new(PanelKind.Politica, "política", 364, 136, AssistantVisualState.Error, PanelFamily.Rojo, 7000),
        [PanelKind.Memoria] = new(PanelKind.Memoria, "memoria", 364, 200, AssistantVisualState.Idle, PanelFamily.Neutro, 7000),
        [PanelKind.Presupuesto] = new(PanelKind.Presupuesto, "presupuesto", 364, 144, AssistantVisualState.Attention, PanelFamily.Ambar, 7000),
        [PanelKind.SinRed] = new(PanelKind.SinRed, "sin red", 364, 140, AssistantVisualState.Offline, PanelFamily.Gris, 7000)
    };

    /// <summary>Ancho del vidrio más ancho de los trece. La ventana reserva este espacio siempre.</summary>
    public static double MaxWidth { get; } = Table.Values.Max(spec => spec.Width);

    /// <summary>Alto del vidrio más alto de los trece. Es el alto útil de la ventana.</summary>
    public static double MaxHeight { get; } = Table.Values.Max(spec => spec.Height);

    /// <summary>La ficha de un desplegable.</summary>
    public static PanelSpec For(PanelKind kind) => Table[kind];

    /// <summary>Las trece fichas, para diagnósticos y pruebas.</summary>
    public static IEnumerable<PanelSpec> All => Table.Values;
}
