using Viernes.Memory.Brain;
using Xunit;

namespace Viernes.Memory.Tests.Brain;

/// <summary>
/// El cerebro: lo que sabe, en archivos de texto que se pueden abrir y corregir.
/// </summary>
/// <remarks>
/// Reemplaza a un <c>.json</c> con tope de quinientas notas de quinientos caracteres. Lo que se
/// prueba acá es lo que lo hace un cerebro y no una libreta: que sobreviva a que lo editen a mano,
/// que corregir no borre la evidencia, y que el índice sea siempre lo que hay en la carpeta.
/// </remarks>
public sealed class BrainTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-cerebro-" + Guid.NewGuid().ToString("N"));

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
    public void UnaNota_SeGuardaYSeVuelveALeerIgual()
    {
        var cerebro = Nuevo();
        var nota = cerebro.Note(
            BrainNoteKind.Preferencia,
            "Toma el café sin azúcar",
            "Siempre lo pidió así, y una vez corrigió cuando se lo trajeron endulzado.",
            scope: "siempre",
            confidence: BrainConfidence.Alta,
            evidence: ["charlas/2026-08-20-031204.md"]);

        cerebro.Save(nota);
        var leida = cerebro.Read(nota.Name);

        Assert.NotNull(leida);
        Assert.Equal("Toma el café sin azúcar", leida.Title);
        Assert.Equal(BrainNoteKind.Preferencia, leida.Kind);
        Assert.Equal(BrainConfidence.Alta, leida.Confidence);
        Assert.Equal(BrainStatus.Vigente, leida.Status);
        Assert.Equal("charlas/2026-08-20-031204.md", Assert.Single(leida.Evidence));
        Assert.Contains("endulzado", leida.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ElNombreDelArchivo_SeLeeDesdeElExplorador()
    {
        Assert.Equal("toma-el-cafe-sin-azucar", Viernes.Memory.Brain.Brain.Slug("Toma el café sin azúcar"));
        Assert.Equal("el-boton-de-guardar-esta-en-archivo", Viernes.Memory.Brain.Brain.Slug("El botón de guardar está en «Archivo»"));
        Assert.Equal("nota", Viernes.Memory.Brain.Brain.Slug("¿¡...!?"));
    }

    [Fact]
    public void ElIndice_ListaLoQueHayConEnlacesQueApuntanAAlgo()
    {
        var cerebro = Nuevo();
        cerebro.Save(cerebro.Note(BrainNoteKind.Preferencia, "Trabaja de noche", "Casi siempre después de las once."));
        cerebro.Save(cerebro.Note(BrainNoteKind.Aplicacion, "Spotify tarda en abrir", "Unos cinco segundos la primera vez."));

        var indice = File.ReadAllText(cerebro.IndexPath);

        Assert.Contains("Trabaja de noche", indice, StringComparison.Ordinal);
        Assert.Contains("Spotify tarda en abrir", indice, StringComparison.Ordinal);

        // Y los enlaces tienen que llevar a un archivo que exista, no a uno que suene bien.
        foreach (var nota in cerebro.All())
        {
            Assert.True(
                File.Exists(Path.Combine(cerebro.Root, nota.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                $"el índice enlaza a {nota.RelativePath} y ahí no hay nada");
            Assert.Contains(nota.RelativePath, indice, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Borrar una nota a mano deja el índice al día.
    /// </summary>
    /// <remarks>
    /// <b>Es la mitad de por qué esto es Markdown.</b> Se invita al usuario a abrir la carpeta y
    /// borrar lo que no quiere que sepa; si el índice se armara de lo que el programa cree tener,
    /// quedaría enlazando a un archivo borrado y diciendo que sabe algo que ya no sabe.
    /// </remarks>
    [Fact]
    public void BorrarUnaNotaAMano_DejaElIndiceAlDia()
    {
        var cerebro = Nuevo();
        var queda = cerebro.Note(BrainNoteKind.Preferencia, "Trabaja de noche", "Después de las once.");
        var borrada = cerebro.Note(BrainNoteKind.Preferencia, "Odia el cilantro", "Lo dijo una vez.");
        cerebro.Save(queda);
        cerebro.Save(borrada);

        File.Delete(Path.Combine(cerebro.Root, borrada.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        cerebro.Reindex();

        var indice = File.ReadAllText(cerebro.IndexPath);
        Assert.Contains("Trabaja de noche", indice, StringComparison.Ordinal);
        Assert.DoesNotContain("Odia el cilantro", indice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Corregir no borra la evidencia de lo que creía antes.
    /// </summary>
    /// <remarks>
    /// Entender por qué se equivocó es la mitad de lo que hace que no se vuelva a equivocar. Una
    /// corrección sin la creencia que corrige es una corrección sin causa.
    /// </remarks>
    [Fact]
    public void CorregirAlgo_NoBorraLoQueCreiaAntes()
    {
        var cerebro = Nuevo();
        var vieja = cerebro.Note(BrainNoteKind.Preferencia, "Toma el café con azúcar", "Pareció pedirlo así.");
        cerebro.Save(vieja);

        var nueva = cerebro.Note(BrainNoteKind.Preferencia, "Toma el café sin azúcar", "Lo corrigió él mismo.");
        cerebro.Supersede(vieja.Name, nueva);

        var anterior = cerebro.Read(vieja.Name);
        Assert.NotNull(anterior);
        Assert.Equal(BrainStatus.Reemplazada, anterior.Status);

        var vigente = cerebro.Read(nueva.Name);
        Assert.NotNull(vigente);
        Assert.Equal(vieja.Name, vigente.Supersedes);

        // Y el índice muestra sólo la que vale, pero dice que la otra sigue ahí.
        var indice = File.ReadAllText(cerebro.IndexPath);
        Assert.Contains("Toma el café sin azúcar", indice, StringComparison.Ordinal);
        Assert.DoesNotContain("- [Toma el café con azúcar]", indice, StringComparison.Ordinal);
        Assert.Contains("dejaron de valer", indice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Una nota que el usuario rompió editándola no se pierde.
    /// </summary>
    /// <remarks>
    /// Va a pasar: se lo invita a editar a mano. Es la misma decisión que se tomó con las
    /// preferencias después de que un campo ilegible se llevara puesto el archivo entero — sólo que
    /// acá lo que se pierde no se puede volver a deducir de nada.
    /// </remarks>
    [Fact]
    public void UnaNotaRotaAMano_SeLeeIgualConLoQueSeEntienda()
    {
        var cerebro = Nuevo();
        var nota = cerebro.Note(BrainNoteKind.Preferencia, "Trabaja de noche", "Después de las once.");
        cerebro.Save(nota);

        var archivo = Path.Combine(cerebro.Root, nota.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(
            archivo,
            "---\ntipo: esto no es un tipo\nconfianza: muchísima\ntitulo: Trabaja de noche\n---\n\nDespués de las once.\n");

        var leida = cerebro.Read(nota.Name);

        Assert.NotNull(leida);
        Assert.Equal("Trabaja de noche", leida.Title);
        Assert.Contains("Después de las once.", leida.Body, StringComparison.Ordinal);
        Assert.Equal(BrainConfidence.Media, leida.Confidence);
    }

    /// <summary>Mover una nota de carpeta con el explorador alcanza para reorganizarla.</summary>
    /// <remarks>
    /// Era parte del pedido: que las carpetas las pueda ir armando ella, y que el usuario también.
    /// Si la carpeta se dedujera del tipo, mover el archivo no cambiaría nada y el índice mentiría.
    /// </remarks>
    [Fact]
    public void MoverUnaNotaDeCarpeta_LaReorganiza()
    {
        var cerebro = Nuevo();
        var nota = cerebro.Note(BrainNoteKind.Preferencia, "Trabaja de noche", "Después de las once.");
        cerebro.Save(nota);

        var desde = Path.Combine(cerebro.Root, nota.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var hacia = Path.Combine(cerebro.KnowledgeFolder, "rutinas", nota.Name + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(hacia)!);
        File.Move(desde, hacia);
        cerebro.Reindex();

        Assert.Equal("rutinas", cerebro.Read(nota.Name)!.Folder);
        Assert.Contains("## rutinas", File.ReadAllText(cerebro.IndexPath), StringComparison.Ordinal);
    }

    [Fact]
    public void UnaClaveEnLoQueAprende_NoLlegaAlDisco()
    {
        var cerebro = Nuevo();
        var clave = Viernes.Memory.Tests.Privacy.CredencialesDeMentira.Falsa("sk-" + "or-" + "v1-", 40, 'b');
        var nota = cerebro.Note(BrainNoteKind.Preferencia, "Usa OpenRouter", $"Su clave es {clave}");
        cerebro.Save(nota);

        var texto = File.ReadAllText(
            Path.Combine(cerebro.Root, nota.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.DoesNotContain(clave, texto, StringComparison.Ordinal);
        Assert.Contains("credencial", texto, StringComparison.Ordinal);
    }

    [Fact]
    public void UnCerebroVacio_LoDice()
    {
        var cerebro = Nuevo();
        cerebro.Reindex();

        Assert.Contains("Todavía no sé nada", File.ReadAllText(cerebro.IndexPath), StringComparison.Ordinal);
        Assert.Empty(cerebro.All());
    }

    private sealed class RelojQuieto : TimeProvider
    {
        private readonly DateTimeOffset _ahora = new(2026, 8, 20, 3, 12, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
