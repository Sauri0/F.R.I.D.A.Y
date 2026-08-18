using System.Text.Json;
using Viernes.Core.Mcp;
using Viernes.Core.Tools;
using Xunit;

namespace Viernes.Core.Tests.Mcp;

/// <summary>
/// Lo que se prueba acá es la caída y la vuelta, que es lo que no había: el proveedor conectaba una
/// vez al arrancar y un servidor muerto se llevaba sus herramientas hasta reiniciar la aplicación.
/// </summary>
public sealed class McpReconnectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Un_servidor_caido_no_borra_sus_herramientas()
    {
        var server = new FakeMcpServer("spotify", "play", "pause");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);
        Assert.Equal(2, provider.Tools.Count);

        server.Kill();
        var result = await ExecuteAsync(provider, "spotify_play");

        // La herramienta sigue declarada: el modelo tiene que saber que existe para poder decir que
        // el servicio está caído, en vez de contestar que no sabe hacer eso.
        Assert.Equal(2, provider.Tools.Count);
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("caído", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Vuelve_sola_cuando_el_servidor_revive()
    {
        var server = new FakeMcpServer("spotify", "play");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);

        server.Kill();
        Assert.Equal(ToolExecutionStatus.Failed, (await ExecuteAsync(provider, "spotify_play")).Status);

        server.Revive();
        time.Advance(TimeSpan.FromSeconds(30));
        await provider.HeartbeatAsync();

        var result = await ExecuteAsync(provider, "spotify_play");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(2, server.ConnectAttempts);
    }

    [Fact]
    public async Task El_latido_se_entera_de_la_caida_sin_que_nadie_pida_nada()
    {
        var server = new FakeMcpServer("spotify", "play");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);

        server.Kill();
        await provider.HeartbeatAsync();

        Assert.Contains(provider.History, entry => entry.State == McpConnectionState.Caido);
    }

    [Fact]
    public async Task Queda_anotado_cuando_se_cayo_y_cuanto_estuvo_caido()
    {
        var server = new FakeMcpServer("spotify", "play");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);

        server.Kill();
        await provider.HeartbeatAsync();

        server.Revive();
        time.Advance(TimeSpan.FromMinutes(11));
        await provider.HeartbeatAsync();

        var fall = provider.History.Single(entry => entry.State == McpConnectionState.Caido);
        var back = provider.History.Single(entry => entry.State == McpConnectionState.Recuperado);

        Assert.Equal("spotify", fall.Server);
        Assert.Equal(TimeSpan.FromMinutes(11), back.Downtime);
    }

    [Fact]
    public async Task No_repite_la_llamada_que_se_cayo_a_la_mitad()
    {
        var server = new FakeMcpServer("correo", "enviar");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);

        server.Kill();
        await ExecuteAsync(provider, "correo_enviar");

        // Reintentar sola una llamada que ya viajó mandaría dos veces el mismo mensaje. Se cuenta el
        // intento que llegó al servidor: tiene que ser exactamente uno.
        Assert.Equal(1, server.CallAttempts);
    }

    [Fact]
    public async Task Un_servidor_que_no_levanta_no_impide_a_los_demas()
    {
        var roto = new FakeMcpServer("roto", "nada");
        roto.Kill();
        var bueno = new FakeMcpServer("bueno", "hacer");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        var tools = await provider.ConnectSessionsAsync(
            [(roto.Name, roto.ConnectAsync), (bueno.Name, bueno.ConnectAsync)],
            timeProvider: time);

        Assert.Single(tools);
        Assert.Equal("bueno_hacer", tools[0].Definition.Name);
        Assert.Contains(provider.Failures, failure => failure.StartsWith("roto:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_servidor_que_levanta_tarde_avisa_sus_herramientas()
    {
        var server = new FakeMcpServer("spotify", "play", "pause");
        server.Kill();
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        McpToolsRecoveredEventArgs? recovered = null;
        provider.ToolsRecovered += (_, args) => recovered = args;

        var tools = await provider.ConnectSessionsAsync(
            [(server.Name, server.ConnectAsync)],
            timeProvider: time);
        Assert.Empty(tools);

        server.Revive();
        time.Advance(TimeSpan.FromSeconds(30));
        await provider.HeartbeatAsync();

        Assert.NotNull(recovered);
        Assert.Equal(2, recovered.Tools.Count);
        Assert.Equal(2, provider.Tools.Count);
    }

    [Fact]
    public async Task No_duplica_las_herramientas_al_reconectar()
    {
        var server = new FakeMcpServer("spotify", "play", "pause");
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);

        for (var round = 0; round < 3; round++)
        {
            server.Kill();
            await provider.HeartbeatAsync();
            server.Revive();
            time.Advance(TimeSpan.FromMinutes(6));
            await provider.HeartbeatAsync();
        }

        Assert.Equal(2, provider.Tools.Count);
    }

    [Fact]
    public async Task No_levanta_un_proceso_por_cada_intento_mientras_espera()
    {
        var server = new FakeMcpServer("roto", "nada");
        server.Kill();
        var time = new ManualTimeProvider(Start);
        await using var provider = new McpToolProvider();

        await provider.ConnectSessionsAsync([(server.Name, server.ConnectAsync)], timeProvider: time);
        var afterStart = server.ConnectAttempts;

        // Diez latidos seguidos sin que pase el tiempo: la espera creciente los tiene que absorber.
        for (var beat = 0; beat < 10; beat++)
        {
            await provider.HeartbeatAsync();
        }

        Assert.Equal(afterStart, server.ConnectAttempts);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    public void La_espera_crece_al_doble(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), McpRetrySchedule.DelayFor(failures));
    }

    [Fact]
    public void La_espera_tiene_techo()
    {
        Assert.Equal(McpRetrySchedule.MaximumDelay, McpRetrySchedule.DelayFor(50));
        Assert.Equal(McpRetrySchedule.MaximumDelay, McpRetrySchedule.DelayFor(int.MaxValue));
    }

    private static async Task<ToolExecutionResult> ExecuteAsync(McpToolProvider provider, string toolName)
    {
        var tool = provider.Tools.Single(candidate => candidate.Definition.Name == toolName);
        return await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { }),
            new ToolExecutionContext("call-1"));
    }
}

