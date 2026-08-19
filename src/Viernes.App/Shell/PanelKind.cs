namespace Viernes.App.Shell;

/// <summary>
/// Los diecinueve desplegables. Uno por función, y ninguno de propósito general.
/// </summary>
/// <remarks>
/// La lista no es una taxonomía: es la enumeración de las diecinueve cosas que Viernes tiene para
/// mostrar. Cada una trae su medida, su familia de vidrio, en qué estado deja al orbe y si se cierra
/// sola — todo eso vive en <see cref="PanelCatalog"/>. Si aparece una función nueva, se agrega acá y
/// se le da su fila en el catálogo; no hay panel genérico al que caerse.
/// <para>
/// Los seis últimos son los que la referencia ejecutable dejaba abiertos: sus medidas <b>no están</b>
/// en <c>FASES</c> y se eligieron acá, adentro del rango que sí está documentado (ancho 360–376,
/// alto 132–220). Van al final y no intercalados a propósito: reordenar esta lista para que quede
/// prolija cambia el valor numérico de un <c>enum</c> que aparece en los diagnósticos.
/// </para>
/// </remarks>
internal enum PanelKind
{
    /// <summary>La única entrada de texto del sistema. Se abre al tocar el orbe.</summary>
    Escribir,

    /// <summary>Los pasos del turno: hace visible que una respuesta convincente no ejecutó nada sola.</summary>
    Trabajando,

    /// <summary>Un vistazo al día.</summary>
    Calendario,

    /// <summary>El estado de lo que entró y todavía no salió.</summary>
    Muestras,

    /// <summary>El cierre del día: qué entró, por dónde y cuánto falta.</summary>
    Caja,

    /// <summary>Confirmar qué se anotó, contra qué caja y con cuánto saldo queda.</summary>
    Gastos,

    /// <summary>Qué está sonando y en qué aplicación.</summary>
    Musica,

    /// <summary>El único desplegable que el usuario no pidió: llega solo y habla.</summary>
    Recordatorio,

    /// <summary>La compuerta. El modelo propone; acá decide el código local.</summary>
    Permiso,

    /// <summary>Una regla local dijo que no. No es un error: es el sistema cumpliendo lo prometido.</summary>
    Politica,

    /// <summary>Memoria revisable y borrable. Sin esto, «memoria personal» es vigilancia.</summary>
    Memoria,

    /// <summary>Frenar el gasto del modelo sin dejar al usuario encerrado.</summary>
    Presupuesto,

    /// <summary>Decir qué se perdió y, sobre todo, qué no.</summary>
    SinRed,

    /// <summary>Los encargos que duran hasta cumplirse: en qué anda cada uno y desde cuándo.</summary>
    Misiones,

    /// <summary>
    /// La pregunta que sobrevive al reinicio, y el lugar donde se contesta.
    /// </summary>
    /// <remarks>
    /// Es el único de los seis nuevos que además <em>recibe</em>: una pregunta que sólo se lee es una
    /// notificación. Por eso es ámbar y por eso no se cierra sola.
    /// </remarks>
    Pregunta,

    /// <summary>Lo que ve el vigía de sesiones de Claude Code. Sólo mira: nunca le escribe.</summary>
    Proyectos,

    /// <summary>
    /// Los permisos que el usuario fue dando, por acción y por sujeto.
    /// </summary>
    /// <remarks>
    /// No se llama <c>Permisos</c> porque a un carácter de distancia de <see cref="Permiso"/> —que es
    /// otra cosa: la compuerta de una acción puntual— empieza a doler: el parámetro del convertidor
    /// en el XAML es una cadena, y una <em>s</em> de más o de menos no la ve el compilador. El panel
    /// dejaría de dibujarse sin que nada avise.
    /// </remarks>
    Autonomia,

    /// <summary>La memoria en revisión: lo que espera aprobación y lo que ya es un hecho.</summary>
    Aprendido,

    /// <summary>
    /// Cuánto lleva gastado el modelo contra el presupuesto configurado.
    /// </summary>
    /// <remarks>
    /// No se llama <c>Gasto</c> por la misma razón que <see cref="Autonomia"/> no se llama
    /// <c>Permisos</c>: <see cref="Gastos"/> ya existe y es otra cosa —el libro de gastos del
    /// usuario—. Éste cuenta dólares de tokens.
    /// </remarks>
    Consumo
}
