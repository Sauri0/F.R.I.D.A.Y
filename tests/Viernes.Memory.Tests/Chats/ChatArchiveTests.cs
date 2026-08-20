using Viernes.Memory.Chats;
using Xunit;

namespace Viernes.Memory.Tests.Chats;

/// <summary>
/// Que cada charla quede escrita, y escrita mientras pasa.
/// </summary>
/// <remarks>
/// Hasta acá no se guardaba ninguna: los turnos vivían en una lista en memoria —y sólo los de la
/// persona— y se tiraban al cerrar. Todo lo que viene después —destilar, indexar, aprender— se
/// apoya en que esto no se pierda.
/// </remarks>
public sealed class ChatArchiveTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(
        Path.GetTempPath(),
        "viernes-charlas-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void UnaCharla_QuedaEscritaConLosDosLados()
    {
        using (var charla = ChatArchive.Open(_carpeta, "hablando"))
        {
            charla.Note(ChatVoice.Persona, "cuánto falta para las tres");
            charla.Note(ChatVoice.Ella, "Faltan dos horas y veinte.");
            charla.Close();
        }

        var texto = File.ReadAllText(Directory.GetFiles(_carpeta, "*.md").Single());

        Assert.Contains("camino: hablando", texto, StringComparison.Ordinal);
        Assert.Contains("**vos**", texto, StringComparison.Ordinal);
        Assert.Contains("cuánto falta para las tres", texto, StringComparison.Ordinal);
        Assert.Contains("**ella**", texto, StringComparison.Ordinal);
        Assert.Contains("Faltan dos horas y veinte.", texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lo dicho está en disco antes de que la charla cierre.
    /// </summary>
    /// <remarks>
    /// <b>Es la razón de ser de todo esto.</b> Guardar al final garantiza perder justo las charlas
    /// que hay que poder revisar: la que se colgó, la que se cortó, la que terminó con la aplicación
    /// muerta. Si esta prueba se cae, el archivo dejó de servir para lo que se hizo.
    /// </remarks>
    [Fact]
    public void LoDicho_EstaEnDiscoAntesDeCerrar()
    {
        using var charla = ChatArchive.Open(_carpeta, "escribiendo");
        charla.Note(ChatVoice.Persona, "acordate de esto");

        var archivo = charla.Path;
        var limite = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < limite)
        {
            if (Leer(archivo).Contains("acordate de esto", StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail("lo dicho no llegó al disco con la charla todavía abierta");
    }

    [Fact]
    public void UnaCharlaEnLaQueNoSeDijoNada_NoDejaArchivo()
    {
        // Tocar el orbe sin decir nada abre y cierra una. Una carpeta llena de archivos vacíos hace
        // que la carpeta deje de servir.
        var charla = ChatArchive.Open(_carpeta, "hablando");
        charla.Close();

        Assert.Empty(Directory.GetFiles(_carpeta, "*.md"));
    }

    [Fact]
    public void LoVacio_NoCuentaComoTurno()
    {
        var charla = ChatArchive.Open(_carpeta, "hablando");
        charla.Note(ChatVoice.Persona, "   ");
        charla.Note(ChatVoice.Ella, null);

        Assert.Equal(0, charla.Turns);

        charla.Close();
        Assert.Empty(Directory.GetFiles(_carpeta, "*.md"));
    }

    /// <summary>Dos charlas en el mismo segundo no se pisan.</summary>
    /// <remarks>
    /// Pasa de verdad: cerrarla y volver a llamarla enseguida. Pisar la primera sería perderla
    /// entera, y sin ruido.
    /// </remarks>
    [Fact]
    public void DosCharlasEnElMismoSegundo_NoSePisan()
    {
        var reloj = new RelojQuieto();

        var primera = ChatArchive.Open(_carpeta, "hablando", reloj);
        primera.Note(ChatVoice.Persona, "la primera");
        primera.Close();

        var segunda = ChatArchive.Open(_carpeta, "hablando", reloj);
        segunda.Note(ChatVoice.Persona, "la segunda");
        segunda.Close();

        var archivos = Directory.GetFiles(_carpeta, "*.md");
        Assert.Equal(2, archivos.Length);
        Assert.Contains(archivos, a => File.ReadAllText(a).Contains("la primera", StringComparison.Ordinal));
        Assert.Contains(archivos, a => File.ReadAllText(a).Contains("la segunda", StringComparison.Ordinal));
    }

    [Fact]
    public void CerrarDosVeces_NoRompeNada()
    {
        var charla = ChatArchive.Open(_carpeta, "hablando");
        charla.Note(ChatVoice.Persona, "algo");
        charla.Close();
        charla.Close();

        Assert.Single(Directory.GetFiles(_carpeta, "*.md"));
    }

    [Fact]
    public void AnotarDespuesDeCerrar_NoTira()
    {
        var charla = ChatArchive.Open(_carpeta, "hablando");
        charla.Note(ChatVoice.Persona, "algo");
        charla.Close();

        charla.Note(ChatVoice.Ella, "tarde");

        Assert.Equal(1, charla.Turns);
    }

    /// <summary>
    /// Una clave dicha en la charla no llega al disco.
    /// </summary>
    /// <remarks>
    /// <b>Guardar charlas es lo que obliga a esto.</b> Mientras no se guardaba nada de lo hablado no
    /// había dónde meter una credencial; ahora sí, y el contrato de privacidad que se le muestra al
    /// usuario sigue diciendo que no se guardan. Lo que sostiene esa línea es este tapado.
    /// </remarks>
    [Fact]
    public void UnaClaveDichaEnLaCharla_NoLlegaAlDisco()
    {
        var clave = "sk-or-v1-" + new string('a', 40);

        using (var charla = ChatArchive.Open(_carpeta, "escribiendo"))
        {
            charla.Note(ChatVoice.Persona, $"guardá esta clave: {clave}");
            charla.Note(ChatVoice.Persona, "y la contraseña es hunter2sombrero");
            charla.Close();
        }

        var texto = File.ReadAllText(Directory.GetFiles(_carpeta, "*.md").Single());

        Assert.DoesNotContain(clave, texto, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2sombrero", texto, StringComparison.Ordinal);
        Assert.Contains("credencial", texto, StringComparison.Ordinal);

        // Y lo que no es la clave sigue estando: tapar no es tirar la charla.
        Assert.Contains("guardá esta clave", texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// La nota de cierre no salva del borrado a una charla en la que no se dijo nada.
    /// </summary>
    /// <remarks>
    /// <b>Es la secuencia exacta del llamador, y ahí estaba el defecto.</b> Quien cierra anota
    /// «— se cerró la charla —» y recién después llama a Close(). Mientras esa nota contó como
    /// turno, el contador nunca valía cero y la rama que borra no corría NUNCA: en la máquina del
    /// usuario había quedado un archivo de 114 bytes con la cabecera y ese único renglón, de una vez
    /// que tocó el orbe y volvió a tocarlo sin decir nada.
    /// <para>
    /// Las pruebas de Close() estaban bien y no alcanzaban: probaban Close() aislado, nunca la
    /// secuencia con la que se lo llama de verdad.
    /// </para>
    /// </remarks>
    [Fact]
    public void LaNotaDeCierre_NoCuentaComoTurnoNiSalvaElArchivo()
    {
        var charla = ChatArchive.Open(_carpeta, "escribiendo");

        charla.Note(ChatVoice.Nota, "— se cerró la charla —");
        Assert.Equal(0, charla.Turns);

        charla.Close();

        Assert.Empty(Directory.GetFiles(_carpeta, "*.md"));
    }

    /// <summary>Con algo dicho, la nota de cierro sí se escribe y el archivo queda.</summary>
    [Fact]
    public void ConAlgoDicho_LaNotaDeCierreSeEscribeYElArchivoQueda()
    {
        var charla = ChatArchive.Open(_carpeta, "hablando");
        charla.Note(ChatVoice.Persona, "qué hora es");
        charla.Note(ChatVoice.Nota, "— se cerró la charla —");
        charla.Close();

        var texto = File.ReadAllText(Directory.GetFiles(_carpeta, "*.md").Single());

        Assert.Equal(1, charla.Turns);
        Assert.Contains("se cerró la charla", texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// Soltar la charla no puede tumbar el proceso aunque el hilo escritor siga parado.
    /// </summary>
    /// <remarks>
    /// Close() espera dos segundos y se rinde. Si Dispose() le desechaba la cola igual, el hilo
    /// escritor —parado esperando— se comía una ObjectDisposedException que no atrapaba nadie, y una
    /// excepción sin dueño en un hilo propio termina el proceso entero.
    /// </remarks>
    [Fact]
    public void SoltarLaCharla_NoTumbaNada()
    {
        var charla = ChatArchive.Open(_carpeta, "escribiendo");
        charla.Note(ChatVoice.Persona, "algo");

        charla.Dispose();
        charla.Dispose();

        // Y anotar después de soltarla tampoco.
        charla.Note(ChatVoice.Ella, "tarde");
    }

    /// <summary>
    /// Lee sin pelearle al hilo que escribe.
    /// </summary>
    /// <remarks>
    /// Con la charla abierta el archivo lo tiene tomado el hilo que escribe, y un lector exclusivo
    /// choca. No es un detalle de la prueba: es cómo lo va a leer el usuario, que puede abrir la
    /// carpeta en el medio de una conversación.
    /// </remarks>
    private static string Leer(string archivo)
    {
        if (!File.Exists(archivo))
        {
            return string.Empty;
        }

        try
        {
            using var flujo = new FileStream(
                archivo,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var lector = new StreamReader(flujo);
            return lector.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Un reloj que nunca se mueve, para forzar el choque de nombres.</summary>
    private sealed class RelojQuieto : TimeProvider
    {
        private readonly DateTimeOffset _ahora = new(2026, 8, 20, 3, 12, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _ahora;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
