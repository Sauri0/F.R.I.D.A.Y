using System.Globalization;
using System.Text.Json;
using Viernes.Core.Tools;
using Viernes.Core.Tools.BuiltIn;
using Xunit;

namespace Viernes.Core.Tests.Tools;

/// <summary>
/// Cubre la diferencia entre archivo y carpeta, que es donde la herramienta mentía.
/// </summary>
/// <remarks>
/// Sin acción para carpetas, «creame una carpeta» terminaba en <c>escribir</c> y creaba un archivo
/// sin extensión con ese nombre; el mensaje de éxito no distinguía uno de otro, así que el engaño
/// recién aparecía al intentar abrirla. Estas pruebas fijan las dos mitades: que crear una carpeta
/// cree una carpeta de verdad, y que escribir se niegue cuando lo que le piden es claramente una.
/// </remarks>
public sealed class FileSystemToolTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "viernes-tests-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task CrearCarpeta_DejaUnaCarpetaYNoUnArchivo()
    {
        var target = Path.Combine(this.root, "Proyecto");

        var result = await RunAsync("carpeta", target);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(Directory.Exists(target));
        Assert.False(File.Exists(target));
        Assert.Contains("carpeta", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrearCarpeta_SobreUnaQueYaEstaNoFalla()
    {
        var target = Path.Combine(this.root, "Proyecto");
        await RunAsync("carpeta", target);

        var result = await RunAsync("carpeta", target);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Escribir_SobreUnaCarpetaSeNiega()
    {
        var target = Path.Combine(this.root, "Proyecto");
        Directory.CreateDirectory(target);

        var result = await RunAsync("escribir", target, "algo");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Escribir_SinExtensionNiContenidoNoInventaUnArchivo()
    {
        var target = Path.Combine(this.root, "Parece Una Carpeta");

        var result = await RunAsync("escribir", target);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(File.Exists(target));
        Assert.Contains("accion=carpeta", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// El peor bug que tuvo esta herramienta. «Mové el informe a Trabajo», con Trabajo inexistente,
    /// renombraba <c>informe.docx</c> a un archivo llamado «Trabajo» sin extensión y contestaba que
    /// lo había movido: el archivo seguía existiendo, sin nombre reconocible ni programa que lo
    /// abriera, que es la forma más difícil de notar que se perdió algo.
    /// </remarks>
    [Fact]
    public async Task Mover_ACarpetaInexistente_LaCreaYNoRenombraElArchivo()
    {
        var origen = Path.Combine(this.root, "informe.docx");
        Directory.CreateDirectory(this.root);
        await File.WriteAllTextAsync(origen, "importante");
        var carpeta = Path.Combine(this.root, "Trabajo");

        var result = await RunAsync("mover", origen, destination: carpeta);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(Directory.Exists(carpeta));
        Assert.False(File.Exists(carpeta));
        Assert.Equal("importante", await File.ReadAllTextAsync(
            Path.Combine(carpeta, "informe.docx")));
    }

    [Fact]
    public async Task Mover_ADestinoConExtension_SigueSiendoUnRenombre()
    {
        var origen = Path.Combine(this.root, "nota.txt");
        Directory.CreateDirectory(this.root);
        await File.WriteAllTextAsync(origen, "x");
        var destino = Path.Combine(this.root, "copia.txt");

        await RunAsync("mover", origen, destination: destino);

        Assert.True(File.Exists(destino));
        Assert.False(Directory.Exists(destino));
    }

    [Fact]
    public async Task Copiar_SobreAlgoExistente_GuardaLoPisado()
    {
        Directory.CreateDirectory(this.root);
        var origen = Path.Combine(this.root, "nuevo.txt");
        var destino = Path.Combine(this.root, "viejo.txt");
        await File.WriteAllTextAsync(origen, "nuevo");
        await File.WriteAllTextAsync(destino, "el que se pisa");

        var result = await RunAsync("copiar", origen, destination: destino);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Contains("papelera", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// La herramienta prometía «se puede recuperar» en cada borrado y la acción no existía: la única
    /// forma era abrir la papelera a mano y adivinar cuál de las carpetas con fecha era.
    /// </remarks>
    [Fact]
    public async Task Recuperar_SinRutaListaLoQueHay()
    {
        var result = await RunAsync("recuperar", path: null);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Recuperar_LoQueNoEstaFallaEnVezDeInventar()
    {
        var result = await RunAsync("recuperar", "no-existe-ningun-archivo-asi.txt");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Abrir_LoQueNoExisteFallaEnVezDeAbrirOtraCosa()
    {
        var result = await RunAsync("abrir", Path.Combine(this.root, "Fantasma"));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("escritorio\\Sub")]
    [InlineData("escritorio/Sub")]
    public async Task RutaHablada_TambienFuncionaComoPrefijo(string spoken)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Sub");

        try
        {
            var result = await RunAsync("carpeta", spoken);

            Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            Directory.Delete(expected, recursive: true);
        }
    }

    [Fact]
    public async Task RutaRelativa_CaeEnElEscritorioYNoJuntoAlEjecutable()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SueltaViernes");

        try
        {
            await RunAsync("carpeta", "SueltaViernes");

            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            Directory.Delete(expected, recursive: true);
        }
    }

    /// <remarks>
    /// Escribir sólo comprobaba que el archivo existiera, nunca qué había quedado adentro: un
    /// archivo vacío o truncado contestaba «Creé el archivo» igual. Esta prueba fija que lo que se
    /// afirma es lo que está en el disco.
    /// </remarks>
    [Fact]
    public async Task Escribir_DejaExactamenteElContenidoPedido()
    {
        var target = Path.Combine(this.root, "nota.txt");

        var result = await RunAsync("escribir", target, "primera línea con acentos: ñandú");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal("primera línea con acentos: ñandú", await File.ReadAllTextAsync(target));
    }

    /// <remarks>
    /// «3. leche- comprar pan». El modelo manda «\n- comprar pan» para hacer renglón nuevo, pero
    /// JsonToolArguments le hace Trim() al contenido y se come justo ese salto; AppendAllText tampoco
    /// ponía separador, así que el ítem nuevo quedaba pegado al último de la lista.
    /// </remarks>
    [Fact]
    public async Task Agregar_NoPegaElTextoNuevoAlUltimoRenglon()
    {
        Directory.CreateDirectory(this.root);
        var target = Path.Combine(this.root, "lista.txt");
        await File.WriteAllTextAsync(target, "1. pan\r\n2. leche");

        var result = await RunAsync("agregar", target, "\n3. yerba");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        var lines = await File.ReadAllLinesAsync(target);
        Assert.Equal(["1. pan", "2. leche", "3. yerba"], lines);
    }

    /// <remarks>
    /// La otra mitad del arreglo anterior: si el archivo ya termina en salto, agregar otro dejaría un
    /// renglón en blanco de más. El separador se pone sólo cuando falta.
    /// </remarks>
    [Fact]
    public async Task Agregar_SobreArchivoQueYaTerminaEnSaltoNoDejaRenglonVacio()
    {
        Directory.CreateDirectory(this.root);
        var target = Path.Combine(this.root, "lista.txt");
        await File.WriteAllTextAsync(target, "1. pan\r\n");

        await RunAsync("agregar", target, "2. leche");

        Assert.Equal(["1. pan", "2. leche"], await File.ReadAllLinesAsync(target));
    }

    [Fact]
    public async Task Agregar_SobreUnArchivoQueNoExisteNoEmpiezaConRenglonVacio()
    {
        var target = Path.Combine(this.root, "nueva.txt");

        var result = await RunAsync("agregar", target, "primera");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal("primera", await File.ReadAllTextAsync(target));
    }

    /// <remarks>
    /// File.ReadAllText no se fija en qué está leyendo: un PDF entraba como texto, el estado salía
    /// Succeeded y el modelo resumía el ruido del binario como si fuera el documento.
    /// </remarks>
    [Fact]
    public async Task Leer_UnBinarioFallaEnVezDeDevolverRuido()
    {
        Directory.CreateDirectory(this.root);
        var target = Path.Combine(this.root, "documento.pdf");
        await File.WriteAllBytesAsync(target, [0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0x00, 0x7F]);

        var result = await RunAsync("leer", target);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("binario", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leer_TextoConAcentosNoSeConfundeConUnBinario()
    {
        Directory.CreateDirectory(this.root);
        var target = Path.Combine(this.root, "carta.txt");
        await File.WriteAllTextAsync(target, "señor: ¿cómo anda? — ñandú");

        var result = await RunAsync("leer", target);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Contains("ñandú", result.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// El cupo se repartía por orden de llegada: Take(200) para carpetas y Take(200 - cuántas hubo)
    /// para archivos. Con 200 subcarpetas el segundo quedaba en Take(0) y la respuesta no mostraba
    /// ningún archivo ni decía que había recortado, así que «no hay archivos» y «no entraron» se
    /// leían igual.
    /// </remarks>
    [Fact]
    public async Task Listar_ConDemasiadasCarpetasIgualMuestraArchivos()
    {
        Directory.CreateDirectory(this.root);
        for (var index = 0; index < 205; index++)
        {
            Directory.CreateDirectory(Path.Combine(
                this.root, "carpeta-" + index.ToString("D3", CultureInfo.InvariantCulture)));
        }

        await File.WriteAllTextAsync(Path.Combine(this.root, "archivo-a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(this.root, "archivo-b.txt"), "b");

        var result = await RunAsync("listar", this.root);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Contains("archivo-a.txt", result.Message, StringComparison.Ordinal);
        Assert.Contains("archivo-b.txt", result.Message, StringComparison.Ordinal);
        Assert.Contains("207", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listar_CuandoEntraTodoNoInventaUnTruncado()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "una"));
        await File.WriteAllTextAsync(Path.Combine(this.root, "uno.txt"), "1");

        var result = await RunAsync("listar", this.root);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.DoesNotContain("más que no entran", result.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Take(40) y después «Encontré {found.Length}»: con 300 coincidencias contestaba «Encontré 40»,
    /// que el modelo repetía como si fuera el total.
    /// </remarks>
    [Fact]
    public async Task Buscar_DiceElTotalRealYNoLoQueAlcanzaAMostrar()
    {
        Directory.CreateDirectory(this.root);
        for (var index = 0; index < 45; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(this.root, $"coincide-{index.ToString("D2", CultureInfo.InvariantCulture)}.txt"),
                "x");
        }

        var result = await RunAsync("buscar", this.root, "coincide");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Contains("45", result.Message, StringComparison.Ordinal);
        Assert.Contains("primeros 40", result.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Usaba EnumerateFileSystemEntries, que trae carpetas, mientras la acción dice «busca archivos
    /// por nombre»: la lista traía cosas que después no se podían leer.
    /// </remarks>
    [Fact]
    public async Task Buscar_NoDevuelveCarpetas()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "coincide-carpeta"));
        await File.WriteAllTextAsync(Path.Combine(this.root, "coincide-archivo.txt"), "x");

        var result = await RunAsync("buscar", this.root, "coincide");

        Assert.Contains("coincide-archivo.txt", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("coincide-carpeta", result.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// La papelera propia no la podaba nadie: cada borrado —y cada reemplazo, que también guarda
    /// copia— dejaba una carpeta con fecha y ninguna se iba nunca.
    /// </remarks>
    [Fact]
    public async Task Papelera_SePodaLoQuePasoLosTreintaDias()
    {
        var papelera = Path.Combine(this.root, "papelera");
        var ahora = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Local);
        var vieja = Path.Combine(papelera, ahora.AddDays(-31).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        var nueva = Path.Combine(papelera, ahora.AddDays(-2).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        var ajena = Path.Combine(papelera, "algo-que-no-es-nuestro");
        Directory.CreateDirectory(vieja);
        Directory.CreateDirectory(nueva);
        Directory.CreateDirectory(ajena);
        await File.WriteAllTextAsync(Path.Combine(vieja, "viejo.txt"), "x");

        var podadas = FileSystemTool.PruneRecycle(papelera, ahora);

        Assert.Equal(1, podadas);
        Assert.False(Directory.Exists(vieja));
        Assert.True(Directory.Exists(nueva));
        Assert.True(Directory.Exists(ajena));
    }

    /// <remarks>
    /// La poda se dispara al guardar algo nuevo en la papelera y no con un temporizador: nada
    /// debería borrar archivos mientras nadie está pidiendo nada.
    /// </remarks>
    [Fact]
    public async Task Papelera_SePodaAlMandarAlgoNuevoYNoConUnTemporizador()
    {
        Directory.CreateDirectory(this.root);
        var condenado = Path.Combine(this.root, "chau.txt");
        await File.WriteAllTextAsync(condenado, "x");

        // Papelera propia de la prueba. Apuntar a la real haría que correr la suite borrara archivos
        // verdaderos del usuario: no hoy, sino dentro de unos meses, en silencio, cuando algo ahí
        // pase los treinta días. Una prueba nunca puede destruir datos de quien la corre.
        var papelera = Path.Combine(this.root, "papelera");
        var vencida = Path.Combine(
            papelera,
            DateTime.Now.AddDays(-45).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(vencida);
        await File.WriteAllTextAsync(Path.Combine(vencida, "olvidado.txt"), "x");

        var result = await RunAsync("borrar", condenado, recycleRoot: papelera);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.False(Directory.Exists(vencida));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    /// <remarks>
    /// La papelera por defecto es la de la prueba, no la del usuario. Cualquier borrado o
    /// sobrescritura de acá adentro guarda una copia, y sin esto la suite iba dejando carpetas en la
    /// papelera real de quien la corre —y, con la poda nueva, algún día también borrando de ahí—.
    /// </remarks>
    private async Task<ToolExecutionResult> RunAsync(
        string action,
        string? path = null,
        string? content = null,
        string? destination = null,
        string? recycleRoot = null)
    {
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>(
            new[]
            {
                new KeyValuePair<string, string>("accion", action),
                new KeyValuePair<string, string>("ruta", path!),
                new KeyValuePair<string, string>("contenido", content!),
                new KeyValuePair<string, string>("destino", destination!)
            }.Where(pair => pair.Value is not null)));

        return await new FileSystemTool(recycleRoot ?? Path.Combine(this.root, "papelera")).ExecuteAsync(
            arguments, new ToolExecutionContext("t1"), CancellationToken.None);
    }
}
