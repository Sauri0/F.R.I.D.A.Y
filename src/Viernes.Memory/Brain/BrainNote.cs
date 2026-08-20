namespace Viernes.Memory.Brain;

/// <summary>
/// Qué clase de cosa sabe.
/// </summary>
/// <remarks>
/// Los tipos no son una taxonomía inventada: separan cosas que envejecen distinto y que hay que
/// poder corregir por separado. Que a alguien le guste el café sin azúcar y que el botón de guardar
/// de una aplicación esté en el menú de archivo son las dos «cosas que sabe», y confundirlas hace
/// que actualizar la aplicación borre la preferencia.
/// </remarks>
public enum BrainNoteKind
{
    /// <summary>Cómo es y cómo le gusta trabajar a la persona.</summary>
    Preferencia,

    /// <summary>Cómo funciona un programa: dónde está cada cosa, qué pasa al tocarla.</summary>
    Aplicacion,

    /// <summary>Cómo se hace algo, de punta a punta.</summary>
    Procedimiento,

    /// <summary>Algo que creía y le dijeron que no era así.</summary>
    Correccion,

    /// <summary>Qué puede y qué no puede hacer, según lo que le pasó de verdad.</summary>
    Capacidades
}

/// <summary>
/// Cuánto se apoya en algo para actuar.
/// </summary>
/// <remarks>
/// En palabras y no en un número, a propósito. Un <c>0,73</c> de confianza es un número inventado
/// que después se lee como si lo hubiera medido alguien; lo que hace falta acá es elegir cómo
/// comportarse, y para eso tres escalones alcanzan y no mienten.
/// </remarks>
public enum BrainConfidence
{
    /// <summary>Lo vio una vez. Preguntar antes de usarlo.</summary>
    Baja,

    /// <summary>Le funcionó, sin confirmación. Usarlo y verificar.</summary>
    Media,

    /// <summary>Se lo dijeron, o le salió bien varias veces sin que lo corrigieran.</summary>
    Alta
}

/// <summary>Si la nota sigue valiendo.</summary>
/// <remarks>
/// Lo reemplazado <b>no se borra</b>. La evidencia de por qué creía algo es lo que permite entender
/// después por qué se equivocó, y borrarla deja una corrección sin causa.
/// </remarks>
public enum BrainStatus
{
    /// <summary>Vale.</summary>
    Vigente,

    /// <summary>La reemplazó otra. Queda por su evidencia.</summary>
    Reemplazada
}

/// <summary>
/// Una cosa que sabe, tal como queda escrita en un archivo de texto.
/// </summary>
/// <remarks>
/// Un archivo por cosa, y no un archivo con todo adentro, por dos motivos que tiran para el mismo
/// lado: se puede corregir una sin tocar las demás —incluso a mano, con cualquier editor— y se puede
/// cargar sólo lo que hace falta en vez de meterle todo el cerebro al modelo en cada turno.
/// <para>
/// El nombre del archivo es el identificador. Es feo comparado con un número, y es lo correcto:
/// <c>toma-el-cafe-sin-azucar.md</c> dice qué hay adentro desde el explorador de archivos, y el
/// usuario tiene que poder mirar esta carpeta y entenderla sin abrir nada.
/// </para>
/// </remarks>
/// <param name="Name">El nombre del archivo, sin la extensión. Identifica la nota.</param>
/// <param name="Kind">Qué clase de cosa es.</param>
/// <param name="Title">Una línea, en castellano, que se lee en el índice.</param>
/// <param name="Scope">Cuándo vale: «siempre», «en Spotify», «cuando trabaja».</param>
/// <param name="Confidence">Cuánto se apoya en esto.</param>
/// <param name="Status">Si sigue valiendo.</param>
/// <param name="Evidence">De qué charlas salió, como rutas relativas al cerebro.</param>
/// <param name="Supersedes">A qué nota reemplaza, si reemplaza a alguna.</param>
/// <param name="When">Cuándo se aprendió.</param>
/// <param name="Folder">En qué carpeta vive, relativa a «saber». Vacío es la raíz.</param>
/// <param name="Body">El cuerpo: lo que sabe, en prosa.</param>
public sealed record BrainNote(
    string Name,
    BrainNoteKind Kind,
    string Title,
    string Scope,
    BrainConfidence Confidence,
    BrainStatus Status,
    IReadOnlyList<string> Evidence,
    string? Supersedes,
    DateTimeOffset When,
    string Folder,
    string Body)
{
    /// <summary>
    /// La versión del formato, en cada archivo.
    /// </summary>
    /// <remarks>
    /// Va escrita adentro y no supuesta. Un cerebro que se lee con un programa más nuevo del que lo
    /// escribió tiene que poder darse cuenta, y uno más viejo tiene que poder negarse en vez de
    /// adivinar — que es como se pierde un cerebro entero por un campo que cambió de significado.
    /// </remarks>
    public const int Schema = 1;

    /// <summary>Dónde vive el archivo, relativo a la carpeta del cerebro.</summary>
    public string RelativePath => string.IsNullOrEmpty(Folder)
        ? System.IO.Path.Combine("saber", Name + ".md").Replace('\\', '/')
        : System.IO.Path.Combine("saber", Folder, Name + ".md").Replace('\\', '/');
}
