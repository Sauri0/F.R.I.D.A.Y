namespace Viernes.App.Shell;

/// <summary>
/// Los trece desplegables. Uno por función, y ninguno de propósito general.
/// </summary>
/// <remarks>
/// La lista no es una taxonomía: es la enumeración de las trece cosas que Viernes tiene para
/// mostrar. Cada una trae su medida, su familia de vidrio, en qué estado deja al orbe y si se cierra
/// sola — todo eso vive en <see cref="PanelCatalog"/>. Si aparece una función nueva, se agrega acá y
/// se le da su fila en el catálogo; no hay panel genérico al que caerse.
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
    SinRed
}
