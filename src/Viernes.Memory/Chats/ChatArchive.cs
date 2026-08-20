using System.Collections.Concurrent;
using System.Text;
using Viernes.Memory.Privacy;

namespace Viernes.Memory.Chats;

/// <summary>Quién dijo cada cosa en una charla.</summary>
public enum ChatVoice
{
    /// <summary>La persona.</summary>
    Persona,

    /// <summary>Ella.</summary>
    Ella,

    /// <summary>Ni una ni otra: una herramienta que se usó, una caída, algo que pasó.</summary>
    Nota
}

/// <summary>
/// Deja escrita cada conversación, mientras pasa, en un archivo de texto que se puede leer.
/// </summary>
/// <remarks>
/// <b>Hasta acá las charlas no se guardaban. Ninguna.</b> Los turnos vivían en una lista en memoria
/// —y sólo los de la persona, no los de ella— y se tiraban al cerrar. No quedaba nada que releer,
/// nada que revisar cuando algo salía mal, y nada de dónde aprender. Todo lo que ella supiera tenía
/// que caber en una libreta de quinientas notas de quinientos caracteres.
/// <para>
/// <b>Se escribe mientras pasa y no al cerrar</b>, y ésa es la decisión que manda sobre el resto del
/// diseño. Guardar al final parece más prolijo —queda un archivo por charla, escrito de una— pero
/// garantiza perder exactamente las charlas que hay que poder revisar: la que se colgó, la que se
/// cortó, la que terminó con la aplicación muerta. Cada turno se escribe apenas se sabe, así que lo
/// peor que puede pasar es que falte el último renglón.
/// </para>
/// <para>
/// Es Markdown y no una base de datos a propósito. El usuario puede abrir la carpeta, leer lo que
/// se dijo, corregirlo y borrar lo que no quiere que quede — con cualquier editor y sin pedirle
/// permiso a nadie. Un formato que sólo el programa entiende convierte «tu asistente se acuerda de
/// vos» en «tu asistente guarda cosas sobre vos que no podés ver».
/// </para>
/// <para>
/// Escribir es de un hilo aparte. Quien anota un turno es el bucle de conversación o el hilo que lee
/// del socket, y ninguno de los dos puede esperar a un disco: el mismo error —E/S sincrónica en el
/// camino de un evento— ya hizo en este repositorio que soltar el orbe se sintiera pegajoso.
/// </para>
/// </remarks>
public sealed class ChatArchive : IDisposable
{
    private readonly BlockingCollection<string> _pending = new(new ConcurrentQueue<string>());
    private readonly Thread _writer;
    private readonly string _path;
    private readonly TimeProvider _time;

    private int _turns;
    private bool _closed;

    private ChatArchive(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _writer = new Thread(Escribir)
        {
            IsBackground = true,
            Name = "viernes-charla"
        };

        _writer.Start();
    }

    /// <summary>Dónde quedó el archivo de esta charla.</summary>
    public string Path => _path;

    /// <summary>
    /// Cuántos turnos de conversación se anotaron. Las notas no cuentan.
    /// </summary>
    /// <remarks>
    /// <b>Contar las notas rompía el borrado de las charlas vacías, y no en teoría.</b> Quien cierra
    /// anota «— se cerró la charla —» <em>antes</em> de llamar a <see cref="Close"/>, así que si eso
    /// contara como turno el contador nunca valdría cero y la rama que borra no correría jamás. En
    /// la máquina del usuario había quedado exactamente ese archivo: 114 bytes, la cabecera y el
    /// renglón del cierre, de una vez que tocó el orbe y volvió a tocarlo sin decir nada.
    /// <para>
    /// Y el nombre queda siendo cierto: una nota no es un turno. Nadie dijo nada.
    /// </para>
    /// </remarks>
    public int Turns => Volatile.Read(ref _turns);

    /// <summary>
    /// Abre una charla nueva.
    /// </summary>
    /// <remarks>
    /// El nombre del archivo lleva la fecha y la hora y nada más. La tentación es ponerle el tema —se
    /// leería mejor en la carpeta— pero el tema recién se sabe al final, y renombrar un archivo que
    /// ya se está escribiendo es cómo se pierde una charla entera. El tema va adentro, en la
    /// cabecera, y ahí se puede corregir cuantas veces haga falta.
    /// </remarks>
    /// <param name="folder">La carpeta de charlas. Se crea si no está.</param>
    /// <param name="route">Por dónde va la charla: hablando o escribiendo.</param>
    /// <param name="timeProvider">El reloj. En las pruebas se le pasa uno de mentira.</param>
    public static ChatArchive Open(string folder, string route, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var time = timeProvider ?? TimeProvider.System;
        var ahora = time.GetLocalNow();

        Directory.CreateDirectory(folder);

        var archivo = System.IO.Path.Combine(folder, $"{ahora:yyyy-MM-dd-HHmmss}.md");
        var i = 2;
        while (File.Exists(archivo))
        {
            // Dos charlas en el mismo segundo es raro y posible —cerrar y volver a llamarla—, y
            // pisar la primera sería perderla entera.
            archivo = System.IO.Path.Combine(folder, $"{ahora:yyyy-MM-dd-HHmmss}-{i++}.md");
        }

        var charla = new ChatArchive(archivo, time);
        charla.Encolar(
            $"---{Environment.NewLine}" +
            $"cuando: {ahora:yyyy-MM-dd HH:mm}{Environment.NewLine}" +
            $"camino: {route}{Environment.NewLine}" +
            $"---{Environment.NewLine}");

        return charla;
    }

