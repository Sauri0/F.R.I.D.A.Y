namespace Viernes.Core.Awareness;

/// <summary>Algo que Viernes notó y que <em>podría</em> valer una interrupción.</summary>
/// <remarks>
/// Los cuatro factores se multiplican y se les resta el costo de interrumpir. Multiplicar y no
/// promediar es deliberado: si algo no es accionable, no importa cuán urgente parezca —no hay nada
/// que hacer con la noticia, y avisar es sólo molestar—. Un cero en cualquier factor lo anula.
/// </remarks>
/// <param name="Key">Identifica el hecho, no el momento: sirve para no avisar dos veces lo mismo.</param>
/// <param name="Message">Lo que se le diría al usuario, ya redactado y corto.</param>
/// <param name="Importance">Cuánto cambia su día si se entera.</param>
/// <param name="Urgency">Cuánto se degrada la noticia si se la guarda para después.</param>
/// <param name="Confidence">Qué tan seguro está de que es cierto.</param>
/// <param name="Actionability">Si hay algo que hacer al respecto.</param>
public sealed record Observation(
    string Key,
    string Message,
    double Importance,
    double Urgency,
    double Confidence,
    double Actionability)
{
    public double Value(double interruptionCost) =>
        (Importance * Urgency * Confidence * Actionability) - interruptionCost;
}

/// <summary>
/// Decide si algo merece interrumpirte, que es un problema distinto de detectarlo.
/// </summary>
/// <remarks>
/// La proactividad no es hablar más: es <b>detectar lo importante antes de que preguntes</b> y
/// callarse el resto. Un asistente que comenta todo lo que ve es peor que uno mudo, porque al
/// tercer aviso irrelevante dejás de escuchar los que sí importan — y ahí perdió la capacidad de
/// avisarte de algo grave.
/// <para>
/// Por eso lo que gobierna acá no es la detección sino el freno: el mismo hecho nunca se avisa dos
/// veces, hay un mínimo de tiempo entre interrupciones, y estar escribiendo sube el costo de
/// interrumpir en vez de bajarlo. Viernes tiene permiso para no decir nada, y eso es una función.
/// </para>
/// </remarks>
public sealed class SalienceGate
{
    /// <summary>Piso de valor para hablar. Por debajo, se calla y lo guarda.</summary>
    private const double SpeakThreshold = 0.25;

    private readonly TimeSpan _minimumSpacing;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _announced = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _lastAnnouncement = DateTimeOffset.MinValue;

    public SalienceGate(TimeSpan? minimumSpacing = null, TimeProvider? timeProvider = null)
    {
        _minimumSpacing = minimumSpacing ?? TimeSpan.FromMinutes(10);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Devuelve lo que vale la pena decir, o <c>null</c> si conviene callarse.
    /// </summary>
    /// <param name="candidates">Todo lo observado, sin filtrar.</param>
    /// <param name="userIsBusy">
    /// Si el usuario está tecleando ahora mismo. Interrumpir a alguien concentrado cuesta más que
    /// interrumpir a alguien que volvió del café, y el costo entra en la cuenta, no en un permiso.
    /// </param>
    public Observation? Choose(IEnumerable<Observation> candidates, bool userIsBusy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (now - _lastAnnouncement < _minimumSpacing)
            {
                return null;
            }

            var cost = userIsBusy ? 0.45 : 0.15;
            Observation? best = null;
            var bestValue = SpeakThreshold;

            foreach (var candidate in candidates)
            {
                // Un hecho se avisa una sola vez. Que siga siendo cierto no lo vuelve noticia otra
                // vez: repetirlo es exactamente cómo un asistente se vuelve ruido de fondo.
                if (_announced.ContainsKey(candidate.Key))
                {
                    continue;
                }

                var value = candidate.Value(cost);
                if (value > bestValue)
                {
                    best = candidate;
                    bestValue = value;
                }
            }

            if (best is null)
            {
                return null;
            }

            _announced[best.Key] = now;
            _lastAnnouncement = now;
            return best;
        }
    }

    /// <summary>
    /// Olvida un hecho para que pueda volver a avisarse si reaparece. Se llama cuando la condición
    /// dejó de cumplirse: la próxima vez sí es noticia nueva.
    /// </summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _announced.Remove(key);
        }
    }
}
