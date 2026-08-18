namespace Viernes.Core.Mcp;

/// <summary>
/// La conexión con un servidor MCP, vista como algo que se cae y se levanta, no como algo que
/// existe o no existe.
/// </summary>
/// <remarks>
/// Antes el proveedor conectaba una vez al arrancar y las herramientas se quedaban con el cliente
/// que les tocó. Cuando el proceso de Spotify se moría —y se muere: token vencido, la máquina que
/// duerme, el servidor que se cierra solo— sus treinta herramientas dejaban de funcionar hasta
/// reiniciar Viernes, sin ningún aviso. Para el usuario eso no se lee como «se cayó un servidor»,
/// se lee como que el asistente se volvió tonto.
/// <para>
/// Acá la herramienta puenteada ya no guarda un cliente sino esta conexión, que puede cambiar de
/// sesión por debajo. Las herramientas siguen declaradas aunque el servidor esté caído: el modelo
/// las conoce, y si el servidor volvió, la llamada anda; y si no volvió, contesta que está caído y
/// en cuánto reintenta, que es información útil, en vez de desaparecer.
/// </para>
/// </remarks>
public sealed class McpServerConnection : IAsyncDisposable
{
    /// <summary>Lo que se tolera esperar a que un servidor levante.</summary>
    /// <remarks>
    /// Generoso a propósito: la primera vez, un <c>npx</c> puede tener que bajar el paquete. Que se
    /// acabe el tiempo no es darse por vencido —queda agendado el reintento, y para entonces el
    /// paquete ya está en caché—, es no dejar colgado el arranque del asistente.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>El latido tiene que ser barato; si no contesta rápido, no está.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Un pedido del usuario puede adelantar el reintento, pero no más seguido que esto.
    /// </summary>
    private static readonly TimeSpan UserRetryGap = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Después de tantos fallos seguidos, ni siquiera un pedido del usuario adelanta el intento.
    /// </summary>
    /// <remarks>
    /// Un servidor mal configurado —un ejecutable que no existe— falla siempre. Sin este freno, cada
    /// herramienta que el modelo intentara levantaría un proceso y esperaría el tiempo de conexión:
    /// un turno podría tardar minutos en decir que algo está roto.
    /// </remarks>
    private const int MaximumEagerFailures = 3;

    private readonly Func<CancellationToken, Task<IMcpSession>> _connect;
    private readonly TimeProvider _time;
    private readonly Action<McpConnectionEvent>? _report;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _knownTools = new(StringComparer.Ordinal);

    private IMcpSession? _session;
    private int _consecutiveFailures;
    private bool _hasEverConnected;
    private bool _disposed;
    private DateTimeOffset? _offlineSince;
    private DateTimeOffset _nextAttemptAt;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Arma la conexión sin levantar nada todavía.
    /// </summary>
    /// <remarks>
    /// Recibe cómo conectarse en vez de construirlo adentro: eso es lo que deja probar la caída y la
    /// vuelta sin levantar procesos.
    /// </remarks>
    public McpServerConnection(
        string serverName,
        Func<CancellationToken, Task<IMcpSession>> connect,
        TimeProvider? timeProvider = null,
        Action<McpConnectionEvent>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(connect);

        Name = serverName;
        _connect = connect;
        _time = timeProvider ?? TimeProvider.System;
        _report = report;
    }

    /// <summary>Nombre del servidor, el mismo que prefija sus herramientas.</summary>
    public string Name { get; }

    /// <summary>Si ahora mismo hay una sesión viva.</summary>
    public bool IsOnline => Volatile.Read(ref _session) is not null;

    /// <summary>Desde cuándo está caído, si lo está.</summary>
    public DateTimeOffset? OfflineSince => _offlineSince;