/// <summary>
/// Un servidor MCP que se puede matar y revivir a voluntad, sin levantar ningún proceso.
/// </summary>
internal sealed class FakeMcpServer
{
    private readonly List<McpToolDescriptor> _tools;
    private bool _running = true;

    public FakeMcpServer(string name, params string[] toolNames)
    {
        Name = name;
        _tools = [.. toolNames.Select(toolName => new McpToolDescriptor(
            toolName,
            $"Herramienta {toolName}",
            JsonSerializer.SerializeToElement(new { type = "object" })))];
    }

    public string Name { get; }

    public int ConnectAttempts { get; private set; }

    /// <summary>Cuántas llamadas llegaron al servidor, hayan salido bien o mal.</summary>
    public int CallAttempts { get; private set; }

    public void Kill() => _running = false;

    public void Revive() => _running = true;

    public Task<IMcpSession> ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        return _running
            ? Task.FromResult<IMcpSession>(new FakeSession(this))
            : Task.FromException<IMcpSession>(new IOException("el proceso no está"));
    }

    private void EnsureRunning()
    {
        if (!_running)
        {
            throw new IOException("el proceso se murió");
        }
    }

    private sealed class FakeSession : IMcpSession
    {
        private readonly FakeMcpServer _server;

        public FakeSession(FakeMcpServer server) => _server = server;

        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default)
        {
            _server.EnsureRunning();
            return Task.FromResult<IReadOnlyList<McpToolDescriptor>>([.. _server._tools]);
        }

        public Task<McpToolCallOutcome> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
        {
            _server.CallAttempts++;
            _server.EnsureRunning();
            return Task.FromResult(new McpToolCallOutcome(false, $"hecho: {toolName}"));
        }

        public Task PingAsync(CancellationToken cancellationToken = default)
        {
            _server.EnsureRunning();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtcNow.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
}
