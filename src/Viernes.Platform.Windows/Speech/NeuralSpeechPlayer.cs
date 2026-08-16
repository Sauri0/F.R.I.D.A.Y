using NAudio.Wave;

namespace Viernes.Platform.Windows.Speech;

/// <summary>
/// Reproduce el PCM que devuelve la voz neural. Trabaja con muestras crudas, así que no necesita
/// ningún decodificador: NAudio.WinMM alcanza.
/// </summary>
public sealed class NeuralSpeechPlayer : IAsyncDisposable
{
    // Formato que devuelve el endpoint de voz al pedir PCM crudo: 24 kHz, 16 bits, mono.
    // Se repite acá en vez de referenciar Core para no invertir la separación entre capas.
    private static readonly WaveFormat PlaybackFormat = new(24_000, 16, 1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WaveOutEvent? _output;
    private bool _isDisposed;

    public bool IsSpeaking { get; private set; }

    /// <summary>Reproduce hasta el final o hasta que se cancele. Devuelve false si no pudo sonar.</summary>
    public async Task<bool> PlayAsync(byte[] pcm, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (pcm.Length == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopInternal();

            using var stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), PlaybackFormat);
            var output = new WaveOutEvent();
            _output = output;
            output.Init(stream);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, _) => completion.TrySetResult(true);

            IsSpeaking = true;
            output.Play();

            await using (cancellationToken.Register(() =>
            {
                try
                {
                    output.Stop();
                }
                catch (Exception)
                {
                    // El dispositivo ya se cerró; la espera termina igual.
                }
            }).ConfigureAwait(false))
            {
                await completion.Task.ConfigureAwait(false);
            }

            return !cancellationToken.IsCancellationRequested;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsSpeaking = false;
            DisposeOutput();
            _gate.Release();
        }
    }

    public void Stop()
    {
        if (_isDisposed)
        {
            return;
        }

        StopInternal();
    }

    private void StopInternal()
    {
        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // Detener algo que ya terminó no es un error.
        }
    }

    private void DisposeOutput()
    {
        try
        {
            _output?.Dispose();
        }
        catch (Exception)
        {
            // Idem.
        }

        _output = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        StopInternal();
        DisposeOutput();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
