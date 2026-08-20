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
    /// Se busca desde el primer corchete hasta el último, que es lo que sobrevive a una frase
    /// adelante y a un bloque de código alrededor. Si eso no es un arreglo válido se prueba con un
    /// objeto suelto entre llaves: contestar una sola nota sin el arreglo es el error de formato más
    /// común de todos.
    /// </remarks>
    private static IEnumerable<JsonElement> Leer(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return [];
        }

        if (Entre(reply, '[', ']') is { } arreglo &&
            arreglo.RootElement.ValueKind == JsonValueKind.Array)
        {
            using (arreglo)
            {
                return [.. arreglo.RootElement
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => item.Clone())];
            }
        }

        if (Entre(reply, '{', '}') is { } objeto &&
            objeto.RootElement.ValueKind == JsonValueKind.Object)
        {
            using (objeto)
            {
                return [objeto.RootElement.Clone()];
            }
        }

        return [];
    }

    private static JsonDocument? Entre(string texto, char abre, char cierra)
    {
        var desde = texto.IndexOf(abre);
        var hasta = texto.LastIndexOf(cierra);
        if (desde < 0 || hasta <= desde)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(texto[desde..(hasta + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Campo(JsonElement objeto, string nombre) =>
        objeto.TryGetProperty(nombre, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
