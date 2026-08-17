using NAudio.Wave;

namespace Viernes.Platform.Windows.Speech;

/// <summary>
/// Reproduce el PCM que devuelve la voz neural. Trabaja con muestras crudas, así que no necesita
/// ningún decodificador: NAudio.WinMM alcanza.
/// </summary>
public sealed class NeuralSpeechPlayer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WaveOutEvent? _output;
    private bool _isDisposed;

    public bool IsSpeaking { get; private set; }

    /// <summary>
    /// Reproduce hasta el final o hasta que se cancele. Devuelve false si no pudo sonar.
    /// La frecuencia viene del proveedor: suponerla hace que la voz suene a otra persona.
    /// </summary>
    public async Task<bool> PlayAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (pcm.Length == 0)
        {
            return false;
        }

        var format = new WaveFormat(sampleRate, 16, 1);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopInternal();

            using var stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), format);
            var output = new WaveOutEvent();
            _output = output;
            output.Init(stream);

            // El argumento traía la excepción del dispositivo y se descartaba con un `_`: si sacabas
            // el auricular a mitad de frase, la reproducción moría y esto igual informaba que había
            // sonado entera. Ahora un fallo del dispositivo se propaga como lo que es.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, stopped) => completion.TrySetResult(stopped.Exception is null);

            IsSpeaking = true;
            output.Play();

            bool sonoEntera;
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
                sonoEntera = await completion.Task.ConfigureAwait(false);
            }

            return sonoEntera && !cancellationToken.IsCancellationRequested;
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
