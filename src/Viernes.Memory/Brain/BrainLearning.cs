using System.Text.Json;

namespace Viernes.Memory.Brain;

/// <summary>
/// Convierte lo que contestó el modelo al cerrar una charla en notas guardadas.
/// </summary>
/// <remarks>
/// <b>Vive acá y no en el anfitrión para poder probarlo.</b> Estaba metido adentro de la clase que
/// maneja micrófono, ventana y modelos, que ninguna prueba puede armar — y es justo la parte más
/// fácil de que salga mal, porque del otro lado hay un modelo contestando texto libre.
/// <para>
/// Todo lo de acá es tolerancia a que el modelo conteste cualquier cosa. Un modelo al que se le pide
/// JSON pelado devuelve, con toda naturalidad, JSON adentro de un bloque de código, JSON con una
/// frase de cortesía adelante, un objeto suelto en vez de un arreglo, o campos que no existen.
/// Rechazar todo eso sería no aprender nunca por un detalle de formato.
/// </para>
/// </remarks>
public static class BrainLearning
{
    /// <summary>Cuántas notas se aceptan de una sola charla.</summary>
    /// <remarks>
    /// Tres. No es una limitación técnica: una charla que dejó ocho cosas duraderas casi siempre
    /// dejó una y siete adornos, y un cerebro que crece ocho notas por conversación deja de ser
    /// legible en una semana. Lo que no entró va a volver a aparecer si de verdad importaba.
    /// </remarks>
    public const int MaximumPerChat = 3;

    /// <summary>
    /// Lee lo que contestó el modelo y guarda lo que sirva.
    /// </summary>
    /// <param name="brain">Dónde guardar.</param>
    /// <param name="reply">Lo que contestó el modelo, tal cual vino.</param>
    /// <param name="evidence">De qué charla salió.</param>
    /// <returns>Cuántas notas quedaron guardadas.</returns>
    public static int Learn(this Brain brain, string? reply, IReadOnlyList<string>? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(brain);

        var guardadas = 0;
        foreach (var cruda in Leer(reply).Take(MaximumPerChat))
        {
            var titulo = Campo(cruda, "titulo");
            var cuerpo = Campo(cruda, "cuerpo");

            // Un título de dos letras o un cuerpo de tres no son una nota: son un modelo llenando el
            // formulario. Entran al cerebro para siempre, así que el filtro va acá y no después.
            if (titulo.Length < 3 || cuerpo.Length < 8)
            {
                continue;
            }

            var nota = brain.Note(
                Enum.TryParse<BrainNoteKind>(Campo(cruda, "tipo"), ignoreCase: true, out var tipo)
                    ? tipo
                    : BrainNoteKind.Preferencia,
                titulo,
                cuerpo,
                Campo(cruda, "alcance"),
                Enum.TryParse<BrainConfidence>(Campo(cruda, "confianza"), ignoreCase: true, out var confianza)
                    ? confianza
                    : BrainConfidence.Media,
                evidence);

            var reemplaza = Campo(cruda, "reemplaza");
            var vieja = reemplaza.Length > 0 ? Brain.Slug(reemplaza) : null;

            // Reemplazarse a sí misma no es reemplazar: es que el modelo repitió el título. Sin este
            // freno, la nota nueva marcaría como vencida a la que se acaba de guardar y el cerebro
            // quedaría sin nada vigente sobre el tema.
            if (vieja is not null && !string.Equals(vieja, nota.Name, StringComparison.Ordinal))
            {
                brain.Supersede(vieja, nota);
            }
            else
            {
                brain.Save(nota);
            }

            guardadas++;
        }

        return guardadas;
    }

    /// <summary>
    /// Saca del texto los objetos que haya, venga como venga.
    /// </summary>
    /// <remarks>
    /// Se prueban las dos formas y <b>la primera que dé algo gana</b>, que es lo que arregla el
    /// defecto: antes, encontrar un arreglo hacía <c>return</c> aunque estuviera vacío, así que un
    /// corchete suelto en una frase de cortesía —«te dejo [lo que aprendí]…»— se llevaba puestas
    /// TODAS las notas de esa charla, sin dejar rastro, y el respaldo del objeto suelto —que el
    /// comentario de al lado llama «el error de formato más común de todos»— nunca llegaba a correr.
    /// </remarks>
    private static IEnumerable<JsonElement> Leer(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return [];
        }

        var delArreglo = DelArreglo(reply);
        return delArreglo.Count > 0 ? delArreglo : DelObjeto(reply);
    }

    private static IReadOnlyList<JsonElement> DelArreglo(string reply)
    {
        if (Entre(reply, '[', ']') is not { } arreglo)
        {
            return [];
        }

        using (arreglo)
        {
            return arreglo.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : [.. arreglo.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())];
        }
    }

    private static IReadOnlyList<JsonElement> DelObjeto(string reply)
    {
        if (Entre(reply, '{', '}') is not { } objeto)
        {
            return [];
        }

        using (objeto)
        {
            return objeto.RootElement.ValueKind != JsonValueKind.Object
                ? []
                : [objeto.RootElement.Clone()];
        }
    }

    /// <summary>
    /// El primer bloque balanceado que empiece con ese carácter, probando desde cada aparición.
    /// </summary>
    /// <remarks>
    /// Del primero al último no sirve: un corchete de más en la prosa de alrededor mete texto que no
    /// es JSON adentro del recorte y lo tira todo. Acá se prueba desde cada apertura y se corta en su
    /// cierre balanceado, saltando lo que esté adentro de una cadena — que es donde viven los
    /// corchetes que no cuentan, porque el cuerpo de una nota puede tener cualquier cosa escrita.
    /// </remarks>
    private static JsonDocument? Entre(string texto, char abre, char cierra)
    {
        for (var inicio = texto.IndexOf(abre); inicio >= 0; inicio = texto.IndexOf(abre, inicio + 1))
        {
            if (Balanceado(texto, inicio, abre, cierra) is not { } fin)
            {
                continue;
            }

            try
            {
                return JsonDocument.Parse(texto[inicio..(fin + 1)]);
            }
            catch (JsonException)
            {
                // Ese bloque no era. Se sigue con la apertura siguiente.
            }
        }

        return null;
    }

    /// <summary>Dónde cierra el bloque que abre en <paramref name="inicio"/>, o nulo si no cierra.</summary>
    private static int? Balanceado(string texto, int inicio, char abre, char cierra)
    {
        var nivel = 0;
        var enCadena = false;
        var escapado = false;

        for (var i = inicio; i < texto.Length; i++)
        {
            var letra = texto[i];

            if (enCadena)
            {
                if (escapado)
                {
                    escapado = false;
                }
                else if (letra == '\\')
                {
                    escapado = true;
                }
                else if (letra == '"')
                {
                    enCadena = false;
                }

                continue;
            }

            if (letra == '"')
            {
                enCadena = true;
            }
            else if (letra == abre)
            {
                nivel++;
            }
            else if (letra == cierra && --nivel == 0)
            {
                return i;
            }
        }

        return null;
    }

    private static string Campo(JsonElement objeto, string nombre) =>
        objeto.TryGetProperty(nombre, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
