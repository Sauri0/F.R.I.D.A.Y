using Viernes.Platform.Windows.Speech.WakeWord;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El caño que le pasa el mismo audio al reconocedor de nombre sin que abra el micrófono.
/// </summary>
/// <remarks>
/// Es lo que permite que el micrófono lo abra una sola aplicación, y de ahí sale todo lo demás: la
/// ventana rodante existe porque el audio pasa por acá antes de llegar a SAPI. Se prueba sin
/// micrófono porque es un anillo de bytes con un lector que bloquea, y las dos cosas que tienen que
/// salir bien —no devolver cero por silencio, no crecer sin límite— se ven en la aritmética.
/// </remarks>
public sealed class AudioPipeStreamTests
{
    private static AudioPipeStream Pipe(double seconds = 1) =>
        new(TimeSpan.FromSeconds(seconds), 32_000);

    [Fact]
    public void Read_DespuesDeEscribir_DevuelveLoMismo()
    {
        using var pipe = Pipe();
        pipe.Write([1, 2, 3, 4]);

        var buffer = new byte[4];
        var read = pipe.Read(buffer, 0, buffer.Length);

        Assert.Equal(4, read);
        Assert.Equal<byte[]>([1, 2, 3, 4], buffer);
    }

    [Fact]
    public async Task Read_SinAudio_EsperaEnVezDeDevolverCero()
    {
        // Para SAPI un cero es fin de audio y apaga el reconocimiento: medio segundo de silencio en
        // el cuarto le terminaría la sesión y el nombre dejaría de detectarse hasta reiniciar.
        using var pipe = Pipe();
        var buffer = new byte[4];

        var lectura = Task.Run(() => pipe.Read(buffer, 0, buffer.Length));

        var primero = await Task.WhenAny(lectura, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Assert.NotSame(lectura, primero);

        pipe.Write([9, 9, 9, 9]);

        Assert.Equal(4, await lectura.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Read_ConMenosDeLoPedido_SigueEsperandoHastaLlenarElBufer()
    {
        // Medido con un espía entre el caño y SAPI: devolviendo 960 bytes donde pidió 3040, SAPI hizo
        // UNA sola lectura y no volvió a pedir nunca más. Un byte de menos se lee igual que un cero, y
        // un cero es fin de audio. Es la razón por la que el oído continuo no detectaba nada.
        using var pipe = Pipe();
        var buffer = new byte[6];

        var lectura = Task.Run(() => pipe.Read(buffer, 0, buffer.Length));

        pipe.Write([1, 2]);
        var primero = await Task.WhenAny(lectura, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Assert.NotSame(lectura, primero);

        pipe.Write([3, 4, 5, 6]);

        Assert.Equal(6, await lectura.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6], buffer);
    }

    [Fact]
    public async Task Read_SiElCanoCierraAMitadDeLlenar_DevuelveLoQueJunto()
    {
        // El único corte legítimo. Sin esto, cerrar el caño dejaría al hilo de SAPI esperando para
        // siempre un búfer que nadie va a terminar de llenar, y Dispose no volvería nunca.
        using var pipe = Pipe();
        var buffer = new byte[6];

        var lectura = Task.Run(() => pipe.Read(buffer, 0, buffer.Length));
        pipe.Write([1, 2, 3]);
        await Task.Delay(50);
        pipe.Complete();

        Assert.Equal(3, await lectura.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Complete_DespiertaAlLectorConFinDeAudio()
    {
        // Si no, el hilo de SAPI queda trabado adentro de Read y Dispose no vuelve nunca.
        using var pipe = Pipe();
        var buffer = new byte[4];
        var lectura = Task.Run(() => pipe.Read(buffer, 0, buffer.Length));

        pipe.Complete();

        Assert.Equal(0, await lectura.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Write_SiElLectorSeAtrasa_TiraLoViejoEnVezDeCrecer()
    {
        // Un proceso que arranca con Windows y escucha todo el día no puede tener una cola que sólo
        // crece. Se prefiere perder audio viejo del reconocedor de nombre; la ventana rodante, que
        // es la que guarda la frase, es otra cosa y no se toca.
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(0.001), 32_000);

        pipe.Write(new byte[32]);
        pipe.Write(new byte[32]);

        Assert.Equal(32, pipe.Available);
        Assert.Equal(32, pipe.DroppedBytes);
    }

    [Fact]
    public void Write_UnBloqueMasGrandeQueElCano_ConservaLoUltimo()
    {
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(0.001), 32_000);
        var data = new byte[64];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)index;
        }

        pipe.Write(data);

        var buffer = new byte[32];
        Assert.Equal(32, pipe.Read(buffer, 0, buffer.Length));
        Assert.Equal(32, buffer[0]);
    }

    [Fact]
    public void Read_ConElAnilloDandoLaVuelta_DevuelveLosBytesEnOrden()
    {
        using var pipe = new AudioPipeStream(TimeSpan.FromSeconds(0.001), 32_000);
        pipe.Write(new byte[24]);
        Assert.Equal(24, pipe.Read(new byte[24], 0, 24));

        pipe.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        var buffer = new byte[12];
        var read = pipe.Read(buffer, 0, buffer.Length);


        Assert.Equal(12, read);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], buffer);
    }
}
