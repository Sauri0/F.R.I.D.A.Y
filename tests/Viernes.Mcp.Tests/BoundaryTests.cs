using Viernes.Core.Autonomy;
using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// La frontera: que conectar un servidor no sea la forma de saltearse los permisos.
/// </summary>
/// <remarks>
/// Es la prueba que más importa de todo el conector. Un servidor MCP corre sin nadie mirando; si una
/// acción marcada como «preguntar» se ejecutara igual, la política de autonomía valdría exactamente
/// hasta que alguien agregue un conector, y el usuario no tendría cómo enterarse.
/// <para>
/// Por eso las afirmaciones no miran el texto de la negativa sino <em>que el efecto no ocurrió</em>:
/// una herramienta que contesta «no tenés permiso» y guarda igual pasaría una prueba de texto.
/// </para>
/// </remarks>
public sealed class BoundaryTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public async Task LoQueLaPoliticaMarcaComoPreguntar_NoSeEjecuta()
    {
        await this.harness.Autonomy.LearnAsync(
            "mision", "*", AutonomyLevel.Preguntar, "Las misiones las abro yo");

        var reply = await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");

        Assert.False(reply.Ok);
        Assert.Contains("necesita que lo autorices", reply.Text, StringComparison.Ordinal);
        Assert.Empty(await this.harness.Missions.ListAsync(onlyOpen: false));
    }

    [Fact]
    public async Task LoQueLaPoliticaMarcaComoNunca_NoSeEjecuta()
    {
        await this.harness.Autonomy.LearnAsync("memoria proponer", "*", AutonomyLevel.Nunca);

        var reply = await this.harness.Connector.ProposeMemoryAsync("Toma el mate amargo");

        Assert.False(reply.Ok);
        Assert.Contains("nunca", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await this.harness.Memory.ReviewAsync()).Suggestions);
    }

    [Fact]
    public async Task EscribirEnOtraSesion_PideAutorizacionSinQueNadieConfigureNada()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false);

        // Sin ninguna regla escrita: la política ya considera consecuente todo lo que empiece con
        // «enviar», así que la primera vez que Claude quiera hablarle a otra sesión, pregunta.
        var reply = await this.harness.Connector.WriteToSessionAsync("Alfa", "seguí con el paso 3");

        Assert.False(reply.Ok);
        Assert.Contains("necesita que lo autorices", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnPermisoSobreUnaMisionEnParticular_NoFrenaLasDemas()
    {
        await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");
        await this.harness.Connector.CreateMissionAsync("Revisar el banco", "Que mida bien");
        await this.harness.Autonomy.LearnAsync("mision cerrar", "flow-bi", AutonomyLevel.Preguntar);

        var frenada = await this.harness.Connector.CloseMissionAsync("Flow-Bi", "listo");
        var libre = await this.harness.Connector.CloseMissionAsync("banco", "listo");

        Assert.False(frenada.Ok);
        Assert.True(libre.Ok);

        var abiertas = await this.harness.Missions.ListAsync();
        Assert.Equal("Seguir Flow-Bi", Assert.Single(abiertas).Title);
    }

    [Fact]
    public async Task LasAccionesDeSoloLectura_NoPasanPorLosPermisos()
    {
        // Un «nunca» que alcanza a todo. Leer tiene que seguir andando: pedir permiso para leer es
        // lo que convierte a un asistente en un trámite, y es la misma regla que ya sigue la app.
        await this.harness.Autonomy.LearnAsync("*", "*", AutonomyLevel.Nunca);

        var misiones = await this.harness.Connector.ListMissionsAsync();
        var memoria = await this.harness.Connector.SearchMemoryAsync();
        var sesiones = this.harness.Connector.ListSessions();
        var estado = await this.harness.Connector.DescribeStateAsync();

        Assert.True(misiones.Ok);
        Assert.True(memoria.Ok);
        Assert.True(sesiones.Ok);
        Assert.True(estado.Ok);
    }
}
