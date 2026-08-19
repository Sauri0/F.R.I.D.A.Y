using Viernes.Core.Autonomy;
using Viernes.Core.Missions;
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
    public async Task CancelarConMotivo_NoEscribeLaBitacoraSiAvanzarEstaProhibido()
    {
        // Cancelar con motivo escribe DOS cosas y consultaba por una sola: la línea de la bitácora
        // la escribe AdvanceAsync, o sea «mision avanzar», y entraba igual con ese permiso en Nunca.
        await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");
        var antes = (await this.harness.Missions.ListAsync(onlyOpen: false))[0].Log.Count;
        await this.harness.Autonomy.LearnAsync("mision avanzar", "*", AutonomyLevel.Nunca);

        var reply = await this.harness.Connector.CloseMissionAsync(
            "m1", "El cliente frenó el proyecto", cancelled: true);

        // Cerrar sí estaba permitido, así que la misión queda cancelada; lo que no entró es la línea.
        Assert.True(reply.Ok);

        var stored = Assert.Single(await this.harness.Missions.ListAsync(onlyOpen: false));
        Assert.Equal(MissionState.Cancelada, stored.State);
        Assert.Equal(antes, stored.Log.Count);
        Assert.DoesNotContain(
            stored.Log,
            entry => entry.Text.Contains("El cliente frenó el proyecto", StringComparison.Ordinal));

        // Y no se calla que faltó: un permiso respetado en silencio se lee igual que uno ignorado.
        Assert.Contains("NO quedó escrito en la bitácora", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CerrarConMotivoSinCancelar_TampocoEscribeLaBitacoraSiAvanzarEstaProhibido()
    {
        // La misma falla por la otra puerta. El arreglo anterior cubrió la rama de cancelar, que
        // escribe con AdvanceAsync; la de cerrar escribe la nota adentro de CloseAsync, y seguía
        // consultando sólo por «mision cerrar». Dos caminos, el mismo efecto: una línea en la
        // bitácora bajo un permiso que no es el suyo.
        await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");
        var antes = (await this.harness.Missions.ListAsync(onlyOpen: false))[0].Log.Count;
        await this.harness.Autonomy.LearnAsync("mision avanzar", "*", AutonomyLevel.Nunca);

        var reply = await this.harness.Connector.CloseMissionAsync(
            "m1", "Salió andando en producción", cancelled: false);

        // Cerrar estaba permitido: la misión se cierra igual. Lo que no entra es el renglón.
        Assert.True(reply.Ok);

        var stored = Assert.Single(await this.harness.Missions.ListAsync(onlyOpen: false));
        Assert.Equal(MissionState.Terminada, stored.State);
        Assert.Equal(antes, stored.Log.Count);
        Assert.DoesNotContain(
            stored.Log,
            entry => entry.Text.Contains("Salió andando en producción", StringComparison.Ordinal));

        Assert.Contains("no quedó anotado", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CerrarConMotivo_SiAvanzarEstaPermitido_SiDejaLaNota()
    {
        // El otro lado de la prueba de arriba: sin permiso prohibido, la nota tiene que entrar. Sin
        // esto, «no escribe la bitácora» pasaría también si el arreglo la hubiera roto para todos.
        await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");

        var reply = await this.harness.Connector.CloseMissionAsync(
            "m1", "Salió andando en producción", cancelled: false);

        Assert.True(reply.Ok);
        var stored = Assert.Single(await this.harness.Missions.ListAsync(onlyOpen: false));
        Assert.Equal(MissionState.Terminada, stored.State);
        Assert.Contains(
            stored.Log,
            entry => entry.Text.Contains("Salió andando en producción", StringComparison.Ordinal));
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
