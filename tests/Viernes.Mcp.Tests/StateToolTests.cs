using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// La foto de ahora: lo primero que va a pedir Claude para ubicarse.
/// </summary>
public sealed class StateToolTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public async Task Estado_PoneAdelanteLoQueEstaEsperandoAlUsuario()
    {
        await this.harness.Connector.CreateMissionAsync("Seguir Flow-Bi", "Avisar cuando esté");
        await this.harness.Connector.AskInMissionAsync("m1", "¿Migro los tests o los reescribo?");
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: true);

        var reply = await this.harness.Connector.DescribeStateAsync();

        Assert.True(reply.Ok);
        Assert.Contains("ESPERANDO AL USUARIO (1)", reply.Text, StringComparison.Ordinal);
        Assert.Contains("¿Migro los tests o los reescribo?", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Claude Code: 1 trabajando", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estado_ConTodoVacio_LoDiceSinAdornos()
    {
        var reply = await this.harness.Connector.DescribeStateAsync();

        Assert.True(reply.Ok);
        Assert.Contains("Misiones: ninguna abierta.", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Nada está esperando al usuario.", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Claude Code: no hay sesiones.", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estado_CuentaElGastoYAvisaSiNoSeEstaGuardando()
    {
        var reply = await this.harness.Connector.DescribeStateAsync();

        // El libro de la prueba es en memoria; el aviso existe para que nadie lea un cero como
        // «no gastaste nada» cuando en realidad es «no lo estoy anotando».
        Assert.Contains("· Gasto: US$ 0.00 hoy en 0 pedidos", reply.Text, StringComparison.Ordinal);
        Assert.Contains("no se está guardando en disco", reply.Text, StringComparison.Ordinal);
    }
}
