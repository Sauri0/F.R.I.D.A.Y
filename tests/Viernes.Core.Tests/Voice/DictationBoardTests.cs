using Viernes.Core.Voice;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El acumulador de la transcripción en curso contra las tres formas en que llega el texto.
/// </summary>
/// <remarks>
/// La regla de qué palabra es provisoria está probada aparte, en <see cref="DictationLineTests"/>.
/// Acá se comprueba lo otro: que las tres fuentes —hipótesis de SAPI, fragmentos en vivo y un WAV
/// entero de Whisper— terminen dibujando lo mismo.
/// </remarks>
public sealed class DictationBoardTests
{
    [Fact]
    public void MientrasSeForma_LaUltimaPalabraEsProvisoria()
    {
        var pizarra = new DictationBoard();

        var palabras = pizarra.Hear("creame una carpeta");

        Assert.Equal(["creame", "una", "carpeta"], palabras.Select(palabra => palabra.Text));
        Assert.Equal(DictationQuality.Provisorio, palabras[^1].Quality);
        Assert.All(palabras.Take(2), palabra => Assert.Equal(DictationQuality.Confirmado, palabra.Quality));
    }

    [Fact]
    public void UnaHipotesisNuevaReemplazaALaAnterior()
    {
        // SAPI manda la hipótesis completa del tramo abierto, no lo que le falta. Sumarlas en vez de
        // reemplazarlas escribe la frase tres veces seguidas en la burbuja.
        var pizarra = new DictationBoard();

        pizarra.Hear("creame");
        pizarra.Hear("creame una");
        var palabras = pizarra.Hear("creame una carpeta");

        Assert.Equal(["creame", "una", "carpeta"], palabras.Select(palabra => palabra.Text));
    }

    [Fact]
    public void AlCerrarElTramo_NoQuedaNingunaProvisoria()
    {
        var pizarra = new DictationBoard();
        pizarra.Hear("creame una carpe");

        var palabras = pizarra.Confirm("creame una carpeta");

        Assert.Equal(["creame", "una", "carpeta"], palabras.Select(palabra => palabra.Text));
        Assert.All(palabras, palabra => Assert.Equal(DictationQuality.Confirmado, palabra.Quality));
    }

    [Fact]
    public void LoQueYaQuedoFirmeNoSeBorraConLaHipotesisSiguiente()
    {
        // El push-to-talk cierra varios tramos en una sola captura. Si la hipótesis del segundo
        // pisara al primero, la burbuja iría perdiendo el principio de la frase mientras se habla.
        var pizarra = new DictationBoard();
        pizarra.Confirm("creame una carpeta");

        var palabras = pizarra.Hear("y abrila");

        Assert.Equal(
            ["creame", "una", "carpeta", "y", "abrila"],
            palabras.Select(palabra => palabra.Text));
        Assert.Equal(DictationQuality.Provisorio, palabras[^1].Quality);
        Assert.Equal(DictationQuality.Confirmado, palabras[2].Quality);
    }

    [Fact]
    public void LoRecuperadoVaAdelanteYSeDibujaDistinto()
    {
        var pizarra = new DictationBoard();
        pizarra.Recover("estaba pensando que", TimeSpan.FromSeconds(4));

        var palabras = pizarra.Hear("Viernes anotá");

        Assert.Equal(
            [
                DictationQuality.Recuperado,
                DictationQuality.Recuperado,
                DictationQuality.Recuperado,
                DictationQuality.Confirmado,
                DictationQuality.Provisorio
            ],
            palabras.Select(palabra => palabra.Quality));
        Assert.True(pizarra.HasRecovered);
        Assert.Equal(TimeSpan.FromSeconds(4), pizarra.RecoveredSpan);
    }

    [Fact]
    public void SinNadaRecuperado_NoHayTramoRecuperadoAunqueSeInformeUnaDuracion()
    {
        // Decir «recuperé cuatro segundos» y no tener ni una palabra es lo que hace aparecer el
        // encabezado «recuperado del búfer» sobre un bloque vacío.
        var pizarra = new DictationBoard();

        pizarra.Recover("   ", TimeSpan.FromSeconds(4));

        Assert.False(pizarra.HasRecovered);
        Assert.Equal(TimeSpan.Zero, pizarra.RecoveredSpan);
    }

    [Fact]
    public void LaFraseQueLlegaEnteraSeAsientaDeUnaVez()
    {
        // Whisper no entrega nada hasta que el micrófono se cerró: llega la frase completa y ya.
        var pizarra = new DictationBoard();
        pizarra.Recover("che", TimeSpan.FromSeconds(2));

        var palabras = pizarra.Settle("Viernes creame una carpeta");

        Assert.Equal(
            ["che", "Viernes", "creame", "una", "carpeta"],
            palabras.Select(palabra => palabra.Text));
        Assert.All(palabras.Skip(1), palabra => Assert.Equal(DictationQuality.Confirmado, palabra.Quality));
    }

    [Fact]
    public void AsentarDosVecesNoDuplicaLaFrase()
    {
        var pizarra = new DictationBoard();
        pizarra.Settle("creame una carpeta");

        var palabras = pizarra.Settle("creame una carpeta");

        Assert.Equal(3, palabras.Count);
    }

    [Fact]
    public void BorrarSeLlevaTambienLoRecuperado()
    {
        var pizarra = new DictationBoard();
        pizarra.Recover("estaba pensando", TimeSpan.FromSeconds(3));
        pizarra.Confirm("Viernes anotá");

        pizarra.Clear();

        Assert.Empty(pizarra.Current(live: false));
        Assert.False(pizarra.HasRecovered);
        Assert.Equal(TimeSpan.Zero, pizarra.RecoveredSpan);
    }

    [Fact]
    public void LosFragmentosEnVivoSeMuestranApenasLlegan()
    {
        // La sesión en vivo entrega la transcripción de a pedazos y no siempre por palabra: lo que
        // se acumula puede cortar una palabra al medio, y esa es justo la que va en itálica.
        var pizarra = new DictationBoard();

        var acumulado = string.Empty;
        foreach (var fragmento in new[] { "abrí ", "Spo", "tify" })
        {
            acumulado += fragmento;
            pizarra.Hear(acumulado);
        }

        var palabras = pizarra.Hear(acumulado);

        Assert.Equal(["abrí", "Spotify"], palabras.Select(palabra => palabra.Text));
        Assert.Equal(DictationQuality.Provisorio, palabras[^1].Quality);
    }
}
