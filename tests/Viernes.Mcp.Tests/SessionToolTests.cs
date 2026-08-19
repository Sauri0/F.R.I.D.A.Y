using Xunit;

namespace Viernes.Mcp.Tests;

/// <summary>
/// Ver las sesiones de Claude Code, y el intento honesto de escribirles.
/// </summary>
public sealed class SessionToolTests : IDisposable
{
    private readonly ConnectorHarness harness = new();

    public void Dispose() => this.harness.Dispose();

    [Fact]
    public void Listar_SeparaLaQueTrabajaDeLaQueEspera()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false, said: "quedó el paso 3");
        this.harness.WriteSession("C:\\proyectos\\Beta", "beta", working: true);

        var reply = this.harness.Connector.ListSessions();

        Assert.True(reply.Ok);
        Assert.Contains("1 de 2 están esperando", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Alfa", reply.Text, StringComparison.Ordinal);
        Assert.Contains("quedó el paso 3", reply.Text, StringComparison.Ordinal);
        Assert.Contains("está trabajando", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Listar_SinSesiones_EsUnaRespuestaYNoUnFallo()
    {
        var reply = this.harness.Connector.ListSessions();

        Assert.True(reply.Ok);
        Assert.Contains("No encontré ninguna sesión", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escribir_NoEscribe_YDevuelveElMensajeParaQueLoPegueElUsuario()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false);
        await this.harness.Autonomy.LearnAsync(
            "enviar mensaje a claude code", "*", Core.Autonomy.AutonomyLevel.Automatico);

        var reply = await this.harness.Connector.WriteToSessionAsync("Alfa", "seguí con el paso 3");

        // Falla aunque el permiso esté dado: no es un problema de autorización sino de que no hay
        // por dónde. Confundir las dos cosas haría que el usuario dé un permiso que no sirve.
        Assert.False(reply.Ok);
        Assert.Contains("No puedo escribir en la sesión de Claude Code", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("necesita que lo autorices", reply.Text, StringComparison.Ordinal);
        Assert.Contains("C:\\proyectos\\Alfa", reply.Text, StringComparison.Ordinal);
        Assert.Contains("seguí con el paso 3", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escribir_ASesionQueNoExiste_LoDiceYEnumeraLasQueHay()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false);
        await this.harness.Autonomy.LearnAsync(
            "enviar mensaje a claude code", "*", Core.Autonomy.AutonomyLevel.Automatico);

        var reply = await this.harness.Connector.WriteToSessionAsync("Zeta", "hola");

        Assert.False(reply.Ok);
        Assert.Contains("Zeta", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Alfa", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escribir_SinTexto_NoLlegaNiAConsultarPermisos()
    {
        var reply = await this.harness.Connector.WriteToSessionAsync("Alfa", "   ");

        Assert.False(reply.Ok);
        Assert.Contains("Necesito el texto", reply.Text, StringComparison.Ordinal);
    }
}
