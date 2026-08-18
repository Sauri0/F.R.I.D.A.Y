using System.Globalization;

namespace Viernes.Core.Live;

/// <summary>
/// Cuánto va costando la sesión, contando minutos de audio.
/// </summary>
/// <remarks>
/// Se cuenta por minutos de audio y no por tokens porque así es como se cobra esta API. El resto de
/// Viernes lleva la cuenta por tokens, y mezclar las dos formas daría un número que no coincide con
/// la factura.
/// <para>
/// Vale la pena tenerlo a mano porque una charla en vivo no se siente cara mientras pasa: son
/// fracciones de centavo por minuto y no hay ningún momento en el que uno «pida» algo caro. Media
/// hora de fondo, todos los días, es otra cosa.
/// </para>
/// </remarks>
public sealed class LiveCostMeter
{
    /// <summary>Dólares por minuto de audio enviado.</summary>
    public const decimal InputUsdPerMinute = 0.005m;

    /// <summary>Dólares por minuto de audio recibido.</summary>
    public const decimal OutputUsdPerMinute = 0.018m;

    private long _inputBytes;
    private long _outputBytes;

    /// <summary>Cuánto audio se mandó.</summary>
    public TimeSpan InputAudio => TimeSpan.FromSeconds(
        (double)Interlocked.Read(ref _inputBytes) / (LiveAudioFormat.InputSampleRate * LiveAudioFormat.BytesPerSample));

    /// <summary>Cuánto audio se recibió.</summary>
    public TimeSpan OutputAudio => TimeSpan.FromSeconds(
        (double)Interlocked.Read(ref _outputBytes) / (LiveAudioFormat.OutputSampleRate * LiveAudioFormat.BytesPerSample));

    /// <summary>
    /// Lo que costó hasta ahora, en dólares.
    /// </summary>
    /// <remarks>
    /// Es una estimación por lo bajo y hay que decirlo así: no incluye el contexto, que se cobra
    /// entero en cada turno. Justamente por eso la compresión de contexto no es sólo una cuestión de
    /// que la sesión no se muera.
    /// </remarks>
    public decimal EstimatedUsd =>
        ((decimal)InputAudio.TotalMinutes * InputUsdPerMinute) +
        ((decimal)OutputAudio.TotalMinutes * OutputUsdPerMinute);

    /// <summary>Suma audio enviado.</summary>
    public void AddInput(int byteCount)
    {
        if (byteCount > 0)
        {
            Interlocked.Add(ref _inputBytes, byteCount);
        }
    }

    /// <summary>Suma audio recibido.</summary>
    public void AddOutput(int byteCount)
    {
        if (byteCount > 0)
        {
            Interlocked.Add(ref _outputBytes, byteCount);
        }
    }

    /// <summary>Vuelve a cero.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _inputBytes, 0);
        Interlocked.Exchange(ref _outputBytes, 0);
    }

    /// <summary>Una línea para mostrar, en castellano y con dos decimales de centavo.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Sesión en vivo: {0:0} s hablando, {1:0} s escuchando, ~USD {2:0.0000}",
            InputAudio.TotalSeconds,
            OutputAudio.TotalSeconds,
            EstimatedUsd);
}