    /// <summary>
    /// Anota un turno. Vuelve enseguida: escribir es de otro hilo.
    /// </summary>
    /// <remarks>
    /// Lo vacío se descarta acá y no en el hilo que escribe, para que <see cref="Turns"/> cuente
    /// turnos de verdad: una charla que quedó en cero es una charla en la que no se dijo nada, y eso
    /// tiene que poder distinguirse de una en la que se dijo algo y no se escribió.
    /// </remarks>
    public void Note(ChatVoice who, string? text)
    {
        if (_closed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var quien = who switch
        {
            ChatVoice.Persona => "vos",
            ChatVoice.Ella => "ella",
            _ => "—"
        };

        // Tapado antes de tocar el disco, no después. Una nota de memoria con una clave adentro se
        // rechaza entera y el usuario se entera; un transcripto no se puede rechazar —es lo que se
        // dijo— así que lo único que queda es que la clave no llegue a escribirse.
        if (who != ChatVoice.Nota)
        {
            Interlocked.Increment(ref _turns);
        }

        Encolar(
            $"**{quien}** · {_time.GetLocalNow():HH:mm:ss}{Environment.NewLine}{Environment.NewLine}" +
            $"{MemoryContentPolicy.Redact(text.Trim())}{Environment.NewLine}{Environment.NewLine}");
    }

    /// <summary>
    /// Cierra la charla y espera a que termine de escribirse.
    /// </summary>
    /// <remarks>
    /// Acá sí se espera, y es el único lugar: al cerrar ya no hay nadie a quien hacer esperar, y en
    /// cambio sí hay algo que se pierde si no se espera — los últimos turnos, que son los que
    /// explican por qué se cerró.
    /// <para>
    /// Una charla sin un solo turno se borra. Tocar el orbe sin decir nada abre y cierra una, y una
    /// carpeta llena de archivos vacíos hace que la carpeta deje de servir.
    /// </para>
    /// </remarks>
    /// <param name="wait">Cuánto esperar como mucho. Pasado eso se sigue igual.</param>
    public void Close(TimeSpan? wait = null)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _pending.CompleteAdding();
        _writer.Join(wait ?? TimeSpan.FromSeconds(2));

        if (Turns != 0)
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            // Un archivo vacío que no se pudo borrar es un archivo vacío, no un problema.
        }
    }

    /// <summary>
    /// Cierra y suelta la cola, pero sólo si el hilo escritor ya terminó.
    /// </summary>
    /// <remarks>
    /// <b>Soltarla siempre podía tumbar el proceso entero.</b> Si <see cref="Close"/> se rinde en su
    /// espera —un disco lento, un antivirus mirando el archivo— el hilo escritor sigue parado en
    /// <c>GetConsumingEnumerable</c>, y desecharle la cola desde acá le tira una
    /// <see cref="ObjectDisposedException"/> que no atrapa nadie: el bucle sólo espera errores de
    /// entrada/salida. Una excepción sin dueño en un hilo propio termina el proceso.
    /// <para>
    /// No soltarla cuesta la memoria de una cola vacía hasta que el proceso muera. Es un precio que
    /// no se compara con el otro.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        Close();

        if (!_writer.IsAlive)
        {
            _pending.Dispose();
        }
    }

    private void Encolar(string texto)
    {
        try
        {
            _pending.Add(texto);
        }
        catch (Exception excepcion) when (excepcion is InvalidOperationException or ObjectDisposedException)
        {
            // La charla ya se cerró. Perder un renglón tardío no puede tumbar nada.
        }
    }

    /// <summary>
    /// El hilo que escribe: junta lo que haya en la cola y lo manda de una.
    /// </summary>
    /// <remarks>
    /// Se agrupa porque una charla hablada produce turnos de a ráfagas —el servidor cierra tramos
    /// cada pocos cientos de milisegundos— y abrir el archivo una vez por tramo es pagar el costo de
    /// abrir tantas veces como tramos haya.
    /// </remarks>
    /// <summary>Lo que haya en la cola, y nada si ya la soltaron.</summary>
    private IEnumerable<string> Pendientes()
    {
        while (true)
        {
            string texto;
            try
            {
                if (!_pending.TryTake(out texto!, Timeout.Infinite))
                {
                    yield break;
                }
            }
            catch (Exception excepcion) when (excepcion is ObjectDisposedException or InvalidOperationException)
            {
                yield break;
            }

            yield return texto;
        }
    }

    private void Escribir()
    {
        var buffer = new StringBuilder();

        foreach (var texto in Pendientes())
        {
            buffer.Append(texto);
            while (_pending.TryTake(out var siguiente))
            {
                buffer.Append(siguiente);
            }

            try
            {
                File.AppendAllText(_path, buffer.ToString(), Encoding.UTF8);
            }
            catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
            {
                // Un disco lleno o una carpeta sin permiso no pueden cortar una conversación. Se
                // pierde el renglón y se sigue: quedarse sin asistente por no poder anotar sería
                // cambiar un problema chico por uno grande.
            }
            catch (ObjectDisposedException)
            {
                // Le soltaron la cola por abajo. Es el cinturón del arreglo de Dispose: cualquier
                // camino que llegue acá tiene que terminar el hilo, no el proceso.
                return;
            }

            buffer.Clear();
        }
    }
}
