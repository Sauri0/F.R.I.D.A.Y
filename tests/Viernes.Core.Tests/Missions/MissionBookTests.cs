using Viernes.Core.Missions;
using Xunit;

namespace Viernes.Core.Tests.Missions;

/// <summary>
/// Lo que tiene que aguantar una misión: sobrevivir al reinicio con su pregunta puesta.
/// </summary>
/// <remarks>
/// Es la diferencia entre un asistente que sigue algo y uno que promete y se olvida. Si la pregunta
/// pendiente no cruza el apagado, la misión queda trabada para siempre sin que nadie se entere:
/// el usuario cree que está avanzando y el asistente cree que está esperando.
/// </remarks>
public sealed class MissionBookTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), $"misiones-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task CrearYAvanzar_DejaLaMisionEnCurso()
    {
        var book = new MissionBook(this.path);

        var mission = await book.CreateAsync("Seguir el proyecto", "Avisarme cuando termine");
        await book.AdvanceAsync(mission.Id, "Revisé el repo");

        var open = await book.ListAsync();
        var stored = Assert.Single(open);
        Assert.Equal(MissionState.EnCurso, stored.State);
        Assert.Equal("Revisé el repo", stored.Log[^1].Text);
    }

    [Fact]
    public async Task LaPreguntaPendienteSobreviveAlReinicio()
    {
        var antes = new MissionBook(this.path);
        var mission = await antes.CreateAsync("Seguir el tablero", "Avisarme");
        await antes.AskAsync(mission.Id, "¿Migro los tests o los reescribo?");

        // Un libro nuevo sobre el mismo archivo es exactamente lo que pasa al reiniciar la máquina.
        var despues = new MissionBook(this.path);
        var recovered = Assert.Single(await despues.ListAsync());

        Assert.Equal(MissionState.Esperando, recovered.State);
        Assert.Equal("¿Migro los tests o los reescribo?", recovered.Question);
    }

    [Fact]
    public async Task Responder_DestrabaYDejaConstanciaDeLoQueDijiste()
    {
        var book = new MissionBook(this.path);
        var mission = await book.CreateAsync("Seguir el tablero", "Avisarme");
        await book.AskAsync(mission.Id, "¿Migro o reescribo?");

        var answered = await book.AnswerAsync(mission.Id, "Reescribilos");

        Assert.NotNull(answered);
        Assert.Equal(MissionState.EnCurso, answered.State);
        Assert.Null(answered.Question);
        Assert.Contains("Reescribilos", answered.Log[^1].Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Hablando nadie dice «m3»: dice «lo del informe», o directamente contesta la pregunta sin
    /// nombrar nada. Si hay una sola misión frenada, es ésa: es la única que puede destrabarse.
    /// </remarks>
    [Fact]
    public async Task SinDecirElId_CaeSobreLaUnicaQueEstaEsperando()
    {
        var book = new MissionBook(this.path);
        var otra = await book.CreateAsync("Revisar mails", "Todos los días");
        await book.AdvanceAsync(otra.Id, "Revisados");
        var frenada = await book.CreateAsync("Seguir el tablero", "Avisarme");
        await book.AskAsync(frenada.Id, "¿Migro o reescribo?");

        var answered = await book.AnswerAsync(string.Empty, "Reescribilos");

        Assert.NotNull(answered);
        Assert.Equal(frenada.Id, answered.Id);
    }

    [Fact]
    public async Task NeedingAttention_SoloLoFrenadoYLoVencido()
    {
        var book = new MissionBook(this.path);
        var trabajando = await book.CreateAsync("En curso", "x");
        await book.AdvanceAsync(trabajando.Id, "avanzando");
        var frenada = await book.CreateAsync("Frenada", "x");
        await book.AskAsync(frenada.Id, "¿y?");

        var attention = await book.NeedingAttentionAsync(DateTimeOffset.Now);

        // Que algo esté avanzando no es noticia. Avisar de eso es cómo un asistente proactivo se
        // vuelve ruido de fondo.
        var single = Assert.Single(attention);
        Assert.Equal(frenada.Id, single.Id);
    }

    [Fact]
    public async Task Cerrar_LaSacaDeLasAbiertas()
    {
        var book = new MissionBook(this.path);
        var mission = await book.CreateAsync("Algo", "x");

        await book.CloseAsync(mission.Id, "Quedó hecho");

        Assert.Empty(await book.ListAsync(onlyOpen: true));
        Assert.Single(await book.ListAsync(onlyOpen: false));
    }

    [Fact]
    public async Task DescribeOpen_NombraLaPreguntaSinContestar()
    {
        var book = new MissionBook(this.path);
        var mission = await book.CreateAsync("Seguir el tablero", "Avisarme");
        await book.AskAsync(mission.Id, "¿Migro o reescribo?");

        var described = await book.DescribeOpenAsync();

        Assert.NotNull(described);
        Assert.Contains("¿Migro o reescribo?", described, StringComparison.Ordinal);
        Assert.Contains(mission.Id, described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SinMisiones_NoInyectaNada() =>
        Assert.Null(await new MissionBook(this.path).DescribeOpenAsync());

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }
}
