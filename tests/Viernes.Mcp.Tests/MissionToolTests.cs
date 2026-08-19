using Viernes.Core.Missions;
using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// Las misiones vistas desde el conector: que Claude pueda abrirlas, moverlas y cerrarlas sobre el
/// mismo archivo que mira el orbe.
/// </summary>
public sealed class MissionToolTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public async Task Crear_DejaLaMisionEnElMismoLibroQueLeeLaAplicacion()
    {
        var reply = await this.harness.Connector.CreateMissionAsync(
            "Seguir el tablero", "Avisar cuando el informe esté", "C:\\proyectos\\tablero");

        Assert.True(reply.Ok);

        var stored = Assert.Single(await this.harness.Missions.ListAsync());
        Assert.Equal("Seguir el tablero", stored.Title);
        Assert.Equal("Avisar cuando el informe esté", stored.Goal);
        Assert.Equal("C:\\proyectos\\tablero", stored.Context);
    }

    [Fact]
    public async Task Listar_CuentaQueFaltaYDesdeCuando()
    {
        await this.harness.Connector.CreateMissionAsync("Seguir el tablero", "Avisar cuando esté");
        await this.harness.Connector.AdvanceMissionAsync("m1", "Revisé el repo");

        var reply = await this.harness.Connector.ListMissionsAsync();

        Assert.True(reply.Ok);
        Assert.Contains("[m1] Seguir el tablero", reply.Text, StringComparison.Ordinal);
        Assert.Contains("se cumple cuando: Avisar cuando esté", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Revisé el repo", reply.Text, StringComparison.Ordinal);
        Assert.Contains("abierta desde", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preguntar_DejaLaPreguntaEsperandoAlUsuario()
    {
        await this.harness.Connector.CreateMissionAsync("Seguir el tablero", "Avisar cuando esté");

        var reply = await this.harness.Connector.AskInMissionAsync("m1", "¿Migro los tests o los reescribo?");

        Assert.True(reply.Ok);

        // Lo que importa no es el texto de la respuesta sino que la pregunta quedó guardada: es lo
        // que va a seguir estando mañana, cuando esta conversación con Claude ya no exista.
        var stored = Assert.Single(await this.harness.Missions.ListAsync());
        Assert.Equal(MissionState.Esperando, stored.State);
        Assert.Equal("¿Migro los tests o los reescribo?", stored.Question);
    }

    [Fact]
    public async Task Cerrar_Cancelada_DejaElMotivoEscrito()
    {
        await this.harness.Connector.CreateMissionAsync("Seguir el tablero", "Avisar cuando esté");

        var reply = await this.harness.Connector.CloseMissionAsync(
            "m1", "El cliente frenó el proyecto", cancelled: true);

        Assert.True(reply.Ok);

        var stored = Assert.Single(await this.harness.Missions.ListAsync(onlyOpen: false));
        Assert.Equal(MissionState.Cancelada, stored.State);
        Assert.Contains("El cliente frenó el proyecto", stored.Log[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Avanzar_SobreAlgoQueNoExiste_EsUnFalloYNoUnaRespuesta()
    {
        var reply = await this.harness.Connector.AdvanceMissionAsync("m9", "algo");

        Assert.False(reply.Ok);
        Assert.Contains("m9", reply.Text, StringComparison.Ordinal);
        Assert.Empty(await this.harness.Missions.ListAsync(onlyOpen: false));
    }

    [Fact]
    public async Task Crear_SinTitulo_NoInventaNada()
    {
        var reply = await this.harness.Connector.CreateMissionAsync("   ");

        Assert.False(reply.Ok);
        Assert.Empty(await this.harness.Missions.ListAsync(onlyOpen: false));
    }
}
