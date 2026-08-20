using Viernes.Memory.Brain;
using Xunit;

namespace Viernes.Memory.Tests.Brain;

/// <summary>
/// Lo que contesta el modelo al cerrar una charla, convertido en notas.
/// </summary>
/// <remarks>
/// Es la parte más fácil de que salga mal de todo el cerebro, porque del otro lado hay un modelo
/// contestando texto libre. Casi todas estas pruebas son formas en que un modelo contesta mal
/// habiendo entendido bien, y ninguna de ellas puede costar un aprendizaje.
/// </remarks>
public sealed class BrainLearningTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-aprende-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch (Exception excepcion) when (excepcion is IOException or UnauthorizedAccessException)
        {
            // Limpiar de más no importa.
        }
    }

    private Viernes.Memory.Brain.Brain Nuevo() => new(_carpeta, new RelojQuieto());

    [Fact]
    public void UnaRespuestaComoSePidio_SeGuarda()
    {
        var cerebro = Nuevo();

        var guardadas = cerebro.Learn(
            """
            [{"tipo":"preferencia","titulo":"Trabaja de noche","alcance":"casi siempre",
              "confianza":"alta","cuerpo":"Arranca después de las once y sigue hasta la madrugada.",
              "reemplaza":""}]
            """,
            ["charlas/2026-08-20-031204.md"]);

        Assert.Equal(1, guardadas);

        var nota = cerebro.Read("trabaja-de-noche");
        Assert.NotNull(nota);
        Assert.Equal(BrainNoteKind.Preferencia, nota.Kind);
        Assert.Equal(BrainConfidence.Alta, nota.Confidence);
        Assert.Equal("casi siempre", nota.Scope);
        Assert.Equal("charlas/2026-08-20-031204.md", Assert.Single(nota.Evidence));
    }

    /// <summary>JSON adentro de un bloque de código, con una frase adelante.</summary>
    /// <remarks>
    /// Es como contesta un modelo al que le pediste JSON pelado, y pasa seguido. Perder un
    /// aprendizaje por tres comillas invertidas sería absurdo.
    /// </remarks>
    [Fact]
    public void JsonAdentroDeUnBloqueDeCodigo_SeEntiendeIgual()
    {
        var cerebro = Nuevo();

        var guardadas = cerebro.Learn(
            "Claro, acá va lo que aprendí:\n\n```json\n" +
            """[{"tipo":"aplicacion","titulo":"Spotify tarda en abrir","alcance":"en esta máquina","confianza":"media","cuerpo":"La primera vez del día tarda unos cinco segundos."}]""" +
            "\n```\n¡Espero que sirva!");

        Assert.Equal(1, guardadas);
        Assert.NotNull(cerebro.Read("spotify-tarda-en-abrir"));
    }

    /// <summary>Una sola nota, sin el arreglo alrededor.</summary>
    [Fact]
    public void UnObjetoSueltoSinElArreglo_SeGuardaIgual()
    {
        var cerebro = Nuevo();

        var guardadas = cerebro.Learn(
            """{"tipo":"preferencia","titulo":"Le dice Viernes","cuerpo":"Eligió ese nombre al instalarla."}""");

        Assert.Equal(1, guardadas);
        Assert.NotNull(cerebro.Read("le-dice-viernes"));
    }

    [Fact]
    public void UnaRespuestaQueNoEsJson_NoGuardaNadaYNoTira()
    {
        var cerebro = Nuevo();

        Assert.Equal(0, cerebro.Learn("No encontré nada duradero en esta conversación."));
        Assert.Equal(0, cerebro.Learn(""));
        Assert.Equal(0, cerebro.Learn(null));
        Assert.Equal(0, cerebro.Learn("[[[roto"));
        Assert.Empty(cerebro.All());
    }

    [Fact]
    public void CamposQueFaltanOSonBasura_TomanSuValorPorOmision()
    {
        var cerebro = Nuevo();

        cerebro.Learn("""[{"titulo":"Algo que sabe","cuerpo":"Con lo mínimo indispensable.","tipo":"vaya a saber","confianza":"muchísima"}]""");

        var nota = cerebro.Read("algo-que-sabe");
        Assert.NotNull(nota);
        Assert.Equal(BrainNoteKind.Preferencia, nota.Kind);
        Assert.Equal(BrainConfidence.Media, nota.Confidence);
        Assert.Equal("siempre", nota.Scope);
    }

    [Fact]
    public void UnaNotaVacia_NoEntraAlCerebro()
    {
        var cerebro = Nuevo();

        // Un título de dos letras y un cuerpo de tres es un modelo llenando el formulario.
        Assert.Equal(0, cerebro.Learn("""[{"titulo":"ab","cuerpo":"cde"},{"titulo":"Bien","cuerpo":"x"}]"""));
        Assert.Empty(cerebro.All());
    }

    [Fact]
    public void MasDeTres_SeQuedaConLasTresPrimeras()
    {
        var cerebro = Nuevo();

        var muchas = string.Join(",", Enumerable.Range(1, 8)
            .Select(i => $$"""{"titulo":"Cosa numero {{i}}","cuerpo":"Un cuerpo suficientemente largo."}"""));

        Assert.Equal(3, cerebro.Learn("[" + muchas + "]"));
        Assert.Equal(3, cerebro.All().Count);
    }

    /// <summary>
    /// Corregir algo marca la nota vieja y deja vigente la nueva.
    /// </summary>
    /// <remarks>
    /// Es lo que hace que el cerebro se corrija en vez de acumular dos notas contradictorias, y sólo
    /// puede pasar porque al destilar se le manda al modelo lo que ya sabía.
    /// </remarks>
    [Fact]
    public void CuandoDiceQueReemplazaAOtra_LaViejaQuedaMarcada()
    {
        var cerebro = Nuevo();
        cerebro.Learn("""[{"titulo":"Toma el café con azúcar","cuerpo":"Pareció pedirlo así una vez."}]""");

        cerebro.Learn(
            """[{"titulo":"Toma el café sin azúcar","cuerpo":"Lo corrigió él mismo.","reemplaza":"Toma el café con azúcar"}]""");

        Assert.Equal(BrainStatus.Reemplazada, cerebro.Read("toma-el-cafe-con-azucar")!.Status);
        Assert.Equal(BrainStatus.Vigente, cerebro.Read("toma-el-cafe-sin-azucar")!.Status);
    }

    /// <summary>
    /// Que se reemplace a sí misma no parte la nota en dos.
    /// </summary>
    /// <remarks>
    /// El modelo repite el título en «reemplaza» con facilidad: le pediste el título de la nota vieja
    /// y la nueva se llama casi igual.
    /// <para>
    /// <b>Esta prueba estaba mal escrita y una rotura deliberada lo mostró.</b> Decía lo mismo que
    /// dice ahora pero sobre un cerebro vacío, y ahí no hay nada que reemplazar: quitando el freno
    /// seguía en verde. El caso que duele es con la nota YA guardada y movida de carpeta a mano —que
    /// es a lo que se invita al usuario—: ahí «marcá la vieja y guardá la nueva» escribe dos archivos
    /// con el mismo nombre en carpetas distintas, uno vencido y otro vigente, y cuál de los dos se
    /// lee después depende del orden en que el sistema devuelva los archivos.
    /// </para>
    /// </remarks>
    [Fact]
    public void SiDiceQueSeReemplazaASiMisma_NoQuedanDosArchivosConElMismoNombre()
    {
        var cerebro = Nuevo();
        cerebro.Learn("""[{"titulo":"Trabaja de noche","cuerpo":"Después de las once."}]""");

        // El usuario la reorganiza, que es lo que se le pide que pueda hacer.
        var original = Path.Combine(cerebro.Root, cerebro.Read("trabaja-de-noche")!.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var movida = Path.Combine(cerebro.KnowledgeFolder, "rutinas", "trabaja-de-noche.md");
        Directory.CreateDirectory(Path.GetDirectoryName(movida)!);
        File.Move(original, movida);

        cerebro.Learn(
            """[{"titulo":"Trabaja de noche","cuerpo":"Arranca a las once y sigue hasta tarde.","reemplaza":"Trabaja de noche"}]""");

        var archivos = Directory.GetFiles(cerebro.KnowledgeFolder, "trabaja-de-noche.md", SearchOption.AllDirectories);
        Assert.Single(archivos);
        Assert.Equal(BrainStatus.Vigente, cerebro.Read("trabaja-de-noche")!.Status);
    }

    private sealed class RelojQuieto : TimeProvider
    {
        private readonly DateTimeOffset _ahora = new(2026, 8, 20, 3, 12, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
