using System.IO;
using System.Text;

namespace Viernes.App.Diagnostics;

/// <summary>
/// Bitácora mínima del recorrido de voz: qué pasó, en qué orden y cuánto tardó. Escribe sólo
/// eventos y tiempos, nunca audio, credenciales ni el contenido de las respuestas.
/// </summary>
/// <remarks>
/// Existe porque diagnosticar «se activa y no hace nada» leyendo código es adivinar: hay cuatro
/// componentes turnándose el micrófono y el orden real sólo se ve corriendo.
/// </remarks>
internal static class RuntimeTrace
{
    private static readonly Lock Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Viernes",
        "trace.log");

    private static readonly DateTimeOffset Started = DateTimeOffset.Now;

    public static void Write(string stage, string? detail = null)
    {
        try
        {
            var line = new StringBuilder()
                .Append((DateTimeOffset.Now - Started).TotalSeconds.ToString("00000.000"))
                .Append("  ")
                .Append(stage.PadRight(26))
                .Append(detail ?? string.Empty)
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line);
            }
        }
        catch (Exception)
        {
            // Una bitácora que falla nunca puede tumbar al asistente.
        }
    }

    /// <summary>Arranca una bitácora limpia por sesión, para que un reporte sea de una sola corrida.</summary>
    public static void Reset()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, $"Viernes · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}
