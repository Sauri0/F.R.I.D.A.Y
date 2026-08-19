namespace Viernes.Core.Voice;

/// <summary>
/// Lo que se lleva transcripto de esta tanda de habla, en un solo lugar.
/// </summary>
/// <remarks>
/// <see cref="DictationLine"/> decide qué palabra es provisoria; acá se acumula lo que hay que
/// pasarle. Son dos cosas distintas y la que se reimplementa distinta en cada lado es ésta: hay tres
/// fuentes de transcripción —las hipótesis de SAPI, los fragmentos de la sesión en vivo y el WAV que
/// entrega el oído continuo— y cada una entrega el texto de otra forma. Sin un solo acumulador, cada
/// camino arma la línea a su manera y la burbuja se dibuja distinta según por dónde entró la voz.
/// <para>
/// Las tres se reducen a lo mismo: hay un tramo <em>recuperado</em> del búfer rodante que ya no
/// cambia, un tramo <em>firme</em> que el reconocedor cerró, y una cola que todavía se está
/// formando.
/// </para>
/// <para>
/// Lleva candado porque las hipótesis llegan del hilo del reconocedor y los fragmentos en vivo del
/// hilo que lee del servidor, mientras la interfaz puede estar leyendo la línea anterior.
/// </para>
/// </remarks>
public sealed class DictationBoard
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    private readonly Lock _gate = new();
    private readonly List<string> _recovered = [];
    private readonly List<string> _confirmed = [];
    private TimeSpan _recoveredSpan;

    /// <summary>Cuánto audio anterior al nombre se rescató. Cero si no se rescató nada.</summary>
    public TimeSpan RecoveredSpan
    {
        get
        {
            lock (_gate)
            {
                return _recoveredSpan;
            }
        }
    }

    /// <summary>Si hay algo recuperado del búfer en la línea actual.</summary>
    public bool HasRecovered
    {
        get
        {
            lock (_gate)
            {
                return _recovered.Count > 0;
            }
        }
    }

    /// <summary>
    /// Lo que la persona venía diciendo antes de nombrarla, rescatado de la ventana rodante.
    /// </summary>
    /// <param name="text">El tramo anterior al nombre, ya transcripto.</param>
    /// <param name="span">
    /// Cuánto audio se rescató de verdad. <b>No son los diez segundos de la ventana</b>: el recorte
    /// llega hasta donde arrancó esa tanda de habla, así que con la tele puesta no se le meten diez
    /// segundos de tele adelante del pedido. El número sale del recorte que se hizo, no de la
    /// configuración.
    /// </param>
    public void Recover(string? text, TimeSpan span)
    {
        lock (_gate)
        {
            _recovered.Clear();
            _recovered.AddRange(Split(text));
            _recoveredSpan = _recovered.Count == 0 ? TimeSpan.Zero : span;
        }
    }

    /// <summary>
    /// Llegó una hipótesis: la frase se sigue formando y la última palabra puede cambiar.
    /// </summary>
    /// <param name="pending">
    /// Lo que se está diciendo <b>después</b> de lo que ya quedó firme. Se reemplaza entero en cada
    /// llamada porque las tres fuentes lo entregan así: SAPI manda la hipótesis completa del tramo
    /// abierto y la sesión en vivo manda lo acumulado del turno.
    /// </param>
    public IReadOnlyList<DictationWord> Hear(string? pending)
    {
        lock (_gate)
        {
            var spoken = new List<string>(_confirmed);
            spoken.AddRange(Split(pending));
            return DictationLine.Build(_recovered, spoken, live: true);
        }
    }

    /// <summary>
    /// El reconocedor cerró un tramo: lo que había provisorio pasa a firme.
    /// </summary>
    /// <remarks>
    /// Con la frase cerrada <b>no queda ninguna palabra provisoria</b>. Si quedara, la frase
    /// terminaría temblando: el reconocedor ya no va a mandar nada que la reemplace.
    /// </remarks>
    /// <param name="text">El tramo que quedó firme.</param>
    public IReadOnlyList<DictationWord> Confirm(string? text)
    {
        lock (_gate)
        {
            _confirmed.AddRange(Split(text));
            return DictationLine.Build(_recovered, _confirmed, live: false);
        }
    }

    /// <summary>
    /// Reemplaza todo lo dicho por este texto y lo da por firme.
    /// </summary>
    /// <remarks>
    /// Para las fuentes que no entregan nada hasta el final —Whisper transcribe el WAV entero
    /// cuando el micrófono ya se cerró—, donde no hay tramos que ir sumando sino una frase completa
    /// que llega de una vez.
    /// </remarks>
    public IReadOnlyList<DictationWord> Settle(string? text)
    {
        lock (_gate)
        {
            _confirmed.Clear();
            _confirmed.AddRange(Split(text));
            return DictationLine.Build(_recovered, _confirmed, live: false);
        }
    }

    /// <summary>La línea de ahora, sin cambiarla.</summary>
    public IReadOnlyList<DictationWord> Current(bool live)
    {
        lock (_gate)
        {
            return DictationLine.Build(_recovered, _confirmed, live);
        }
    }

    /// <summary>Empieza otra frase: se borra todo, también lo recuperado.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _recovered.Clear();
            _confirmed.Clear();
            _recoveredSpan = TimeSpan.Zero;
        }
    }

    private static IEnumerable<string> Split(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