    /// <summary>Fallos seguidos acumulados; es lo que gobierna la espera creciente.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Primer intento de conexión. Devuelve las herramientas que trajo; si no levantó, devuelve
    /// vacío y deja agendado el reintento, porque un servidor caído no puede impedir arrancar.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> StartAsync(
        CancellationToken cancellationToken = default) =>
        await AttemptAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Latido del supervisor: comprueba que siga vivo o, si está caído y ya toca, lo vuelve a
    /// levantar. Devuelve las herramientas que no se conocían antes.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return [];
            }

            if (_session is { } live)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(PingTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        timeout.Token);
                    await live.PingAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await DropSessionUnsafeAsync(live, Describe(exception)).ConfigureAwait(false);
                }

                return [];
            }

            return _time.GetUtcNow() < _nextAttemptAt
                ? []
                : await ConnectUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ejecuta una herramienta del servidor, reconectando primero si hace falta.
    /// </summary>
    /// <exception cref="McpServerUnavailableException">
    /// El servidor está caído y todavía no volvió.
    /// </exception>
    public async Task<McpToolCallOutcome> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var session = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.CallToolAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Se anota la caída y se devuelve el fallo, pero NO se repite la llamada.
            //
            // Reintentarla sola sería lo cómodo y está mal: si el servidor alcanzó a ejecutarla y se
            // cayó devolviendo la respuesta, el reintento la ejecuta dos veces. Mandar dos veces un
            // mensaje o saltear dos canciones es peor que avisar que falló.
            await MarkDownAsync(session, Describe(exception)).ConfigureAwait(false);
            throw new McpServerUnavailableException(Name, RemainingWait());
        }
    }

    private async Task<IMcpSession> AcquireSessionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } live)
            {
                return live;
            }

            // Hay alguien esperando del otro lado, así que se adelanta el reintento agendado —salvo
            // que el servidor ya haya demostrado que no va a volver.
            var now = _time.GetUtcNow();
            var mayTryNow = now >= _nextAttemptAt ||
                (_consecutiveFailures <= MaximumEagerFailures && now - _lastAttemptAt >= UserRetryGap);
            if (!mayTryNow)
            {
                throw new McpServerUnavailableException(Name, RemainingWaitUnsafe(now));
            }

            await ConnectUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _session ?? throw new McpServerUnavailableException(Name, RemainingWait());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<McpToolDescriptor>> AttemptAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _disposed
                ? []
                : await ConnectUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Conecta y lista herramientas. Debe llamarse con el semáforo tomado.</summary>
    private async Task<IReadOnlyList<McpToolDescriptor>> ConnectUnsafeAsync(
        CancellationToken cancellationToken)
    {
        _lastAttemptAt = _time.GetUtcNow();

        IMcpSession session;
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            session = await _connect(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RegisterFailureUnsafe(Describe(exception));
            return [];
        }

        IReadOnlyList<McpToolDescriptor> tools;
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            tools = await session.ListToolsAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SafeDisposeAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            // Un servidor que saluda pero no sabe decir qué hace no sirve; se lo trata como caído.
            await SafeDisposeAsync(session).ConfigureAwait(false);
            RegisterFailureUnsafe(Describe(exception));
            return [];
        }

        var now = _time.GetUtcNow();
        var downtime = _offlineSince is { } since ? now - since : (TimeSpan?)null;
        var recovering = _hasEverConnected;

        _session = session;
        _consecutiveFailures = 0;
        _offlineSince = null;
        _hasEverConnected = true;

        var fresh = new List<McpToolDescriptor>();
        foreach (var tool in tools)
        {
            if (_knownTools.Add(tool.Name))
            {
                fresh.Add(tool);
            }
        }

        Report(new McpConnectionEvent(
            Name,
            recovering ? McpConnectionState.Recuperado : McpConnectionState.Conectado,
            $"herramientas={tools.Count}",
            now.ToLocalTime(),
            recovering ? downtime : null));

        return fresh;
    }

    private async Task MarkDownAsync(IMcpSession failed, string detail)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DropSessionUnsafeAsync(failed, detail).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Baja la sesión que falló. Debe llamarse con el semáforo tomado.</summary>
    private async Task DropSessionUnsafeAsync(IMcpSession failed, string detail)
    {
        // Si otro ya la reemplazó mientras esperábamos el semáforo, la sesión nueva no tiene la
        // culpa de lo que le pasó a la vieja.
        if (!ReferenceEquals(_session, failed))
        {
            return;
        }

        _session = null;
        await SafeDisposeAsync(failed).ConfigureAwait(false);
        RegisterFailureUnsafe(detail);
    }

    /// <summary>Anota el fallo y agenda el próximo intento. Con el semáforo tomado.</summary>
    private void RegisterFailureUnsafe(string detail)
    {
        var now = _time.GetUtcNow();
        _consecutiveFailures++;
        _nextAttemptAt = now + McpRetrySchedule.DelayFor(_consecutiveFailures);

        // Sólo se anota el principio de la caída. Anotar cada reintento llenaría el registro de
        // ruido y taparía justo lo que interesa: cuándo se cayó y cuándo volvió.
        if (_offlineSince is not null)
        {
            return;
        }

        _offlineSince = now;
        Report(new McpConnectionEvent(
            Name,
            _hasEverConnected ? McpConnectionState.Caido : McpConnectionState.NoLevanto,
            detail,
            now.ToLocalTime()));
    }

    private TimeSpan RemainingWait() => RemainingWaitUnsafe(_time.GetUtcNow());

    private TimeSpan RemainingWaitUnsafe(DateTimeOffset now)
    {
        var remaining = _nextAttemptAt - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void Report(McpConnectionEvent connectionEvent)
    {
        try
        {
            _report?.Invoke(connectionEvent);
        }
        catch (Exception)
        {
            // Anotar lo que pasó no puede ser el motivo de que además se rompa otra cosa.
        }
    }

    private static async Task SafeDisposeAsync(IMcpSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cerrar un proceso que ya se murió no puede impedir levantar el que lo reemplaza.
        }
    }

    /// <summary>El mensaje suele ser inútil; el tipo dice más de qué clase de caída fue.</summary>
    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {exception.Message}";

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_session is { } live)
            {
                _session = null;
                await SafeDisposeAsync(live).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }
}
