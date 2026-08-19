using Viernes.App.ViewModels;

namespace Viernes.App.Shell;

/// <summary>
/// La ficha de un desplegable: medida exacta, familia de vidrio, estado del orbe y si se cierra solo.
/// </summary>
/// <param name="Kind">Cuál de los diecinueve.</param>
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
/// Las diecinueve fichas. Trece salen medidas de la referencia ejecutable; seis se eligieron acá.
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
/// <para>
/// <b>Ninguna fila nueva puede pasar de 376 × 220</b>, que son el ancho y el alto máximos que ya
/// tenía la tabla —muestras y caja—. <see cref="MaxWidth"/> y <see cref="MaxHeight"/> son lo que
/// dimensiona la ventana: una fila más ancha o más alta agranda la ventana entera para siempre, y la
/// ventana es lo único que no se puede recalcular en caliente. El rango documentado de la referencia
/// —ancho 360–376, alto 132–220— ya garantiza eso; salirse de él es salirse del diseño.
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
        [PanelKind.SinRed] = new(PanelKind.SinRed, "sin red", 364, 140, AssistantVisualState.Offline, PanelFamily.Gris, 7000),

        // Los seis que la referencia dejó abiertos. El criterio es el de las trece de arriba, no uno
        // nuevo: una lista de filas pide el alto de memoria (200) y el ancho de la columna más larga;
        // algo que pide una decisión es ámbar, bajo y no se cierra solo —cerrarle un panel en la cara
        // a alguien que estaba por decidir es decidir por él—; algo que sólo informa vive 7 s.
        [PanelKind.Misiones] = new(PanelKind.Misiones, "misiones abiertas", 364, 200, AssistantVisualState.Idle, PanelFamily.Neutro, 7000),
        [PanelKind.Pregunta] = new(PanelKind.Pregunta, "la pregunta pendiente", 364, 148, AssistantVisualState.Attention, PanelFamily.Ambar, 0),

        // Proyectos va al ancho máximo —376, el de muestras— porque cada fila lleva tres datos que no
        // se pueden recortar sin perder el sentido: qué proyecto, en qué anda y desde cuándo.
        [PanelKind.Proyectos] = new(PanelKind.Proyectos, "proyectos", 376, 196, AssistantVisualState.Idle, PanelFamily.Neutro, 7000),

        // Autonomía y aprendido son neutros aunque tengan botones: el registro es «mostrarte lo que
        // hay», y la decisión es opcional. Ámbar es para lo que llega a preguntar algo; éstos los
        // abre el usuario. Eso sí, no se cierran solos: tienen controles adentro.
        [PanelKind.Autonomia] = new(PanelKind.Autonomia, "permisos aprendidos", 372, 206, AssistantVisualState.Idle, PanelFamily.Neutro, 0),
        [PanelKind.Aprendido] = new(PanelKind.Aprendido, "lo que aprendí", 364, 200, AssistantVisualState.Idle, PanelFamily.Neutro, 0),
        [PanelKind.Consumo] = new(PanelKind.Consumo, "gasto", 364, 166, AssistantVisualState.Idle, PanelFamily.Neutro, 7000)
    };

    /// <summary>Ancho del vidrio más ancho de los diecinueve. La ventana reserva este espacio siempre.</summary>
    public static double MaxWidth { get; } = Table.Values.Max(spec => spec.Width);

    /// <summary>Alto del vidrio más alto de los diecinueve. Es el alto útil de la ventana.</summary>
    public static double MaxHeight { get; } = Table.Values.Max(spec => spec.Height);

    /// <summary>La ficha de un desplegable.</summary>
    public static PanelSpec For(PanelKind kind) => Table[kind];

    /// <summary>Las diecinueve fichas, para diagnósticos y pruebas.</summary>
    public static IEnumerable<PanelSpec> All => Table.Values;
}
