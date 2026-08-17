using Viernes.Core.Voice;

namespace Viernes.App.Services;

/// <summary>
/// Acuses cortos —«mhm», «dale»— ya sintetizados y guardados en memoria, listos para sonar al instante.
/// </summary>
/// <remarks>
/// Sirven para tapar la espera entre que terminás de hablar y que hay respuesta. No aceleran nada:
/// cambian una pausa muda por una señal de que te escuchó, que es la diferencia entre una charla y
/// un formulario.
/// <para>
/// Están pregrabados porque si no, no sirven. Sintetizar «mhm» cuesta la misma ida y vuelta a la red
/// que sintetizar la respuesta entera —uno o dos segundos—, así que un acuse pedido en el momento
/// llegaría cuando ya no hace falta. Se sintetizan una vez al arrancar, en segundo plano, y después
/// suenan desde memoria en microsegundos.
/// </para>
/// <para>
/// Son varios y se eligen al azar sin repetir el anterior: escuchar siempre el mismo sonido es lo
/// que hace que algo se sienta una máquina.
/// </para>
/// </remarks>
internal sealed class Acknowledgements
{
    /// <summary>
    /// Cortos y sin contenido a propósito: no pueden adelantar nada de la respuesta, porque todavía
    /// no existe. Sólo dicen «te escuché, dame un segundo».
    /// </summary>
    private static readonly string[] Phrases = ["Mhm.", "Dale.", "Ajá.", "Bien.", "Ya vemos."];

    private readonly OpenRouterSpeechClient _voice;
    private readonly List<SynthesizedSpeech> _cached = [];
    private readonly Lock _gate = new();
    private int _lastIndex = -1;
    private int _warming;

    public Acknowledgements(OpenRouterSpeechClient voice) => _voice = voice;

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _cached.Count > 0;
            }
        }
    }

    /// <summary>
    /// Sintetiza el banco una sola vez, en segundo plano. Si la voz remota no está disponible, no
    /// pasa nada: sin acuses el asistente funciona igual, sólo se siente más callado.
    /// </summary>
    public void Warm()
    {
        if (Interlocked.Exchange(ref _warming, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var phrase in Phrases)
            {
                try
                {
                    var audio = await _voice.SynthesizeAsync(phrase).ConfigureAwait(false);
                    if (audio is not null)
                    {
                        lock (_gate)
                        {
                            _cached.Add(audio);
                        }
                    }
                }
                catch (Exception)
                {
                    // Un acuse que falta no puede impedir que existan los demás.
                }
            }

            clock.Stop();
            int count;
            lock (_gate)
            {
                count = _cached.Count;
            }

            Diagnostics.RuntimeTrace.Write(
                "acuses",
                count == 0
                    ? $"ninguno grabado · {_voice.LastFailure ?? "sin motivo"}"
                    : $"{count} de {Phrases.Length} grabados en {clock.ElapsedMilliseconds} ms");
        });
    }

    /// <summary>Uno al azar, nunca el mismo que la vez anterior. <c>null</c> si el banco está vacío.</summary>
    public SynthesizedSpeech? Next()
    {
        lock (_gate)
        {
            if (_cached.Count == 0)
            {
                return null;
            }

            if (_cached.Count == 1)
            {
                return _cached[0];
            }

            int index;
            do
            {
                index = Random.Shared.Next(_cached.Count);
            }
            while (index == _lastIndex);

            _lastIndex = index;
            return _cached[index];
        }
    }
}
