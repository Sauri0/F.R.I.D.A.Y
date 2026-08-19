using Viernes.Platform.Windows.Speech.Recognition;
using Viernes.Platform.Windows.Speech.WakeWord;
using Xunit;

namespace Viernes.Core.Tests.Voice;

/// <summary>
/// El corte entre lo que se rescató de la ventana rodante y el pedido.
/// </summary>
public sealed class UtteranceTranscriptTests
{
    private static TranscribedSegment Tramo(string text, double desde, double hasta) =>
        new(text, TimeSpan.FromSeconds(desde), TimeSpan.FromSeconds(hasta), 0.9f);

    /// <summary>Un tramo con las palabras fechadas una por una, que es como llegan de verdad.</summary>
    private static TranscribedSegment TramoConPalabras(double desde, params (string Text, double Desde, double Hasta)[] palabras) =>
        new(
            string.Join(' ', palabras.Select(palabra => palabra.Text)),
            TimeSpan.FromSeconds(desde),
            TimeSpan.FromSeconds(palabras[^1].Hasta),
            0.9f,
            [.. palabras.Select(palabra => new TimedWord(
                palabra.Text,
                TimeSpan.FromSeconds(palabra.Desde),
                TimeSpan.FromSeconds(palabra.Hasta)))]);

    [Fact]
    public void LoQueCierraAntesDelNombreEsRescatado()
    {
        // El caso para el que existe todo esto: venías hablando de otra cosa y la nombraste en el
        // medio. Lo de antes se manda igual, pero se dibuja como lo que es.
        var partes = UtteranceTranscript.Split(
            [
                Tramo("Estaba pensando en el asado.", 0, 3.2),
                Tramo("Che Viernes, anotá que falta carbón.", 3.4, 7.0)
            ],
            preRoll: TimeSpan.FromSeconds(3.4));

        Assert.Equal("Estaba pensando en el asado.", partes.Recovered);
        Assert.Equal("Che Viernes, anotá que falta carbón.", partes.Spoken);
        Assert.Equal(
            "Estaba pensando en el asado. Che Viernes, anotá que falta carbón.",
            partes.Full);
    }

    [Fact]
    public void UnaSolaFraseDeCorridoNoSeParteAunqueElNombreEsteAlFinalDelPreRoll()
    {
        // «Che, necesito que Viernes me abra Spotify» es una cosa sola dicha a alguien. Partirla
        // dibujaría media frase al 40 %, como si la primera mitad no se la hubieran dicho a ella.
        var partes = UtteranceTranscript.Split(
            [Tramo("Che, necesito que Viernes me abra Spotify.", 0, 4.0)],
            preRoll: TimeSpan.FromSeconds(2.5));

        Assert.Equal(string.Empty, partes.Recovered);
        Assert.Equal("Che, necesito que Viernes me abra Spotify.", partes.Spoken);
    }

    [Fact]
    public void SiSoloDijoElNombre_NoQuedaTodoDelLadoRescatado()
    {
        // Decir sólo «Viernes» entra entero adentro del pre-roll. Sin la regla del último tramo, la
        // línea salía completa al 40 %: la burbuja diría «te escuché sin querer» sobre alguien que
        // la acaba de llamar por su nombre.
        var partes = UtteranceTranscript.Split(
            [Tramo("Viernes.", 0, 0.8)],
            preRoll: TimeSpan.FromSeconds(1.5));

        Assert.Equal(string.Empty, partes.Recovered);
        Assert.Equal("Viernes.", partes.Spoken);
    }

    [Fact]
    public void SinPreRoll_TodoEsPedido()
    {
        var partes = UtteranceTranscript.Split(
            [
                Tramo("Viernes.", 0, 0.6),
                Tramo("Abrí Spotify.", 0.7, 2.0)
            ],
            preRoll: TimeSpan.Zero);

        Assert.Equal(string.Empty, partes.Recovered);
        Assert.Equal("Viernes. Abrí Spotify.", partes.Spoken);
    }

    [Fact]
    public void ElTramoQueCruzaElCorteVaDelLadoDelPedido()
    {
        // Un tramo que empieza antes del nombre y termina después lo contiene: es el pedido.
        var partes = UtteranceTranscript.Split(
            [
                Tramo("Bueno.", 0, 0.5),
                Tramo("Viernes creame una carpeta.", 0.6, 3.0)
            ],
            preRoll: TimeSpan.FromSeconds(1.2));

        Assert.Equal("Bueno.", partes.Recovered);
        Assert.Equal("Viernes creame una carpeta.", partes.Spoken);
    }

