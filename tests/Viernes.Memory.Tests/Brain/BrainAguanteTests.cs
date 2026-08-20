using Viernes.Memory.Brain;
using Xunit;

namespace Viernes.Memory.Tests.Brain;

/// <summary>
/// Lo que el cerebro tiene que aguantar sin perder nada.
/// </summary>
/// <remarks>
/// Todas estas pruebas salieron de una auditoría adversarial sobre el cerebro recién escrito. No son
/// casos raros: son un modelo contestando con una frase de cortesía, dos charlas cerrando a la vez,
/// un usuario editando un archivo a mano. Lo que se pierde acá no se puede volver a deducir de nada,
/// así que perder en silencio es el peor resultado posible.
/// </remarks>
public sealed class BrainAguanteTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-aguante-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// Un corchete suelto en la prosa no se lleva puestas todas las notas.
    /// </summary>
    /// <remarks>
    /// Encontrar un arreglo hacía <c>return</c> aunque estuviera vacío, así que un «te dejo [lo que
    /// aprendí]» antes del JSON tiraba la charla entera y el respaldo del objeto suelto —el error de
    /// formato más común— nunca corría.
    /// </remarks>
    [Fact]
    public void UnCorcheteSueltoEnLaProsa_NoSeLlevaLasNotas()
    {
        var cerebro = Nuevo();

        var guardadas = cerebro.Learn(
            "Listo, te dejo [lo que aprendí] acá abajo:\n" +
            """{"titulo":"Trabaja de noche","cuerpo":"Arranca después de las once."}""");

        Assert.Equal(1, guardadas);
        Assert.NotNull(cerebro.Read("trabaja-de-noche"));
    }

    [Fact]
    public void UnCorcheteAdentroDelCuerpo_NoRompeElArreglo()
    {
        var cerebro = Nuevo();

        var guardadas = cerebro.Learn(
            """[{"titulo":"Usa corchetes","cuerpo":"Escribe cosas como [esto] y ]aquello[ sin problema."}]""");

        Assert.Equal(1, guardadas);
        Assert.Contains("[esto]", cerebro.Read("usa-corchetes")!.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Volver a aprender un título ya reemplazado no lo resucita.
    /// </summary>
    /// <remarks>
    /// Guardar pisaba el archivo y lo dejaba vigente otra vez, borrando la evidencia de lo que creía
    /// antes. Es justo lo que Supersede promete que no puede pasar, y lo contrario de «nada se borra
    /// al corregir».
    /// </remarks>
    [Fact]
    public void VolverAAprenderAlgoYaReemplazado_NoLoResucita()
    {
        var cerebro = Nuevo();
        cerebro.Learn("""[{"titulo":"Toma el café con azúcar","cuerpo":"Pareció pedirlo así."}]""");
        cerebro.Learn(
            """[{"titulo":"Toma el café sin azúcar","cuerpo":"Lo corrigió él.","reemplaza":"Toma el café con azúcar"}]""");

        // Y en otra charla el modelo vuelve a decir lo viejo.
        cerebro.Learn("""[{"titulo":"Toma el café con azúcar","cuerpo":"Volvió a parecer eso."}]""");

        var vieja = cerebro.Read("toma-el-cafe-con-azucar");
        Assert.NotNull(vieja);
        Assert.Equal(BrainStatus.Reemplazada, vieja.Status);
        Assert.Contains("Pareció pedirlo así", vieja.Body, StringComparison.Ordinal);
    }

    /// <summary>Dos títulos distintos que dan el mismo nombre de archivo no se pisan.</summary>
    /// <remarks>
    /// El nombre se recorta a sesenta caracteres y tira todo lo que no sea letra o número, así que
    /// pasa. La víctima puede ser una nota vieja de otra charla que no tenía nada que ver.
    /// </remarks>
    [Fact]
    public void DosTitulosQueColapsanEnElMismoNombre_NoSePisan()
    {
        var cerebro = Nuevo();

        // Sesenta caracteres iguales y la diferencia DESPUÉS del recorte: los dos títulos dan el
        // mismo nombre de archivo. Con menos de sesenta no colisionan, y una prueba que no colisiona
        // no prueba nada — la primera versión de esto pasaba con el arreglo sacado.
        var largo = new string('a', 60);
        Assert.Equal(
            Viernes.Memory.Brain.Brain.Slug(largo + " primero"),
            Viernes.Memory.Brain.Brain.Slug(largo + " segundo"));

        cerebro.Save(cerebro.Note(BrainNoteKind.Preferencia, largo + " primero", "Lo primero."));
        cerebro.Save(cerebro.Note(BrainNoteKind.Preferencia, largo + " segundo", "Lo segundo."));

        var cuerpos = cerebro.All().Select(nota => nota.Body).ToArray();
        Assert.Equal(2, cerebro.All().Count);
        Assert.Contains("Lo primero.", cuerpos);
        Assert.Contains("Lo segundo.", cuerpos);
    }

    /// <summary>Una nota sin el cierre de cabecera conserva su cuerpo.</summary>
    /// <remarks>
    /// Se invita al usuario a editar a mano, así que va a haber archivos con la cabecera abierta.
    /// Sin esto, todas las líneas de abajo se leían como campos: la nota quedaba en el índice
    /// diciendo que sabía algo y adentro no había nada.
    /// </remarks>
    [Fact]
    public void SinElCierreDeCabecera_ElCuerpoNoSePierde()
    {
        var cerebro = Nuevo();
        var nota = cerebro.Note(BrainNoteKind.Preferencia, "Trabaja de noche", "Después de las once.");
        cerebro.Save(nota);

        var archivo = Path.Combine(cerebro.Root, nota.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(archivo, "---\ntipo: preferencia\ntitulo: Trabaja de noche\n\nDespués de las once.\n");

        Assert.Contains("Después de las once.", cerebro.Read(nota.Name)!.Body, StringComparison.Ordinal);
    }

    /// <summary>Un título con saltos de línea no inyecta campos en la cabecera.</summary>
    /// <remarks>
    /// La cabecera es «clave: valor» por renglón y no escapa nada; al leer gana el último valor, así
    /// que un salto de línea podía cambiar campos escritos antes —el tipo, la versión del formato— y
    /// además truncaba el título a su primera línea.
    /// </remarks>
    [Fact]
    public void UnTituloConSaltosDeLinea_NoInyectaCampos()
    {
        var cerebro = Nuevo();

        cerebro.Save(cerebro.Note(
            BrainNoteKind.Aplicacion,
            "Spotify tarda\ntipo: preferencia\nesquema: 99",
            "Unos cinco segundos."));

        var nota = Assert.Single(cerebro.All());
        Assert.Equal(BrainNoteKind.Aplicacion, nota.Kind);
        Assert.Contains("esquema: 99", nota.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dos charlas cerrando a la vez no se pierden notas.
    /// </summary>
    /// <remarks>
    /// Cerrar una conversación destila en una tarea aparte, y dos charlas pueden cerrar juntas
    /// —hablando y escribiendo, o dos ventanas—. El índice se rehace leyendo el disco entero, así
    /// que sin candado una podía escribir el índice mientras la otra escribía su nota.
    /// </remarks>
    [Fact]
    public void DosCharlasAprendiendoALaVez_NoSePierdeNinguna()
    {
        var cerebro = Nuevo();

        Parallel.For(0, 24, i =>
            cerebro.Save(cerebro.Note(BrainNoteKind.Preferencia, $"Cosa numero {i}", $"El cuerpo de la {i}.")));

        Assert.Equal(24, cerebro.All().Count);

        var indice = File.ReadAllText(cerebro.IndexPath);
        for (var i = 0; i < 24; i++)
        {
            Assert.Contains($"Cosa numero {i}", indice, StringComparison.Ordinal);
        }
    }

    private sealed class RelojQuieto : TimeProvider
    {
        private readonly DateTimeOffset _ahora = new(2026, 8, 20, 3, 12, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
