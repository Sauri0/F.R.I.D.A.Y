namespace Viernes.Platform.Windows.Speech.Recognition;

/// <summary>
/// Cuenta en qué se parecen y en qué difieren dos detectores mirando el mismo audio.
/// </summary>
public sealed record VoiceActivityAgreement(
    long Frames,
    long BothVoice,
    long BothSilence,
    long OnlyPrimary,
    long OnlyBackup)
{
    /// <summary>Proporción de bloques en los que los dos dijeron lo mismo, de 0 a 1.</summary>
    public double Agreement => Frames == 0 ? 1 : (double)(BothVoice + BothSilence) / Frames;

    /// <summary>Una línea para el informe del banco de medición.</summary>
    public override string ToString() =>
        $"{Frames} bloques · coinciden {Agreement:P1} · sólo el nuevo {OnlyPrimary} · " +
        $"sólo el viejo {OnlyBackup}";
}

/// <summary>
/// Hace correr dos detectores en paralelo y deja el registro de quién dijo qué.
/// </summary>
/// <remarks>
/// El usuario pidió expresamente poder comparar antes de confiar en el detector nuevo, y tiene
/// razón: acá ya pasó que una constante se movió por una sola medición y hubo que volver atrás. Un
/// detector que se cambia sin medir es una apuesta, y la única forma de medir sin un banco de audio
/// etiquetado es hacerlos escuchar lo mismo y mirar dónde se separan.
/// <para>
/// Manda uno solo —<see cref="Primary"/>—; el otro corre al lado sin poder cortar nada. Así se puede
/// tener el modelo entrenado escuchando durante días con la heurística todavía al mando, o al revés.
/// </para>
/// </remarks>
public sealed class VoiceActivityScoreboard : IVoiceActivityDetector
{
    private readonly object _sync = new();
    private long _frames;
    private long _bothVoice;
    private long _bothSilence;
    private long _onlyPrimary;
    private long _onlyBackup;
    private bool _disposed;

    /// <summary>
    /// Arma la comparación. El primero manda; el segundo sólo se anota.
    /// </summary>
    public VoiceActivityScoreboard(IVoiceActivityDetector primary, IVoiceActivityDetector backup)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(backup);
        Primary = primary;
        Backup = backup;
    }

    /// <summary>El que decide de verdad.</summary>
    public IVoiceActivityDetector Primary { get; }

    /// <summary>El que corre al lado para poder compararlo.</summary>
    public IVoiceActivityDetector Backup { get; }

    public VoiceActivityDetectorInfo Info => new(
        $"{Primary.Info.Name} (comparado con {Backup.Info.Name})",
        Primary.Info.IsTrainedModel,
        Primary.Info.Description);

    /// <summary>Lo acumulado hasta ahora.</summary>
    public VoiceActivityAgreement Agreement
    {
        get
        {
            lock (_sync)
            {
                return new VoiceActivityAgreement(_frames, _bothVoice, _bothSilence, _onlyPrimary, _onlyBackup);
            }
        }
    }

    public VoiceActivityDecision Analyze(ReadOnlySpan<short> samples, bool insideUtterance)
    {
        var primary = Primary.Analyze(samples, insideUtterance);

        // El respaldo recibe el mismo «¿estamos adentro de una frase?» que el titular. Darle el suyo
        // propio los haría divergir por el estado y no por el criterio, que es lo que se quiere medir.
        var backup = Backup.Analyze(samples, insideUtterance);

        lock (_sync)
        {
            _frames++;
            if (primary.IsVoice && backup.IsVoice)
            {
                _bothVoice++;
            }
            else if (!primary.IsVoice && !backup.IsVoice)
            {
                _bothSilence++;
            }
            else if (primary.IsVoice)
            {
                _onlyPrimary++;
            }
            else
            {
                _onlyBackup++;
            }
        }

        return primary;
    }

    public void Reset()
    {
        Primary.Reset();
        Backup.Reset();
    }

    /// <summary>Borra la cuenta sin tocar a los detectores.</summary>
    public void ClearCounters()
    {
        lock (_sync)
        {
            _frames = 0;
            _bothVoice = 0;
            _bothSilence = 0;
            _onlyPrimary = 0;
            _onlyBackup = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Primary.Dispose();
        Backup.Dispose();
    }
}