    [Fact]
    public void SinTramos_NoInventaNada()
    {
        var partes = UtteranceTranscript.Split([], preRoll: TimeSpan.FromSeconds(3));

        Assert.Equal(string.Empty, partes.Recovered);
        Assert.Equal(string.Empty, partes.Spoken);
        Assert.Equal(string.Empty, partes.Full);
    }

    [Fact]
    public void LosTramosVaciosNoDejanEspaciosSueltos()
    {
        var partes = UtteranceTranscript.Split(
            [
                Tramo("  ", 0, 0.3),
                Tramo("Estaba pensando.", 0.4, 2.0),
                Tramo("Viernes, anotá.", 2.1, 3.5)
            ],
            preRoll: TimeSpan.FromSeconds(2.1));

        Assert.Equal("Estaba pensando.", partes.Recovered);
        Assert.Equal("Viernes, anotá.", partes.Spoken);
    }

    [Fact]
    public void ConLasPalabrasFechadas_ElCorteCaeDondeSonoElNombreYNoDondeWhisperCortoElTramo()
    {
        // Medido: «Estaba pensando en el asado. Che Viernes, anotá que falta carbón» sale de Whisper
        // como UN solo tramo de 0 a 4,92 s, con un punto en el medio y todo. Cortando por tramo no se
        // rescataba nunca nada; los horarios son los de la medición.
        var partes = UtteranceTranscript.Split(
            [
                TramoConPalabras(
                    0,
                    ("Estaba", 0.10, 0.46),
                    ("pensando", 0.46, 1.07),
                    ("en", 1.07, 1.22),
                    ("el", 1.22, 1.31),
                    ("asado.", 1.41, 2.00),
                    ("Che,", 2.00, 2.43),
                    ("Viernes,", 2.60, 3.19),
                    ("anotá", 3.19, 3.64),
                    ("que", 3.64, 3.90),
                    ("falta", 3.90, 4.31),
                    ("carbón.", 4.38, 4.90))
            ],
            preRoll: TimeSpan.FromSeconds(3.3),
            phrase: "Viernes");

        Assert.Equal("Estaba pensando en el asado. Che,", partes.Recovered);
        Assert.Equal("Viernes, anotá que falta carbón.", partes.Spoken);
    }

    [Fact]
    public void ElNombreQuedaDelLadoDelPedido()
    {
        // El reconocedor avisa cuando la palabra ya terminó, así que el pre-roll la incluye. Sin
        // moverla, «Viernes» se dibujaba al 40 %: como algo que no le dijeron a ella.
        var partes = UtteranceTranscript.Split(
            [
                TramoConPalabras(
                    0,
                    ("Che", 0.0, 0.4),
                    ("Viernes", 0.4, 1.0),
                    ("anotá", 1.0, 1.6))
            ],
            preRoll: TimeSpan.FromSeconds(1.05),
            phrase: "Viernes");

        Assert.Equal("Che", partes.Recovered);
        Assert.Equal("Viernes anotá", partes.Spoken);
    }

    [Fact]
    public void SinSaberElNombre_NoSeMueveNada()
    {
        var partes = UtteranceTranscript.Split(
            [
                TramoConPalabras(
                    0,
                    ("Che", 0.0, 0.4),
                    ("Viernes", 0.4, 1.0),
                    ("anotá", 1.0, 1.6))
            ],
            preRoll: TimeSpan.FromSeconds(1.05));

        Assert.Equal("Che Viernes", partes.Recovered);
        Assert.Equal("anotá", partes.Spoken);
    }

    [Fact]
    public void ElNombreCompuestoSeMueveEntero()
    {
        var partes = UtteranceTranscript.Split(
            [
                TramoConPalabras(
                    0,
                    ("Bueno,", 0.0, 0.4),
                    ("hola", 0.4, 0.8),
                    ("Viernes,", 0.8, 1.4),
                    ("abrí", 1.4, 1.9))
            ],
            preRoll: TimeSpan.FromSeconds(1.45),
            phrase: "Hola Viernes");

        Assert.Equal("Bueno,", partes.Recovered);
        Assert.Equal("hola Viernes, abrí", partes.Spoken);
    }

}
