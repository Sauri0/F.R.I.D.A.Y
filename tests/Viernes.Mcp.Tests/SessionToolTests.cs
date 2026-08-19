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
        Assert.Contains("está trabajando", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Listar_NoSacaLoQueDijoElAsistente_SinQueNadieLoPida()
    {
        // Son cientos de caracteres de la conversación de otro proyecto del usuario, y de un vistazo
        // a la lista salían los de todas las sesiones de la máquina. Se sigue viendo QUE está
        // esperando, que es lo que la herramienta promete; lo que dijo, no.
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false, said: "quedó el paso 3");

        var reply = this.harness.Connector.ListSessions();

        Assert.True(reply.Ok);
        Assert.Contains("te está esperando", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("quedó el paso 3", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Listar_ConElArgumentoExplicito_SiLoSaca()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false, said: "quedó el paso 3");

        var reply = this.harness.Connector.ListSessions(includeLastMessage: true);

        Assert.True(reply.Ok);
        Assert.Contains("quedó el paso 3", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Listar_SePuedeAcotarAUnProyecto()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false);
        this.harness.WriteSession("C:\\proyectos\\Beta", "beta", working: true);

        var reply = this.harness.Connector.ListSessions(project: "Beta");

        Assert.True(reply.Ok);
        Assert.Contains("Beta", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alfa", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Listar_AcotadoAAlgoQueNoEsta_LoDiceYNoDevuelveElResto()
    {
        this.harness.WriteSession("C:\\proyectos\\Alfa", "alfa", working: false);

        var reply = this.harness.Connector.ListSessions(project: "Zeta");

        Assert.True(reply.Ok);
        Assert.Contains("Zeta", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alfa", reply.Text, StringComparison.Ordinal);
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

        // Identifica la sesión destino sin copiar adentro lo que esa sesión venía diciendo.
        Assert.DoesNotContain("Dijo:", reply.Text, StringComparison.Ordinal);
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
