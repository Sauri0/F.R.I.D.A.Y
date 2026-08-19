using System.Globalization;
using System.Text;

namespace Viernes.Core.Configuration;

/// <summary>
/// Cómo se llama el asistente y, en consecuencia, cómo se lo despierta.
/// </summary>
/// <remarks>
/// El nombre lo elige quien instala. Antes estaba escrito a mano en unos cuarenta lugares —el prompt
/// del sistema, las frases de activación, el título de la ventana, la bandeja, las notificaciones—
/// y cambiarlo significaba encontrarlos todos y no olvidarse de ninguno. Acá hay uno solo.
/// <para>
/// El nombre hablado no es lo mismo que la carpeta de datos: <c>%LOCALAPPDATA%\Viernes</c> queda fija
/// porque identifica al producto, no al asistente. Si siguiera al nombre, renombrarlo abandonaría el
/// historial, las preferencias y los 465 MB del modelo de voz ya descargado.
/// </para>
/// </remarks>
public sealed record AssistantIdentity
{
    /// <summary>Nombre con el que nació el proyecto; es el que se usa si nadie elige otro.</summary>
    public const string DefaultName = "Viernes";

    private const int MinimumLength = 2;
    private const int MaximumLength = 24;

    /// <summary>Identidad por defecto, para código que todavía no recibió la del usuario.</summary>
    public static AssistantIdentity Default { get; } = new(DefaultName);

    /// <summary>Construye una identidad a partir de un nombre crudo, normalizándolo.</summary>
    public AssistantIdentity(string? name)
    {
        this.Name = Normalize(name);
    }

    /// <summary>El nombre tal como se le dice y se lo muestra.</summary>
    public string Name { get; }

    /// <summary>
    /// Frases de activación derivadas del nombre, siempre de dos palabras.
    /// </summary>
    /// <remarks>
    /// Nunca la palabra suelta, y no es una preferencia estética. Medido en el registro real con el
    /// nombre original: «viernes» dicho al pasar —«el viernes tengo turno»— despertaba al asistente
    /// con confianza 0,69, mientras las activaciones verdaderas puntuaban entre 0,62 y 0,68. El falso
    /// positivo puntuaba <em>más alto</em> que casi todos los aciertos, así que ningún umbral los
    /// separaba y subirlo sólo apagaba el reconocimiento.
    /// <para>
    /// Exigir dos palabras cambia el problema de raíz: «hola Ana» o «che Ana» no aparecen en una
    /// conversación por accidente, y son además la forma natural de dirigirse a alguien. Por eso las
    /// frases se derivan del nombre en vez de dejarse elegir sueltas: cualquier nombre que el usuario
    /// escriba —incluso uno que sea una palabra de todos los días— queda protegido igual.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> WakePhrases =>
        [$"Hola {this.Name}", $"Che {this.Name}", $"Ey {this.Name}"];

    /// <summary>Cómo se presenta el asistente en la primera línea del prompt del sistema.</summary>
    public string PromptName => this.Name;

    /// <summary>
    /// Dice si un nombre propuesto sirve, y si no, por qué, en palabras que se le puedan mostrar
    /// a quien está instalando.
    /// </summary>
    /// <remarks>
    /// Devuelve el motivo en castellano porque el único lugar que llama a esto es el instalador y la
    /// pantalla de bienvenida: un <c>false</c> pelado obligaría a inventar el mensaje dos veces.
    /// </remarks>
    public static bool TryValidate(string? candidate, out string? problem)
    {
        var trimmed = candidate?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            problem = "Escribí un nombre.";
            return false;
        }

        if (trimmed.Length < MinimumLength)
        {
            problem = "El nombre necesita al menos dos letras.";
            return false;
        }

        if (trimmed.Length > MaximumLength)
        {
            problem = $"El nombre no puede pasar de {MaximumLength} caracteres.";
            return false;
        }

        if (trimmed.Any(char.IsDigit))
        {
            problem = "Sin números: el nombre se dice en voz alta y los números no se reconocen bien.";
            return false;
        }

        if (!trimmed.Any(char.IsLetter))
        {
            problem = "El nombre tiene que tener letras.";
            return false;
        }

        if (trimmed.Any(character => !IsAcceptable(character)))
        {
            problem = "Usá sólo letras, espacios, guiones o apóstrofos.";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Deja el nombre en su forma canónica, o vuelve al de fábrica si lo propuesto no sirve.
    /// </summary>
    /// <remarks>
    /// Nunca lanza. Este valor viene de un archivo de preferencias en disco que cualquiera puede
    /// editar a mano, y un asistente que no arranca porque alguien escribió un nombre con un símbolo
    /// raro es peor que uno que se llama como venía de fábrica.
    /// </remarks>
    public static string Normalize(string? raw)
    {
        if (!TryValidate(raw, out _))
        {
            return DefaultName;
        }

        // Los espacios internos se colapsan: «Ana   María» y «Ana María» tienen que despertar igual.
        var collapsed = string.Join(
            ' ',
            raw!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return Capitalize(collapsed);
    }

    private static bool IsAcceptable(char character) =>
        char.IsLetter(character) || character is ' ' or '-' or '\'' or '’';

    private static string Capitalize(string value)
    {
        // Sólo se toca la primera letra de cada palabra si venía en minúscula. Alguien que escribe
        // «JARVIS» o «McCoy» eligió esas mayúsculas a propósito y no hay por qué corregirlas.
        var builder = new StringBuilder(value.Length);
        var startOfWord = true;

        foreach (var character in value)
        {
            builder.Append(startOfWord && char.IsLower(character)
                ? char.ToUpper(character, CultureInfo.CurrentCulture)
                : character);
            startOfWord = character is ' ' or '-';
        }

        return builder.ToString();
    }
}
